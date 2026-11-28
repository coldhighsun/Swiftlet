using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;
using Swiftlet.Infrastructure.Sync;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="TrashCleanupWorker"/> with real SQLite repositories, a scripted IMAP session and a virtual clock.
/// </summary>
public sealed class TrashCleanupWorkerTests
{
    [Fact]
    public async Task RunPass_CleanupOff_DoesNotConnect()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(null, 1, ct);

        // Act
        await h.Worker.RunPassAsync(ct);

        // Assert
        h.Factory.ConnectCount.Should().Be(0);
    }

    [Fact]
    public async Task RunPass_ExpiredMessages_DeletesAllBatchesOnOneConnectionAndReportsOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(7, 150, ct);
        await h.Worker.RunPassAsync(ct);
        var reportsBefore = h.Progress.Count;
        h.Time.Advance(TimeSpan.FromDays(7));

        // Act
        await h.Worker.RunPassAsync(ct);

        // Assert
        reportsBefore.Should().Be(0);
        h.Factory.ConnectCount.Should().Be(2);
        h.Factory.Session.Disposed.Should().BeTrue();
        h.Factory.Session.Messages["Trash"].Should().BeEmpty();
        var report = h.Progress.Should().ContainSingle().Which;
        (report.Deleted, report.Finished, report.Error).Should().Be((150, true, null));
    }

    [Fact]
    public async Task RunPass_ServerRefuses_ReportsTheError()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(7, 1, ct);
        await h.Worker.RunPassAsync(ct);
        h.Time.Advance(TimeSpan.FromDays(7));
        h.Factory.Session.DeleteFailure = new NotSupportedException("no UIDPLUS");

        // Act
        await h.Worker.RunPassAsync(ct);

        // Assert
        var report = h.Progress.Should().ContainSingle().Which;
        (report.Error, report.Finished, report.WillRetry).Should().Be(("no UIDPLUS", true, false));
    }

    [Fact]
    public async Task RunPass_ConnectionLostPartWay_ReportsWhatWasDeleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(7, 150, ct);
        await h.Worker.RunPassAsync(ct);
        h.Time.Advance(TimeSpan.FromDays(7));
        var calls = 0;
        h.Factory.Session.OnDeleteAsync = () => ++calls == 2 ? throw new IOException("reset") : Task.CompletedTask;

        // Act
        await h.Worker.RunPassAsync(ct);

        // Assert
        var report = h.Progress.Should().ContainSingle().Which;
        (report.Deleted, report.Finished, report.Error).Should().Be((100, true, null));
    }

    [Fact]
    public async Task RunPass_SettingsChangedAfterConnecting_DeletesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(7, 1, ct);
        await h.Worker.RunPassAsync(ct);
        h.Time.Advance(TimeSpan.FromDays(7));
        h.Factory.CreateSession = _ =>
        {
            h.Revisions.Bump(h.AccountId);
            return h.Factory.Session;
        };

        // Act
        await h.Worker.RunPassAsync(ct);

        // Assert
        h.Factory.Session.DeleteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunPass_ConnectingFails_DoesNotThrow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(7, 1, ct);
        h.Factory.Failure = new OAuthReauthenticationRequiredException("me@example.com");

        // Act
        var pass = () => h.Worker.RunPassAsync(ct);

        // Assert
        await pass.Should().NotThrowAsync();
        h.Progress.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_Started_WaitsBeforeTheFirstPassAndThenRepeats()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(7, 1, ct);
        await h.Worker.StartAsync(ct);
        try
        {
            await WatcherHarness.WaitUntilAsync(() => h.Time.PendingTimerCount == 1, ct);
            var beforeDelay = h.Factory.ConnectCount;

            // Act
            h.Time.Advance(TrashCleanupWorker.InitialDelay);
            await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 1 && h.Time.PendingTimerCount == 1, ct);
            h.Time.Advance(TrashCleanupWorker.Interval);

            // Assert
            await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 2 && h.Time.PendingTimerCount == 1, ct);
            beforeDelay.Should().Be(0);
        }
        finally
        {
            await h.Worker.StopAsync(ct);
        }
    }

    /// <summary>
    /// A worker wired to real repositories over in-memory SQLite.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>
        /// The connection that keeps the in-memory global database alive.
        /// </summary>
        private readonly SqliteConnection _connection;

        /// <summary>
        /// The service provider.
        /// </summary>
        private readonly ServiceProvider _provider;

        /// <summary>
        /// The reports received so far.
        /// </summary>
        private readonly List<DeletionProgressEventArgs> _progress = [];

        /// <summary>
        /// Initializes a new instance of the <see cref="Harness"/> class.
        /// </summary>
        /// <param name="connection">The global database connection.</param>
        /// <param name="provider">The service provider.</param>
        /// <param name="accountId">The account identifier.</param>
        private Harness(SqliteConnection connection, ServiceProvider provider, Guid accountId)
        {
            _connection = connection;
            _provider = provider;
            AccountId = accountId;
            provider.GetRequiredService<SyncEvents>().DeletionProgressed += (_, e) =>
            {
                lock (_progress)
                {
                    _progress.Add(e);
                }
            };
        }

        /// <summary>
        /// Gets the seeded account's identifier.
        /// </summary>
        public Guid AccountId { get; }

        /// <summary>
        /// Gets the worker under test.
        /// </summary>
        public TrashCleanupWorker Worker => _provider.GetRequiredService<TrashCleanupWorker>();

        /// <summary>
        /// Gets the connection factory.
        /// </summary>
        public FakeImapSessionFactory Factory => _provider.GetRequiredService<FakeImapSessionFactory>();

        /// <summary>
        /// Gets the virtual clock.
        /// </summary>
        public VirtualTimeProvider Time => (VirtualTimeProvider)_provider.GetRequiredService<TimeProvider>();

        /// <summary>
        /// Gets the connection settings revisions.
        /// </summary>
        public AccountConnectionRevisions Revisions => _provider.GetRequiredService<AccountConnectionRevisions>();

        /// <summary>
        /// Gets a snapshot of the reports.
        /// </summary>
        public IReadOnlyList<DeletionProgressEventArgs> Progress
        {
            get
            {
                lock (_progress)
                {
                    return [.. _progress];
                }
            }
        }

        /// <summary>
        /// Creates a harness with one account whose Trash holds messages on the server.
        /// </summary>
        /// <param name="retentionDays">How long the account keeps deleted mail; <see langword="null"/> for ever.</param>
        /// <param name="count">The number of messages in Trash.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The harness.</returns>
        public static async Task<Harness> CreateAsync(int? retentionDays, int count, CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(cancellationToken);
            var factory = new FakeImapSessionFactory();
            var time = new VirtualTimeProvider();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(factory);
            services.AddSingleton<IImapSessionFactory>(factory);
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<SyncCoordinator>();
            services.AddSingleton<SyncEvents>();
            services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<InMemoryAccountDbContextFactory>();
            services.AddSingleton<IAccountDbContextFactory>(sp => sp.GetRequiredService<InMemoryAccountDbContextFactory>());
            services.AddScoped<AccountContextProvider>();
            services.AddScoped<IAccountRepository, AccountRepository>();
            services.AddScoped<IFolderRepository, FolderRepository>();
            services.AddScoped<IMessageRepository, MessageRepository>();
            services.AddScoped<TrashCleanupService>();
            services.AddSingleton<AccountConnectionRevisions>();
            services.AddSingleton<TrashCleanupWorker>();
            var provider = services.BuildServiceProvider(validateScopes: true);

            var account = TestData.Account("me@example.com");
            account.ChangeTrashRetention(retentionDays, time.GetUtcNow());
            var trash = MailFolder.Create(account.Id, "Trash", "Trash", FolderRole.Trash);
            trash.UpdateUidValidity(1);
            await using (var scope = provider.CreateAsyncScope())
            {
                await SqliteSchemaInitializer.InitializeGlobalAsync(scope.ServiceProvider.GetRequiredService<GlobalDbContext>(), cancellationToken);
                await scope.ServiceProvider.GetRequiredService<IAccountRepository>().AddAsync(account, cancellationToken);
                var folders = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
                await folders.AddAsync(trash, cancellationToken);
                await folders.SaveChangesAsync(cancellationToken);
            }

            factory.Session.Messages["Trash"] = [.. Enumerable.Range(1, count).Select(i => TestData.Remote((uint)i))];
            return new Harness(connection, provider, account.Id);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
