using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;
using Swiftlet.Infrastructure.Sync;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="BackfillWorker"/> with real SQLite repositories and a scripted IMAP session.
/// </summary>
public class BackfillWorkerTests
{
    /// <summary>
    /// Verifies the worker downloads the whole history in batches, reports every stored message and refreshes the views once per batch.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillAccountAsync_LargeFolder_DownloadsEverythingAndReportsEachMessage()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(250, ct);
        var progress = new List<(int Downloaded, bool Finished, string? Folder, int Remaining)>();
        h.Events.BackfillProgressed += (_, e) => progress.Add((e.Downloaded, e.Finished, e.FolderName, e.Remaining));
        var refreshes = 0;
        h.Events.AccountSynced += (_, _) => refreshes++;

        // Act
        var complete = await h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);

        // Assert
        complete.Should().BeTrue();
        (await h.CachedCountAsync("INBOX", ct)).Should().Be(250);
        progress.Select(p => p.Downloaded).Should().Equal(Enumerable.Range(1, 230), "every stored message is reported");
        progress.Should().OnlyContain(p => !p.Finished && p.Folder == "INBOX");
        progress[0].Remaining.Should().Be(229, "130 older messages were left after listing the first 100, minus the one just stored");
        progress[^1].Remaining.Should().Be(0);
        refreshes.Should().Be(3);
        h.Factory.Session.Disposed.Should().BeTrue("the dedicated connection is closed when the pass ends");
    }

    /// <summary>
    /// Verifies the account lock is free whenever a batch has been reported, so regular syncs can slip in between batches.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillAccountAsync_BetweenBatches_AccountLockIsFree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(250, ct);
        var lockWasFree = new List<bool>();
        h.Events.AccountSynced += (_, e) =>
        {
            var probe = h.Coordinator.RunExclusiveAsync(e.AccountId, () => Task.FromResult(0), ct);
            lockWasFree.Add(probe.IsCompletedSuccessfully);
        };

        // Act
        await h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);

        // Assert
        lockWasFree.Should().Equal(true, true, true);
    }

    /// <summary>
    /// Verifies a regular sync that waits for the lock runs between two batches and its new mail is not lost.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillAccountAsync_NewMailArrivesMidway_RegularSyncRunsBetweenBatches()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(250, ct);
        var syncDuringBackfill = (Task<SyncResult>?)null;
        h.Events.AccountSynced += (_, e) =>
        {
            if (syncDuringBackfill is not null)
            {
                return;
            }

            h.Factory.Session.Messages["INBOX"].Add(TestData.Remote(251));
            syncDuringBackfill = Task.Run(
                () => h.Coordinator.RunExclusiveAsync(e.AccountId, () => h.SyncAsync(ct), ct),
                ct);
        };

        // Act
        await h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);
        var sync = await syncDuringBackfill!;

        // Assert
        sync.NewMessages.Should().Be(1);
        (await h.CachedCountAsync("INBOX", ct)).Should().Be(251);
    }

    /// <summary>
    /// Verifies every folder is completed in priority order, All Mail by copying what the INBOX already holds, and Trash and Junk last.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillAccountAsync_AllFolderRoles_CompletesInPriorityOrderWithTrashAndJunkLast()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(
            30,
            ct,
            extraFolders: [("Trash", FolderRole.Trash), ("Junk", FolderRole.Junk), ("[Gmail]/All Mail", FolderRole.AllMail)]);

        // Act
        var complete = await h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);

        // Assert
        complete.Should().BeTrue();
        (await h.CachedCountAsync("INBOX", ct)).Should().Be(30);
        (await h.CachedCountAsync("[Gmail]/All Mail", ct)).Should().Be(50);
        (await h.CachedCountAsync("Junk", ct)).Should().Be(50);
        (await h.CachedCountAsync("Trash", ct)).Should().Be(50);
        h.Factory.Session.FetchOlderCalls.Select(c => c.Folder).Distinct().Should().Equal("INBOX", "[Gmail]/All Mail", "Junk", "Trash");
        h.Factory.Session.FetchMessagesCalls.Select(c => c.Folder).Should().NotContain("[Gmail]/All Mail", "every older All Mail message exists in the INBOX");
    }

    /// <summary>
    /// Verifies All Mail keeps only its first-sync messages when the user turned it off.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillAccountAsync_AllMailDisabled_LeavesAllMailAlone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(30, ct, extraFolders: [("[Gmail]/All Mail", FolderRole.AllMail)]);
        h.Settings.DownloadAllMail = false;

        // Act
        await h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);

        // Assert
        (await h.CachedCountAsync("[Gmail]/All Mail", ct)).Should().Be(20);
        h.Factory.Session.FetchOlderCalls.Select(c => c.Folder).Distinct().Should().Equal("INBOX");
    }

    /// <summary>
    /// Verifies a folder that is still empty locally does not keep the account in the short retry loop.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillAccountAsync_EmptyFolderOnServer_PassStillCountsAsComplete()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(30, ct);
        h.Factory.Session.Folders.Add(TestData.Folder("Drafts", FolderRole.Drafts));
        h.Factory.Session.Messages["Drafts"] = [];
        await h.SyncAsync(ct);

        // Act
        var complete = await h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);

        // Assert
        complete.Should().BeTrue();
        (await h.CachedCountAsync("INBOX", ct)).Should().Be(30);
        (await h.CachedCountAsync("Drafts", ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies turning the setting off stops the download without connecting for older messages.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillAccountAsync_SettingOff_DownloadsNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(250, ct);
        h.Settings.DownloadFullHistory = false;

        // Act
        var complete = await h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);

        // Assert
        complete.Should().BeFalse();
        h.Factory.Session.FetchOlderCalls.Should().BeEmpty();
        (await h.CachedCountAsync("INBOX", ct)).Should().Be(20);
    }

    /// <summary>
    /// Verifies a failure in the middle keeps what was downloaded and the next pass resumes from the oldest cached message.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillAccountAsync_FailsMidway_NextPassResumesWithoutDuplicates()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(250, ct);
        var calls = 0;
        h.Factory.Session.OnFetchOlder = _ =>
        {
            if (++calls == 2)
            {
                throw new IOException("connection reset");
            }
        };

        // Act
        var act = () => h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);
        await act.Should().ThrowAsync<IOException>();
        var cachedAfterFailure = await h.CachedCountAsync("INBOX", ct);
        var complete = await h.Worker.BackfillAccountAsync(h.Account, _ => { }, ct);

        // Assert
        cachedAfterFailure.Should().Be(120);
        complete.Should().BeTrue();
        (await h.CachedCountAsync("INBOX", ct)).Should().Be(250);
        (await h.DistinctUidCountAsync("INBOX", ct)).Should().Be(250);
    }

    /// <summary>
    /// Verifies the scheduled path runs a pass once and leaves a finished account alone until the recheck interval.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ReconcileAsync_FinishedAccount_IsNotStartedAgainImmediately()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(250, ct);
        var connectsBefore = h.Factory.ConnectCount;
        var finished = new List<int>();
        h.Events.BackfillProgressed += (_, e) =>
        {
            if (e.Finished)
            {
                finished.Add(e.Downloaded);
            }
        };

        // Act
        await h.Worker.ReconcileAsync(ct);
        await h.Worker.WhenPassesCompleteAsync();
        await h.Worker.ReconcileAsync(ct);
        await h.Worker.WhenPassesCompleteAsync();

        // Assert
        (h.Factory.ConnectCount - connectsBefore).Should().Be(1);
        finished.Should().Equal(230);
        (await h.CachedCountAsync("INBOX", ct)).Should().Be(250);
    }

    /// <summary>
    /// Verifies a failed pass is logged, not thrown, and is not retried until the retry interval has passed.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ReconcileAsync_ConnectFails_BacksOffInsteadOfRetryingAtOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(250, ct);
        var connectsBefore = h.Factory.ConnectCount;
        h.Factory.Failure = new IOException("offline");

        // Act
        await h.Worker.ReconcileAsync(ct);
        await h.Worker.WhenPassesCompleteAsync();
        await h.Worker.ReconcileAsync(ct);
        await h.Worker.WhenPassesCompleteAsync();

        // Assert
        (h.Factory.ConnectCount - connectsBefore).Should().Be(1);
        (await h.CachedCountAsync("INBOX", ct)).Should().Be(20);
    }

    /// <summary>
    /// Verifies nothing starts while the setting is off.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ReconcileAsync_SettingOff_StartsNoPass()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Harness.CreateAsync(250, ct);
        var connectsBefore = h.Factory.ConnectCount;
        h.Settings.DownloadFullHistory = false;

        // Act
        await h.Worker.ReconcileAsync(ct);
        await h.Worker.WhenPassesCompleteAsync();

        // Assert
        h.Factory.ConnectCount.Should().Be(connectsBefore);
    }

    /// <summary>
    /// A worker wired to real repositories over in-memory SQLite, with a server holding one large INBOX.
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
        /// Initializes a new instance of the <see cref="Harness"/> class.
        /// </summary>
        /// <param name="connection">The global database connection.</param>
        /// <param name="provider">The service provider.</param>
        /// <param name="account">The seeded account.</param>
        private Harness(SqliteConnection connection, ServiceProvider provider, MailAccount account)
        {
            _connection = connection;
            _provider = provider;
            Account = account;
        }

        /// <summary>
        /// Gets the seeded account.
        /// </summary>
        public MailAccount Account { get; }

        /// <summary>
        /// Gets the worker under test.
        /// </summary>
        public BackfillWorker Worker => _provider.GetRequiredService<BackfillWorker>();

        /// <summary>
        /// Gets the IMAP factory holding the scripted server.
        /// </summary>
        public FakeImapSessionFactory Factory => _provider.GetRequiredService<FakeImapSessionFactory>();

        /// <summary>
        /// Gets the sync event hub.
        /// </summary>
        public SyncEvents Events => _provider.GetRequiredService<SyncEvents>();

        /// <summary>
        /// Gets the per-account lock.
        /// </summary>
        public SyncCoordinator Coordinator => _provider.GetRequiredService<SyncCoordinator>();

        /// <summary>
        /// Gets the sync settings.
        /// </summary>
        public FakeSyncSettings Settings => _provider.GetRequiredService<FakeSyncSettings>();

        /// <summary>
        /// Creates a harness whose account has completed its first sync (the newest 20 messages per folder are cached).
        /// </summary>
        /// <param name="inboxCount">The number of messages on the server's INBOX.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="extraFolders">Further server folders, each holding 50 messages.</param>
        /// <returns>The harness.</returns>
        public static async Task<Harness> CreateAsync(
            int inboxCount,
            CancellationToken cancellationToken,
            (string Name, FolderRole Role)[]? extraFolders = null)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(cancellationToken);
            var factory = new FakeImapSessionFactory();
            factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
            factory.Session.Messages["INBOX"] = [.. Enumerable.Range(1, inboxCount).Select(i => TestData.Remote((uint)i))];
            foreach (var (name, role) in extraFolders ?? [])
            {
                factory.Session.Folders.Add(TestData.Folder(name, role));
                factory.Session.Messages[name] = [.. Enumerable.Range(1, 50).Select(i => TestData.Remote((uint)i))];
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(factory);
            services.AddSingleton<IImapSessionFactory>(factory);
            services.AddSingleton<FakeSyncSettings>();
            services.AddSingleton<ISyncSettings>(sp => sp.GetRequiredService<FakeSyncSettings>());
            services.AddSingleton(new MailSyncOptions(BackfillBatchSize: 100));
            services.AddSingleton<SyncCoordinator>();
            services.AddSingleton<SyncEvents>();
            services.AddSingleton(TimeProvider.System);
            services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<InMemoryAccountDbContextFactory>();
            services.AddSingleton<IAccountDbContextFactory>(sp => sp.GetRequiredService<InMemoryAccountDbContextFactory>());
            services.AddScoped<AccountContextProvider>();
            services.AddScoped<IAccountRepository, AccountRepository>();
            services.AddScoped<IFolderRepository, FolderRepository>();
            services.AddScoped<IMessageRepository, MessageRepository>();
            services.AddScoped<MailSyncService>();
            services.AddScoped<MailBackfillService>();
            services.AddSingleton<BackfillWorker>();
            var provider = services.BuildServiceProvider(validateScopes: true);

            await using var scope = provider.CreateAsyncScope();
            await SqliteSchemaInitializer.InitializeGlobalAsync(scope.ServiceProvider.GetRequiredService<GlobalDbContext>(), cancellationToken);
            var account = TestData.Account($"{Guid.NewGuid():N}@example.com");
            await scope.ServiceProvider.GetRequiredService<IAccountRepository>().AddAsync(account, cancellationToken);
            var harness = new Harness(connection, provider, account);
            await harness.SyncAsync(cancellationToken);
            factory.Session.Disposed = false;
            return harness;
        }

        /// <summary>
        /// Runs a regular sync of the account in its own scope.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The sync result.</returns>
        public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MailSyncService>().SyncAsync(Account.Id, cancellationToken);
        }

        /// <summary>
        /// Counts the cached messages of a folder.
        /// </summary>
        /// <param name="folderFullName">The folder full name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of cached messages.</returns>
        public async Task<int> CachedCountAsync(string folderFullName, CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            var folder = await FindFolderAsync(scope, folderFullName, cancellationToken);
            var context = await scope.ServiceProvider.GetRequiredService<AccountContextProvider>().GetAsync(Account.Id, cancellationToken);
            return await context.Messages.CountAsync(m => m.FolderId == folder.Id, cancellationToken);
        }

        /// <summary>
        /// Counts the distinct UIDs cached for a folder.
        /// </summary>
        /// <param name="folderFullName">The folder full name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of distinct UIDs.</returns>
        public async Task<int> DistinctUidCountAsync(string folderFullName, CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            var folder = await FindFolderAsync(scope, folderFullName, cancellationToken);
            var context = await scope.ServiceProvider.GetRequiredService<AccountContextProvider>().GetAsync(Account.Id, cancellationToken);
            return await context.Messages.Where(m => m.FolderId == folder.Id).Select(m => m.Uid).Distinct().CountAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }

        /// <summary>
        /// Finds a folder of the account by its full name.
        /// </summary>
        /// <param name="scope">The scope to resolve the repository from.</param>
        /// <param name="folderFullName">The folder full name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The folder.</returns>
        private async Task<MailFolder> FindFolderAsync(AsyncServiceScope scope, string folderFullName, CancellationToken cancellationToken)
        {
            var folders = await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetByAccountAsync(Account.Id, cancellationToken);
            return folders.Single(f => f.FullName == folderFullName);
        }
    }
}
