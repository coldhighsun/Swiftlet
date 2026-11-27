using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Tests for the immediate push of read and star changes: commands, <see cref="PendingFlagService"/> and <see cref="FlagPushWorker"/>.
/// </summary>
public class FlagPushTests
{
    /// <summary>
    /// Verifies a read change reaches the server without a sync and is no longer pending.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task FlagPushWorker_MessageRead_WritesItToTheServer()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(ct);
        var setup = h.Factory.ConnectCount;
        await h.Worker.StartAsync(ct);

        // Act
        await h.SetReadAsync(1, true, ct);
        await WatcherHarness.WaitUntilAsync(() => h.CompletedPushes == 1, ct);
        await h.Worker.StopAsync(ct);

        // Assert
        h.Factory.Session.GetServerFlags("INBOX", 1).IsRead.Should().BeTrue();
        (await h.PendingCountAsync(ct)).Should().Be(0);
        (h.Factory.ConnectCount - setup).Should().Be(1);
        h.Factory.Session.Disposed.Should().BeTrue("shutting down closes the kept connection");
    }

    /// <summary>
    /// Verifies a further change goes out over the connection that is already open.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task FlagPushWorker_SecondChange_ReusesTheConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(ct);
        var setup = h.Factory.ConnectCount;
        await h.Worker.StartAsync(ct);
        await h.SetReadAsync(1, true, ct);
        await WatcherHarness.WaitUntilAsync(() => h.CompletedPushes == 1, ct);

        // Act
        await h.SetReadAsync(2, true, ct);
        await WatcherHarness.WaitUntilAsync(() => h.CompletedPushes == 2, ct);
        await h.Worker.StopAsync(ct);

        // Assert
        (h.Factory.ConnectCount - setup).Should().Be(1);
    }

    /// <summary>
    /// Verifies a kept connection that was dropped meanwhile is replaced at once.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task FlagPushWorker_KeptConnectionDropped_ReconnectsAtOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(ct);
        var setup = h.Factory.ConnectCount;
        await h.Worker.StartAsync(ct);
        await h.SetReadAsync(1, true, ct);
        await WatcherHarness.WaitUntilAsync(() => h.CompletedPushes == 1, ct);
        var failures = 0;
        h.Factory.Session.OnSetFlags = (_, _) =>
        {
            if (failures++ == 0)
            {
                throw new IOException("connection reset");
            }
        };

        // Act
        await h.SetReadAsync(2, true, ct);
        await WatcherHarness.WaitUntilAsync(() => h.CompletedPushes == 2, ct);
        await h.Worker.StopAsync(ct);

        // Assert
        (h.Factory.ConnectCount - setup).Should().Be(2);
    }

    [Fact]
    public async Task FlagPushWorker_ConnectionSettingsChanged_ReplacesTheKeptConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(ct);
        var setup = h.Factory.ConnectCount;
        await h.Worker.StartAsync(ct);
        await h.SetReadAsync(1, true, ct);
        await WatcherHarness.WaitUntilAsync(() => h.CompletedPushes == 1, ct);

        // Act
        h.Revisions.Bump(h.AccountId);
        await h.SetReadAsync(2, true, ct);
        await WatcherHarness.WaitUntilAsync(() => h.CompletedPushes == 2, ct);
        await h.Worker.StopAsync(ct);

        // Assert
        (h.Factory.ConnectCount - setup).Should().Be(2);
    }

    /// <summary>
    /// Verifies a server that refuses the change leaves it pending for the next sync.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task FlagPushWorker_ServerRefuses_KeepsTheChangePending()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(ct);
        h.Factory.Session.SetFlagsFailure = new InvalidOperationException("read-only");
        await h.Worker.StartAsync(ct);

        // Act
        await h.SetReadAsync(1, true, ct);
        await WatcherHarness.WaitUntilAsync(() => h.Factory.Session.Disposed, ct);
        await h.Worker.StopAsync(ct);

        // Assert
        (await h.PendingCountAsync(ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies nothing is written when the folder's UIDVALIDITY no longer matches the cache.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task PushFolderAsync_UidValidityChanged_WritesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(ct);
        await h.SetReadAsync(1, true, ct);
        h.Factory.Session.UidValidity["INBOX"] = 99;

        // Act
        var pushed = await h.PushInboxAsync(ct);

        // Assert
        pushed.Should().Be(0);
        h.Factory.Session.SetFlagsCalls.Should().BeEmpty();
        (await h.PendingCountAsync(ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies a sync waits for a running flag push of a folder before it touches that folder's flags.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncAsync_FlagWorkRunning_WaitsForIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(ct);
        var release = new TaskCompletionSource<int>();
        var holder = h.Flags.RunExclusiveAsync(h.InboxId, () => release.Task, ct);
        h.Factory.Session.FetchFlagsCalls.Clear();

        // Act
        var sync = h.SyncAsync(ct);
        var finishedEarly = sync.IsCompleted;
        var fetchedEarly = h.Factory.Session.FetchFlagsCalls.Count;
        release.SetResult(0);
        await holder;
        await sync;

        // Assert
        finishedEarly.Should().BeFalse();
        fetchedEarly.Should().Be(0);
        h.Factory.Session.FetchFlagsCalls.Should().ContainSingle();
    }

    /// <summary>
    /// An account whose INBOX holds two unread messages that are cached, wired to real repositories over in-memory SQLite.
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
        /// Initializes a new instance of the <see cref="Harness"/> class.
        /// </summary>
        /// <param name="connection">The global database connection.</param>
        /// <param name="provider">The service provider.</param>
        /// <param name="account">The seeded account.</param>
        private Harness(SqliteConnection connection, ServiceProvider provider, MailAccount account)
        {
            _connection = connection;
            _provider = provider;
            _account = account;
        }

        /// <summary>
        /// Gets the INBOX identifier.
        /// </summary>
        public Guid InboxId { get; private set; }

        /// <summary>
        /// Gets the worker under test.
        /// </summary>
        public FlagPushWorker Worker => _provider.GetRequiredService<FlagPushWorker>();

        /// <summary>
        /// Gets the IMAP factory holding the scripted server.
        /// </summary>
        public FakeImapSessionFactory Factory => _provider.GetRequiredService<FakeImapSessionFactory>();

        /// <summary>
        /// Gets the number of completed pushes; the service logs one entry after it has cleared the pending marks.
        /// </summary>
        public int CompletedPushes => _provider.GetRequiredService<CapturingLogger<PendingFlagService>>().Entries.Count;

        /// <summary>
        /// Gets the seeded account's identifier.
        /// </summary>
        public Guid AccountId => _account.Id;

        /// <summary>
        /// Gets the connection settings revisions.
        /// </summary>
        public AccountConnectionRevisions Revisions => _provider.GetRequiredService<AccountConnectionRevisions>();

        /// <summary>
        /// Gets the flag locks.
        /// </summary>
        public FlagCoordinator Flags => _provider.GetRequiredService<FlagCoordinator>();

        /// <summary>
        /// Creates the harness and runs the first sync.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The harness.</returns>
        public static async Task<Harness> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(cancellationToken);
            var factory = new FakeImapSessionFactory();
            factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
            factory.Session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2)];

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<CapturingLogger<PendingFlagService>>();
            services.AddSingleton<ILogger<PendingFlagService>>(sp => sp.GetRequiredService<CapturingLogger<PendingFlagService>>());
            services.AddSingleton(factory);
            services.AddSingleton<IImapSessionFactory>(factory);
            services.AddSingleton<ISyncSettings>(new FakeSyncSettings());
            services.AddSingleton(new MailSyncOptions());
            services.AddSingleton<FlagCoordinator>();
            services.AddSingleton<FlagPushSignal>();
            services.AddSingleton(TimeProvider.System);
            services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<InMemoryAccountDbContextFactory>();
            services.AddSingleton<IAccountDbContextFactory>(sp => sp.GetRequiredService<InMemoryAccountDbContextFactory>());
            services.AddScoped<AccountContextProvider>();
            services.AddScoped<IAccountRepository, AccountRepository>();
            services.AddScoped<IFolderRepository, FolderRepository>();
            services.AddScoped<IMessageRepository, MessageRepository>();
            services.AddScoped<MailSyncService>();
            services.AddScoped<PendingFlagService>();
            services.AddSingleton<AccountConnectionRevisions>();
            services.AddSingleton<FlagPushWorker>();
            var provider = services.BuildServiceProvider(validateScopes: true);

            await using var scope = provider.CreateAsyncScope();
            await SqliteSchemaInitializer.InitializeGlobalAsync(scope.ServiceProvider.GetRequiredService<GlobalDbContext>(), cancellationToken);
            var account = TestData.Account($"{Guid.NewGuid():N}@example.com");
            await scope.ServiceProvider.GetRequiredService<IAccountRepository>().AddAsync(account, cancellationToken);
            var harness = new Harness(connection, provider, account);
            await harness.SyncAsync(cancellationToken);
            harness.InboxId = (await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetByAccountAsync(account.Id, cancellationToken))
                .Single()
                .Id;
            factory.Session.Disposed = false;
            return harness;
        }

        /// <summary>
        /// Runs a regular sync in its own scope.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The sync result.</returns>
        public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MailSyncService>().SyncAsync(_account.Id, cancellationToken);
        }

        /// <summary>
        /// Changes the read state of a cached message through the command, like the UI does.
        /// </summary>
        /// <param name="uid">The message UID.</param>
        /// <param name="isRead">The new state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the command is handled.</returns>
        public async Task SetReadAsync(uint uid, bool isRead, CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var state = (await messages.GetFlagStatesAsync(_account.Id, InboxId, cancellationToken)).Single(m => m.Uid == uid);
            var handler = new SetMessageReadCommandHandler(messages, _provider.GetRequiredService<FlagPushSignal>());
            await handler.Handle(new SetMessageReadCommand(_account.Id, state.Id, isRead), cancellationToken);
        }

        /// <summary>
        /// Pushes the pending changes of the INBOX over a new connection.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of messages written.</returns>
        public async Task<int> PushInboxAsync(CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PendingFlagService>()
                .PushFolderAsync(Factory.Session, _account.Id, InboxId, cancellationToken);
        }

        /// <summary>
        /// Counts the INBOX messages with a change waiting to be written.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The count.</returns>
        public async Task<int> PendingCountAsync(CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IMessageRepository>()
                .GetPendingFlagStatesAsync(_account.Id, InboxId, cancellationToken)).Count;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Worker.Dispose();
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
