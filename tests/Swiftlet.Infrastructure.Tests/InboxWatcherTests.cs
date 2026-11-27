using AwesomeAssertions;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;
using Swiftlet.Infrastructure.Sync;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="InboxWatcher"/>. Time is fully virtual: delays only elapse when a test advances <see cref="VirtualTimeProvider"/>.
/// </summary>
public class InboxWatcherTests
{
    /// <summary>
    /// Verifies a new account gets a loop that connects, syncs, and starts waiting on its inbox.
    /// </summary>
    [Fact]
    public async Task Reconcile_NewAccount_StartsLoopAndConnects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        var account = await h.AddAccountAsync(withInbox: true, ct);
        h.Session.OnWaitForChange = WatcherHarness.BlockUntilCancelled;

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 1, ct);

        h.Factory.ConnectCount.Should().Be(1);
        h.Watcher.RunningAccountIds.Should().BeEquivalentTo([account.Id]);
        h.Session.WaitFolders.Should().Equal("INBOX");
    }

    /// <summary>
    /// Verifies a deleted account has its loop cancelled and its session disposed.
    /// </summary>
    [Fact]
    public async Task Reconcile_AccountDeleted_StopsItsLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        var account = await h.AddAccountAsync(withInbox: true, ct);
        h.Session.OnWaitForChange = WatcherHarness.BlockUntilCancelled;
        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 1, ct);

        await h.DeleteAccountAsync(account.Id, ct);
        await h.Watcher.ReconcileAsync(ct);

        h.Watcher.RunningAccountIds.Should().BeEmpty();
        h.Session.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Reconcile_ConnectionSettingsChanged_RestartsTheLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        var account = await h.AddAccountAsync(withInbox: true, ct);
        h.Session.OnWaitForChange = WatcherHarness.BlockUntilCancelled;
        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 1, ct);

        h.Revisions.Bump(account.Id);
        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 2, ct);
        await h.Watcher.ReconcileAsync(ct);

        h.Factory.ConnectCount.Should().Be(2, "one change restarts the loop once");
    }

    [Fact]
    public async Task Reconcile_ServerWithoutIdleThenSettingsChanged_ChecksTheServerAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        var account = await h.AddAccountAsync(withInbox: true, ct);
        h.Session.SupportsIdle = false;
        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.Disposed, ct);

        h.Session.SupportsIdle = true;
        h.Session.OnWaitForChange = WatcherHarness.BlockUntilCancelled;
        h.Revisions.Bump(account.Id);
        await h.Watcher.ReconcileAsync(ct);

        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 1, ct);
        h.Factory.ConnectCount.Should().Be(2);
    }

    /// <summary>
    /// Verifies a reported change (the first wait of a session) triggers a sync command and then another wait.
    /// </summary>
    [Fact]
    public async Task Loop_ChangeReported_SendsSyncCommandThenWaitsAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        var account = await h.AddAccountAsync(withInbox: true, ct);
        var calls = 0;
        h.Session.OnWaitForChange = (_, _, token) =>
        {
            h.Events.Add("wait");
            return ++calls == 1 ? Task.FromResult(true) : WatcherHarness.BlockUntilCancelled("INBOX", TimeSpan.Zero, token);
        };

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 2, ct);

        // The first wait of a session reports a change, so the sync follows it and the next wait compares against the pre-sync baseline.
        h.Events.Snapshot().Should().Equal("wait", "sync", "wait");
        h.Sender.SyncedAccounts.Should().OnlyContain(id => id == account.Id);
    }

    /// <summary>
    /// Verifies an elapsed maximum wait re-issues IDLE without running a sync.
    /// </summary>
    [Fact]
    public async Task Loop_MaxWaitElapsed_ReissuesIdleWithoutSync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        var maxWaits = new List<TimeSpan>();
        h.Session.OnWaitForChange = (folder, maxWait, token) =>
        {
            maxWaits.Add(maxWait);
            return maxWaits.Count switch
            {
                1 => Task.FromResult(true),
                2 => Task.FromResult(false),
                _ => WatcherHarness.BlockUntilCancelled(folder, maxWait, token),
            };
        };

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 3, ct);

        // Only the first wait (a change) synced; the elapsed second wait just re-issued IDLE.
        h.Sender.SyncedAccounts.Should().HaveCount(1);
        h.Factory.ConnectCount.Should().Be(1);
        maxWaits.Should().OnlyContain(w => w == TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Verifies a failing connection is retried after 5 s, 10 s, 20 s and so on, capped at 5 minutes.
    /// </summary>
    [Fact]
    public async Task Loop_ConnectionFails_BacksOffExponentiallyAndCaps()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        h.Factory.Failure = new IOException("network down");

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 1, ct);

        var expectedDelays = new[] { 5, 10, 20, 40, 80, 160, 300, 300 };
        var attempts = 1;
        foreach (var seconds in expectedDelays)
        {
            await WatcherHarness.WaitUntilAsync(() => h.Time.PendingTimerCount == 1, ct);
            h.Time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            h.Factory.ConnectCount.Should().Be(attempts, $"the retry must not happen before {seconds} s");

            h.Time.Advance(TimeSpan.FromMilliseconds(1));
            attempts++;
            await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == attempts, ct);
        }
    }

    /// <summary>
    /// Verifies the backoff goes back to 5 s once a wait that actually idled succeeded (the first wait of a session does not count).
    /// </summary>
    [Fact]
    public async Task Loop_ConnectionFailsThenRecovers_ResetsBackoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        h.Factory.Failure = new IOException("network down");
        var waits = 0;
        h.Session.OnWaitForChange = (_, _, _) => ++waits switch
        {
            1 => Task.FromResult(true),
            2 => Task.FromResult(false),
            _ => Task.FromException<bool>(new IOException("connection dropped")),
        };

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 1 && h.Time.PendingTimerCount == 1, ct);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 2 && h.Time.PendingTimerCount == 1, ct);
        h.Factory.Failure = null;
        h.Time.Advance(TimeSpan.FromSeconds(10));

        // Connect succeeds, wait #1 reports a change, wait #2 idles and times out (success), wait #3 throws: the next retry is 5 s away again.
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 3 && h.Time.PendingTimerCount == 1, ct);
        h.Time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1));
        h.Factory.ConnectCount.Should().Be(3);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 4, ct);
    }

    /// <summary>
    /// Verifies that when IDLE itself fails on every connection (so wait #1 returns true but wait #2 throws) the backoff keeps growing,
    /// while a sync still runs after each first wait.
    /// </summary>
    [Fact]
    public async Task Loop_IdleAlwaysFailsAfterFirstWait_BacksOffAndStillSyncs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        var waits = 0;
        h.Session.OnWaitForChange = (_, _, _) => ++waits % 2 == 1
            ? Task.FromResult(true)
            : Task.FromException<bool>(new IOException("IDLE rejected"));

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 1, ct);

        var expectedDelays = new[] { 5, 10, 20, 40, 80, 160, 300, 300 };
        var attempts = 1;
        foreach (var seconds in expectedDelays)
        {
            await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == attempts * 2 && h.Time.PendingTimerCount == 1, ct);
            h.Time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            h.Factory.ConnectCount.Should().Be(attempts, $"the retry must not happen before {seconds} s");

            h.Time.Advance(TimeSpan.FromMilliseconds(1));
            attempts++;
            await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == attempts, ct);
        }

        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == attempts * 2, ct);
        h.Sender.SyncedAccounts.Should().HaveCount(attempts);
    }

    /// <summary>
    /// Verifies a failing sync command is logged and the loop keeps waiting.
    /// </summary>
    [Fact]
    public async Task Loop_SyncFails_LogsAndContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        h.Sender.Failure = new InvalidOperationException("sync broke");
        var calls = 0;
        h.Session.OnWaitForChange = (_, _, token) => ++calls == 1
            ? Task.FromResult(true)
            : WatcherHarness.BlockUntilCancelled("INBOX", TimeSpan.Zero, token);

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 2, ct);

        h.Factory.ConnectCount.Should().Be(1);
        h.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    /// <summary>
    /// Verifies every connection starts with a wait that reports a change, followed by a sync, and a reconnect repeats this.
    /// </summary>
    [Fact]
    public async Task Loop_Reconnected_RepeatsFirstWaitThenSync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        var calls = 0;
        h.Session.OnWaitForChange = (_, _, token) =>
        {
            h.Events.Add("wait");
            return ++calls switch
            {
                1 => Task.FromResult(true),
                2 => Task.FromException<bool>(new IOException("connection dropped")),
                3 => Task.FromResult(true),
                _ => WatcherHarness.BlockUntilCancelled("INBOX", TimeSpan.Zero, token),
            };
        };

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Time.PendingTimerCount == 1, ct);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 4, ct);

        h.Events.Snapshot().Should().Equal("wait", "sync", "wait", "wait", "sync", "wait");
        h.Factory.ConnectCount.Should().Be(2);
    }

    /// <summary>
    /// Verifies a server without IDLE is connected to once, then left to the polling worker.
    /// </summary>
    [Fact]
    public async Task Loop_ServerWithoutIdle_StopsWithoutPollingTheServer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        h.Session.SupportsIdle = false;

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.Disposed, ct);
        for (var i = 0; i < 3; i++)
        {
            await h.Watcher.ReconcileAsync(ct);
        }

        h.Factory.ConnectCount.Should().Be(1);
        h.Session.WaitCount.Should().Be(0);
        h.Sender.SyncedAccounts.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the loop does not connect before the account has a local inbox folder, and connects once it appears.
    /// </summary>
    [Fact]
    public async Task Loop_NoInboxFolderYet_WaitsWithoutConnecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        var account = await h.AddAccountAsync(withInbox: false, ct);
        h.Session.OnWaitForChange = WatcherHarness.BlockUntilCancelled;

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Time.PendingTimerCount == 1, ct);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await WatcherHarness.WaitUntilAsync(() => h.Time.PendingTimerCount == 1, ct);
        h.Factory.ConnectCount.Should().Be(0);

        await h.AddInboxAsync(account.Id, ct);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 1, ct);

        h.Factory.ConnectCount.Should().Be(1);
    }

    /// <summary>
    /// Verifies an expired sign-in delays the next attempt by five minutes instead of the short backoff.
    /// </summary>
    [Fact]
    public async Task Loop_ReauthenticationRequired_WaitsFiveMinutesBeforeRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        h.Factory.Failure = new OAuthReauthenticationRequiredException("me@example.com");

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 1 && h.Time.PendingTimerCount == 1, ct);
        h.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        h.Factory.ConnectCount.Should().Be(1);

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await WatcherHarness.WaitUntilAsync(() => h.Factory.ConnectCount == 2, ct);
    }

    /// <summary>
    /// Verifies stopping the watcher cancels a blocked wait and disposes the session.
    /// </summary>
    [Fact]
    public async Task Loop_Cancelled_DisposesSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        h.Session.OnWaitForChange = WatcherHarness.BlockUntilCancelled;
        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 1, ct);

        await h.Watcher.StopLoopsAsync();

        h.Session.Disposed.Should().BeTrue();
        h.Watcher.RunningAccountIds.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a sync that is cancelled for a reason other than shutdown (such as an HTTP timeout) is logged and does not cause a reconnect.
    /// </summary>
    [Fact]
    public async Task Loop_SyncThrowsNonLoopCancellation_LogsAndContinuesWithoutReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        h.Sender.Failure = new TaskCanceledException("token refresh timed out");
        var calls = 0;
        h.Session.OnWaitForChange = (_, _, token) => ++calls == 1
            ? Task.FromResult(true)
            : WatcherHarness.BlockUntilCancelled("INBOX", TimeSpan.Zero, token);

        await h.Watcher.ReconcileAsync(ct);
        await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 2, ct);

        h.Factory.ConnectCount.Should().Be(1);
        h.Time.PendingTimerCount.Should().Be(0, "no reconnect backoff delay is armed");
        h.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception is TaskCanceledException);
    }

    /// <summary>
    /// Verifies a reconcile pass cancelled for a reason other than shutdown is logged and the watcher keeps running.
    /// </summary>
    [Fact]
    public async Task Execute_ReconcileThrowsNonShutdownCancellation_LogsAndKeepsRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(
            services => services.AddScoped<IAccountRepository, CancellingAccountRepository>(),
            ct);

        await h.Watcher.StartAsync(ct);
        try
        {
            await WatcherHarness.WaitUntilAsync(() => h.Logger.Entries.Any(e => e.Level == LogLevel.Error && e.Exception is TaskCanceledException), ct);

            h.Watcher.ExecuteTask.Should().NotBeNull();
            h.Watcher.ExecuteTask!.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await h.Watcher.StopAsync(ct);
        }
    }

    /// <summary>
    /// Verifies shutdown cancels every loop before waiting for any of them, so slow disposals overlap.
    /// </summary>
    [Fact]
    public async Task StopLoops_TwoAccounts_CancelsBothBeforeAwaitingEither()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await WatcherHarness.CreateAsync(ct);
        await h.AddAccountAsync(withInbox: true, ct);
        await h.AddAccountAsync(withInbox: true, ct);
        var cancelled = 0;
        var bothCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Session.OnWaitForChange = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                // Each loop only finishes once the other one has also been cancelled, which a sequential stop never allows.
                if (Interlocked.Increment(ref cancelled) == 2)
                {
                    bothCancelled.TrySetResult();
                }

                await bothCancelled.Task;
                throw;
            }

            return false;
        };
        try
        {
            await h.Watcher.ReconcileAsync(ct);
            await WatcherHarness.WaitUntilAsync(() => h.Session.WaitCount == 2, ct);

            await h.Watcher.StopLoopsAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);

            h.Watcher.RunningAccountIds.Should().BeEmpty();
        }
        finally
        {
            // Release a cancelled loop even when the test fails early; otherwise disposing the harness waits for it forever.
            bothCancelled.TrySetResult();
        }
    }
}

/// <summary>
/// An account repository whose listing fails with a cancellation that is not a shutdown, like a timed-out database call.
/// </summary>
internal sealed class CancellingAccountRepository : IAccountRepository
{
    /// <inheritdoc />
    public Task AddAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task<MailAccount?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MailAccount>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        throw new TaskCanceledException("the database call timed out");
    }

    /// <inheritdoc />
    public Task UpdateAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task SetExpandedAsync(Guid id, bool expanded, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Wires an <see cref="InboxWatcher"/> to fakes: a scripted IMAP session, a recording sender and a virtual clock.
/// </summary>
internal sealed class WatcherHarness : IAsyncDisposable
{
    /// <summary>
    /// The connection keeping the in-memory global database alive; the contexts open their own connections to it.
    /// </summary>
    private readonly SqliteConnection _connection;

    /// <summary>
    /// The service provider of the scenario.
    /// </summary>
    private readonly ServiceProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatcherHarness"/> class.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="provider">The service provider.</param>
    /// <param name="watcher">The watcher under test.</param>
    private WatcherHarness(SqliteConnection connection, ServiceProvider provider, InboxWatcher watcher)
    {
        _connection = connection;
        _provider = provider;
        Watcher = watcher;
    }

    /// <summary>
    /// Gets the watcher under test.
    /// </summary>
    public InboxWatcher Watcher { get; }

    /// <summary>
    /// Gets the session factory.
    /// </summary>
    public FakeImapSessionFactory Factory => (FakeImapSessionFactory)_provider.GetRequiredService<IImapSessionFactory>();

    /// <summary>
    /// Gets the scripted session.
    /// </summary>
    public FakeImapSession Session => Factory.Session;

    /// <summary>
    /// Gets the connection settings revisions.
    /// </summary>
    public AccountConnectionRevisions Revisions => _provider.GetRequiredService<AccountConnectionRevisions>();

    /// <summary>
    /// Gets the virtual clock.
    /// </summary>
    public VirtualTimeProvider Time => (VirtualTimeProvider)_provider.GetRequiredService<TimeProvider>();

    /// <summary>
    /// Gets the recording sender.
    /// </summary>
    public RecordingSender Sender => _provider.GetRequiredService<RecordingSender>();

    /// <summary>
    /// Gets the capturing logger.
    /// </summary>
    public CapturingLogger<InboxWatcher> Logger => _provider.GetRequiredService<CapturingLogger<InboxWatcher>>();

    /// <summary>
    /// Gets the ordered list of sync and wait events.
    /// </summary>
    public EventLog Events => _provider.GetRequiredService<EventLog>();

    /// <summary>
    /// Creates a harness with an empty database.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The harness.</returns>
    public static Task<WatcherHarness> CreateAsync(CancellationToken cancellationToken)
    {
        return CreateAsync(null, cancellationToken);
    }

    /// <summary>
    /// Creates a harness with an empty database, letting the caller replace registrations.
    /// </summary>
    /// <param name="configure">Applied after the default registrations, so it can override them; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The harness.</returns>
    public static async Task<WatcherHarness> CreateAsync(Action<IServiceCollection>? configure, CancellationToken cancellationToken)
    {
        // Account loops query the global store concurrently, so every context needs its own connection (a SqliteConnection is not
        // thread safe). A named shared-cache database gives that while staying in memory; it lives as long as this first connection.
        var connectionString = $"Data Source=watcher-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var services = new ServiceCollection();
        services.AddSingleton<EventLog>();
        services.AddSingleton<CapturingLogger<InboxWatcher>>();
        services.AddSingleton<ILogger<InboxWatcher>>(sp => sp.GetRequiredService<CapturingLogger<InboxWatcher>>());
        services.AddSingleton<TimeProvider>(new VirtualTimeProvider());
        services.AddSingleton<IImapSessionFactory>(new FakeImapSessionFactory());
        services.AddSingleton<RecordingSender>();
        services.AddSingleton<ISender>(sp => sp.GetRequiredService<RecordingSender>());
        services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton<InMemoryAccountDbContextFactory>();
        services.AddSingleton<IAccountDbContextFactory>(sp => sp.GetRequiredService<InMemoryAccountDbContextFactory>());
        services.AddScoped<AccountContextProvider>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddSingleton<AccountConnectionRevisions>();
        services.AddSingleton<InboxWatcher>();
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider(validateScopes: true);
        await using (var setup = provider.CreateAsyncScope())
        {
            await SqliteSchemaInitializer.InitializeGlobalAsync(setup.ServiceProvider.GetRequiredService<GlobalDbContext>(), cancellationToken);
        }

        return new WatcherHarness(connection, provider, provider.GetRequiredService<InboxWatcher>());
    }

    /// <summary>
    /// A wait that never reports a change and ends only when cancelled.
    /// </summary>
    /// <param name="folder">The watched folder.</param>
    /// <param name="maxWait">The maximum wait.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Never completes normally.</returns>
    public static async Task<bool> BlockUntilCancelled(string folder, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return false;
    }

    /// <summary>
    /// Adds an account, optionally with an Inbox folder.
    /// </summary>
    /// <param name="withInbox">Whether to add the Inbox folder too.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The account.</returns>
    public async Task<MailAccount> AddAccountAsync(bool withInbox, CancellationToken cancellationToken)
    {
        var account = TestData.Account($"{Guid.NewGuid():N}@example.com");
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAccountRepository>().AddAsync(account, cancellationToken);
        if (withInbox)
        {
            await scope.ServiceProvider.GetRequiredService<IFolderRepository>()
                .AddAsync(MailFolder.Create(account.Id, "INBOX", "Inbox", FolderRole.Inbox), cancellationToken);
        }

        return account;
    }

    /// <summary>
    /// Adds the Inbox folder to an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the folder is saved.</returns>
    public async Task AddInboxAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IFolderRepository>()
            .AddAsync(MailFolder.Create(accountId, "INBOX", "Inbox", FolderRole.Inbox), cancellationToken);
    }

    /// <summary>
    /// Deletes an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the account is deleted.</returns>
    public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAccountRepository>().DeleteAsync(accountId, cancellationToken);
    }

    /// <summary>
    /// Waits (in real time, bounded) until a condition holds; used only to let background loops reach a state, never to elapse virtual time.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    public static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("The watcher did not reach the expected state.");
            }

            await Task.Delay(5, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Watcher.StopLoopsAsync();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>
/// A thread-safe ordered log of events.
/// </summary>
internal sealed class EventLog
{
    /// <summary>
    /// The events.
    /// </summary>
    private readonly List<string> _events = [];

    /// <summary>
    /// Appends an event.
    /// </summary>
    /// <param name="name">The event name.</param>
    public void Add(string name)
    {
        lock (_events)
        {
            _events.Add(name);
        }
    }

    /// <summary>
    /// Copies the events so far.
    /// </summary>
    /// <returns>The events.</returns>
    public string[] Snapshot()
    {
        lock (_events)
        {
            return [.. _events];
        }
    }
}

/// <summary>
/// An <see cref="ISender"/> that records <see cref="SyncAccountCommand"/>s instead of running them.
/// </summary>
/// <param name="events">The shared event log.</param>
internal sealed class RecordingSender(EventLog events) : ISender
{
    /// <summary>
    /// The synced account identifiers.
    /// </summary>
    private readonly List<Guid> _synced = [];

    /// <summary>
    /// Gets the account identifiers a sync was requested for.
    /// </summary>
    public IReadOnlyList<Guid> SyncedAccounts
    {
        get
        {
            lock (_synced)
            {
                return [.. _synced];
            }
        }
    }

    /// <summary>
    /// Gets or sets an exception to throw from the sync command.
    /// </summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
    {
        if (command is SyncAccountCommand sync)
        {
            events.Add("sync");
            lock (_synced)
            {
                _synced.Add(sync.AccountId);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return ValueTask.FromResult((TResponse)(object)new SyncResult(0, 0, []));
        }

        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// A logger that keeps what was logged.
/// </summary>
/// <typeparam name="T">The category type.</typeparam>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>
    /// The captured entries.
    /// </summary>
    private readonly List<(LogLevel Level, Exception? Exception)> _entries = [];

    /// <summary>
    /// Gets a copy of the captured entries.
    /// </summary>
    public IReadOnlyList<(LogLevel Level, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, exception));
        }
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire only when <see cref="Advance"/> moves the virtual clock past them.
/// </summary>
internal sealed class VirtualTimeProvider : TimeProvider
{
    /// <summary>
    /// Guards the clock and the timer list.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// The active timers.
    /// </summary>
    private readonly List<VirtualTimer> _timers = [];

    /// <summary>
    /// The current virtual time.
    /// </summary>
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Gets the number of timers that are armed and will fire in the future.
    /// </summary>
    public int PendingTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Next is not null);
            }
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new VirtualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock forward, firing every timer that falls due on the way in due-time order.
    /// </summary>
    /// <param name="span">The amount to advance.</param>
    public void Advance(TimeSpan span)
    {
        var target = GetUtcNow() + span;
        while (true)
        {
            VirtualTimer? due;
            lock (_gate)
            {
                due = _timers.Where(t => t.Next is not null && t.Next <= target).OrderBy(t => t.Next).FirstOrDefault();
                if (due is null)
                {
                    _now = target;
                    return;
                }

                _now = due.Next!.Value;
            }

            due.Fire();
        }
    }

    /// <summary>
    /// Removes a timer from the active list.
    /// </summary>
    /// <param name="timer">The timer.</param>
    private void Remove(VirtualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>
    /// A timer driven by <see cref="VirtualTimeProvider"/>.
    /// </summary>
    /// <param name="owner">The owning clock.</param>
    /// <param name="callback">The callback.</param>
    /// <param name="state">The callback state.</param>
    private sealed class VirtualTimer(VirtualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        /// <summary>
        /// The repeat period, or null for a one-shot timer.
        /// </summary>
        private TimeSpan? _period;

        /// <summary>
        /// Gets the next due time, or null when the timer is not armed.
        /// </summary>
        public DateTimeOffset? Next { get; private set; }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Next = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                _period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }

            return true;
        }

        /// <summary>
        /// Runs the callback and schedules the next repetition, if any.
        /// </summary>
        public void Fire()
        {
            lock (owner._gate)
            {
                Next = _period is null ? null : owner._now + _period;
            }

            callback(state);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            owner.Remove(this);
            Next = null;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
