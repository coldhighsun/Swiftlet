using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// Tests for <see cref="DeletionWorker"/> with real SQLite repositories, a scripted IMAP session and a virtual clock.
/// </summary>
public class DeletionWorkerTests
{
    /// <summary>
    /// Verifies nothing is connected for an account without pending deletions.
    /// </summary>
    [Fact]
    public async Task Reconcile_NothingPending_DoesNotConnect()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(0, ct);

        // Act
        await h.RunAsync(ct);

        // Assert
        h.Factory.ConnectCount.Should().Be(0);
        h.Progress.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies pending deletions (for example left over from the last run) are carried out on one connection and reported.
    /// </summary>
    [Fact]
    public async Task Reconcile_PendingMessages_DeletesAllOnOneConnectionAndReports()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(150, ct);

        // Act
        await h.RunAsync(ct);

        // Assert
        h.Factory.ConnectCount.Should().Be(1);
        h.Factory.Session.Disposed.Should().BeTrue();
        h.Factory.Session.Messages["INBOX"].Should().BeEmpty();
        h.Progress.Select(p => (p.Deleted, p.Remaining, p.Finished)).Should().Equal((100, 50, false), (150, 0, false), (150, 0, true));
    }

    /// <summary>
    /// Verifies a lost connection is retried after 5 and then 10 seconds until the deletion works.
    /// </summary>
    [Fact]
    public async Task Run_ConnectionKeepsDropping_RetriesWithGrowingDelayUntilDone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(1, ct);
        h.Factory.Session.DeleteFailures.Enqueue(new IOException("reset"));
        h.Factory.Session.DeleteFailures.Enqueue(new IOException("reset"));

        // Act
        await h.Worker.ReconcileAsync(ct);
        foreach (var (seconds, attempts) in new[] { (5, 1), (10, 2) })
        {
            await WatcherHarness.WaitUntilAsync(() => h.Factory.Session.DeleteCalls.Count == attempts && h.Time.PendingTimerCount == 1, ct);
            h.Time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            h.Factory.Session.DeleteCalls.Should().HaveCount(attempts, $"the retry must not happen before {seconds} s");
            h.Time.Advance(TimeSpan.FromMilliseconds(1));
        }

        await h.Worker.WhenRunsCompleteAsync();

        // Assert
        h.Factory.Session.DeleteCalls.Should().HaveCount(3);
        h.Progress.Where(p => p.WillRetry).Should().HaveCount(2);
        h.Progress[^1].Finished.Should().BeTrue();
        h.Progress[^1].Error.Should().BeNull();
    }

    /// <summary>
    /// Verifies a rejected password is not repeated at once; the account is tried again only after five minutes.
    /// </summary>
    [Fact]
    public async Task Run_SignInFails_WaitsBeforeTryingAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(1, ct);
        h.Factory.Failure = new OAuthReauthenticationRequiredException("me@example.com");

        // Act
        await h.RunAsync(ct);
        await h.RunAsync(ct);
        var beforeDelay = h.Factory.ConnectCount;
        h.Time.Advance(DeletionWorker.BlockedInterval);
        h.Factory.Failure = null;
        await h.RunAsync(ct);

        // Assert
        beforeDelay.Should().Be(1);
        h.Factory.ConnectCount.Should().Be(2);
        h.Factory.Session.Messages["INBOX"].Should().BeEmpty();
        h.Progress[0].Error.Should().NotBeNull();
        h.Progress[0].WillRetry.Should().BeTrue();
    }

    [Fact]
    public async Task Run_SettingsChangedWhileBlocked_TriesAgainAtOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(1, ct);
        h.Factory.Failure = new OAuthReauthenticationRequiredException("me@example.com");
        await h.RunAsync(ct);

        // Act
        h.Factory.Failure = null;
        h.Revisions.Bump(h.AccountId);
        await h.RunAsync(ct);

        // Assert
        h.Factory.ConnectCount.Should().Be(2);
        h.Factory.Session.Messages["INBOX"].Should().BeEmpty();
    }

    [Fact]
    public async Task Run_SettingsChangedBetweenBatches_ContinuesOnANewConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(150, ct);
        var batches = 0;
        h.Events.DeletionProgressed += (_, _) =>
        {
            if (++batches == 1)
            {
                h.Revisions.Bump(h.AccountId);
            }
        };

        // Act
        await h.RunAsync(ct);

        // Assert
        h.Factory.ConnectCount.Should().Be(2);
        h.Factory.Session.DeleteCalls.Should().HaveCount(2);
        h.Factory.Session.Messages["INBOX"].Should().BeEmpty();
        h.Progress[^1].Deleted.Should().Be(150);
    }

    /// <summary>
    /// Verifies a refusal is reported again when the run ends, so the final report does not read as success.
    /// </summary>
    [Fact]
    public async Task Run_ServerRefuses_FinalReportCarriesTheError()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(1, ct);
        h.Factory.Session.DeleteFailure = new MailOperationRejectedException("read only");

        // Act
        await h.RunAsync(ct);

        // Assert
        var last = h.Progress[^1];
        last.Finished.Should().BeTrue();
        last.Error.Should().Be("read only");
        last.WillRetry.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a signal wakes the running service without waiting for the poll interval.
    /// </summary>
    [Fact]
    public async Task Execute_Signalled_StartsWithoutAdvancingTheClock()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(0, ct);
        await h.Worker.StartAsync(ct);
        try
        {
            await WatcherHarness.WaitUntilAsync(() => h.Time.PendingTimerCount == 1, ct);

            // The first run must be over before the test writes: both would use the same in-memory SQLite connection.
            await h.Worker.WhenRunsCompleteAsync();

            // Act
            await h.AddPendingAsync(1, ct);
            h.Signal.Notify();

            // Assert
            await WatcherHarness.WaitUntilAsync(() => h.Progress.Any(p => p.Finished), ct);
        }
        finally
        {
            await h.Worker.StopAsync(ct);
        }
    }

    /// <summary>
    /// Verifies messages deleted while a run is ending are picked up by that run, not only by the next poll.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhileRunIsEnding_DeletesTheNewMessagesToo()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(1, ct);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Factory.Session.OnDisposeAsync = () =>
        {
            closing.TrySetResult();
            return release.Task;
        };

        // The run has found nothing left to delete and is held while it closes its connection.
        await h.Worker.ReconcileAsync(ct);
        await closing.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Act
        await h.AddPendingAsync(1, ct);
        await h.Worker.ReconcileAsync(ct);
        release.SetResult();
        await h.Worker.WhenRunsCompleteAsync();

        // Assert
        h.Factory.Session.DeleteCalls.Should().HaveCount(2);
        h.Factory.Session.Messages["INBOX"].Should().BeEmpty();
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
        /// The seeded account.
        /// </summary>
        private readonly MailAccount _account;

        /// <summary>
        /// The seeded INBOX.
        /// </summary>
        private readonly MailFolder _inbox;

        /// <summary>
        /// The reports received so far.
        /// </summary>
        private readonly List<DeletionProgressEventArgs> _progress = [];

        /// <summary>
        /// Initializes a new instance of the <see cref="Harness"/> class.
        /// </summary>
        /// <param name="connection">The global database connection.</param>
        /// <param name="provider">The service provider.</param>
        /// <param name="account">The account.</param>
        /// <param name="inbox">The INBOX.</param>
        private Harness(SqliteConnection connection, ServiceProvider provider, MailAccount account, MailFolder inbox)
        {
            _connection = connection;
            _provider = provider;
            _account = account;
            _inbox = inbox;
            provider.GetRequiredService<SyncEvents>().DeletionProgressed += (_, e) =>
            {
                lock (_progress)
                {
                    _progress.Add(e);
                }
            };
        }

        /// <summary>
        /// Gets the worker under test.
        /// </summary>
        public DeletionWorker Worker => _provider.GetRequiredService<DeletionWorker>();

        /// <summary>
        /// Gets the connection factory.
        /// </summary>
        public FakeImapSessionFactory Factory => _provider.GetRequiredService<FakeImapSessionFactory>();

        /// <summary>
        /// Gets the virtual clock.
        /// </summary>
        public VirtualTimeProvider Time => (VirtualTimeProvider)_provider.GetRequiredService<TimeProvider>();

        /// <summary>
        /// Gets the seeded account's identifier.
        /// </summary>
        public Guid AccountId => _account.Id;

        /// <summary>
        /// Gets the connection settings revisions.
        /// </summary>
        public AccountConnectionRevisions Revisions => _provider.GetRequiredService<AccountConnectionRevisions>();

        /// <summary>
        /// Gets the sync event hub.
        /// </summary>
        public SyncEvents Events => _provider.GetRequiredService<SyncEvents>();

        /// <summary>
        /// Gets the wake-up signal.
        /// </summary>
        public DeletionSignal Signal => _provider.GetRequiredService<DeletionSignal>();

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
        /// Creates a harness with one account whose INBOX holds pending messages.
        /// </summary>
        /// <param name="pending">The number of messages waiting for deletion.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The harness.</returns>
        public static async Task<Harness> CreateAsync(int pending, CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(cancellationToken);
            var factory = new FakeImapSessionFactory();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(factory);
            services.AddSingleton<IImapSessionFactory>(factory);
            services.AddSingleton<TimeProvider>(new VirtualTimeProvider());
            services.AddSingleton<SyncCoordinator>();
            services.AddSingleton<SyncEvents>();
            services.AddSingleton<DeletionSignal>();
            services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<InMemoryAccountDbContextFactory>();
            services.AddSingleton<IAccountDbContextFactory>(sp => sp.GetRequiredService<InMemoryAccountDbContextFactory>());
            services.AddScoped<AccountContextProvider>();
            services.AddScoped<IAccountRepository, AccountRepository>();
            services.AddScoped<IFolderRepository, FolderRepository>();
            services.AddScoped<IMessageRepository, MessageRepository>();
            services.AddScoped<PendingDeletionService>();
            services.AddSingleton<AccountConnectionRevisions>();
            services.AddSingleton<DeletionWorker>();
            var provider = services.BuildServiceProvider(validateScopes: true);

            var account = TestData.Account("me@example.com");
            var inbox = MailFolder.Create(account.Id, "INBOX", "Inbox", FolderRole.Inbox);
            await using (var scope = provider.CreateAsyncScope())
            {
                await SqliteSchemaInitializer.InitializeGlobalAsync(scope.ServiceProvider.GetRequiredService<GlobalDbContext>(), cancellationToken);
                await scope.ServiceProvider.GetRequiredService<IAccountRepository>().AddAsync(account, cancellationToken);
                await scope.ServiceProvider.GetRequiredService<IFolderRepository>().AddAsync(inbox, cancellationToken);
            }

            var harness = new Harness(connection, provider, account, inbox);
            factory.Session.Messages["INBOX"] = [];
            await harness.AddPendingAsync(pending, cancellationToken);
            return harness;
        }

        /// <summary>
        /// Adds messages to the server and the cache and marks them for deletion.
        /// </summary>
        /// <param name="count">The number of messages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when they are marked.</returns>
        public async Task AddPendingAsync(int count, CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var messages = Enumerable.Range(1, count)
                .Select(i => MailMessage.Create(_account.Id, _inbox.Id, (uint)i, "s", "A", "a@example.com", "r", DateTimeOffset.UnixEpoch, "b"))
                .ToList();
            Factory.Session.Messages["INBOX"].AddRange(messages.Select(m => TestData.Remote(m.Uid)));
            await repository.AddRangeAsync(messages, cancellationToken);
            await repository.MarkDeletePendingAsync(_account.Id, [.. messages.Select(m => m.Id)], cancellationToken);
        }

        /// <summary>
        /// Runs one reconcile pass and waits for it.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the runs are done.</returns>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await Worker.ReconcileAsync(cancellationToken);
            await Worker.WhenRunsCompleteAsync();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
