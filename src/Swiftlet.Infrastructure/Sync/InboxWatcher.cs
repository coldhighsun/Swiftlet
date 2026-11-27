using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Infrastructure.Resilience;

namespace Swiftlet.Infrastructure.Sync;

/// <summary>
/// Background service that keeps an IMAP IDLE connection open on the inbox of every account and triggers a sync when the server reports a change.
/// Accounts whose server lacks IDLE are left to the polling <see cref="AccountSyncWorker"/>.
/// </summary>
/// <param name="scopeFactory">The service scope factory.</param>
/// <param name="timeProvider">The clock used for every delay.</param>
/// <param name="revisions">Reports changed connection settings, which restart the loop of the account.</param>
/// <param name="logger">The logger.</param>
/// <param name="names">Resolves the names shown in the log; optional.</param>
public sealed class InboxWatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    AccountConnectionRevisions revisions,
    ILogger<InboxWatcher> logger,
    LogNames? names = null) : BackgroundService
{
    /// <summary>
    /// The names shown in the log; identifiers when no directory is supplied.
    /// </summary>
    private readonly LogNames _names = names ?? new LogNames();

    /// <summary>
    /// How often the set of watched accounts is reconciled with the stored accounts; also the poll interval while an account has no inbox folder yet.
    /// </summary>
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a single IDLE wait lasts before it is re-issued (servers drop idle connections after about 30 minutes).
    /// </summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The running loops by account identifier; only touched by the sequential reconcile passes and shutdown.
    /// </summary>
    private readonly Dictionary<Guid, AccountLoop> _loops = [];

    /// <summary>
    /// Gets the identifiers of the accounts that currently have a running watch loop.
    /// </summary>
    internal IReadOnlyCollection<Guid> RunningAccountIds => [.. _loops.Where(l => !l.Value.Task.IsCompleted).Select(l => l.Key)];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ReconcileInterval, timeProvider);
            do
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A failing database read (including a cancellation that is not our shutdown, such as a timeout) must not kill the watcher; the next tick tries again.
                    logger.LogError(ex, "Reconciling the inbox watchers failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown requested; the loops are stopped below.
        }
        finally
        {
            await StopLoopsAsync();
        }
    }

    /// <summary>
    /// Starts a loop for every account that has none, restarts the loops of accounts whose connection settings changed and
    /// stops the loops of accounts that no longer exist.
    /// </summary>
    /// <param name="stoppingToken">The token that ends every loop on shutdown.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    internal async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<MailAccount> accounts;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            accounts = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAllAsync(stoppingToken);
        }

        var existing = accounts.Select(a => a.Id).ToHashSet();
        foreach (var id in _loops.Keys.Where(id => !existing.Contains(id)).ToList())
        {
            await StopLoopAsync(id);
        }

        foreach (var id in existing)
        {
            var revision = revisions.Get(id);
            if (_loops.TryGetValue(id, out var current))
            {
                // A loop that ended because the server has no IDLE stays registered (and is not restarted) until the account is
                // removed or its connection settings change.
                if (current.Revision == revision && (!current.Task.IsCompleted || current.IdleUnsupported))
                {
                    continue;
                }

                await StopLoopAsync(id);
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var state = new AccountLoop(cts, revision);
            state.Task = Task.Run(() => RunAccountLoopAsync(id, state, cts.Token), CancellationToken.None);
            _loops[id] = state;
        }
    }

    /// <summary>
    /// Cancels every loop and waits for them to finish and dispose their sessions.
    /// </summary>
    /// <returns>A task that completes when all loops have ended.</returns>
    internal async Task StopLoopsAsync()
    {
        var loops = _loops.Values.ToList();
        _loops.Clear();

        // Cancel every loop first, then wait for all of them, so slow teardowns overlap instead of adding up.
        foreach (var loop in loops)
        {
            await loop.Cancellation.CancelAsync();
        }

        await Task.WhenAll(loops.Select(l => l.Task));
        foreach (var loop in loops)
        {
            loop.Cancellation.Dispose();
        }
    }

    /// <summary>
    /// Cancels one loop, waits for it to end and forgets it.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns>A task that completes when the loop has ended.</returns>
    private async Task StopLoopAsync(Guid accountId)
    {
        if (!_loops.Remove(accountId, out var loop))
        {
            return;
        }

        await loop.Cancellation.CancelAsync();
        await loop.Task;
        loop.Cancellation.Dispose();
    }

    /// <summary>
    /// Runs the watch cycle of one account until it is cancelled or the server turns out not to support IDLE.
    /// Any connection, protocol or database failure is retried with exponential backoff; the loop must outlive them all.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="state">The loop state.</param>
    /// <param name="cancellationToken">The token that ends the loop.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    private async Task RunAccountLoopAsync(Guid accountId, AccountLoop state, CancellationToken cancellationToken)
    {
        var accountName = _names.Account(accountId);
        using var logScope = logger.BeginMailScope(accountName);
        var streak = new FailureStreak();
        var pipeline = RetryPipelines.Endless(
            timeProvider,
            static _ => true,
            (error, delay) =>
            {
                if (error is OAuthReauthenticationRequiredException)
                {
                    // Retrying cannot help until the user signs in again, so it is checked only rarely.
                    logger.LogWarning(error, "Account {Account} needs to sign in again; IDLE watching is paused.", accountName);
                }
                else
                {
                    logger.LogWarning(error, "IDLE watching failed for account {Account}; retrying in {Delay}.", accountName, delay);
                }
            });
        var context = RetryPipelines.Rent(streak, cancellationToken);
        try
        {
            var outcome = await pipeline.ExecuteAsync(
                async resilience => await WatchAsync(accountId, streak.Reset, resilience.CancellationToken),
                context);
            if (outcome == WatchOutcome.IdleUnsupported)
            {
                state.IdleUnsupported = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The account was removed or the application is shutting down.
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Waits for the inbox folder, connects a dedicated session and alternates between waiting and syncing until it fails or is cancelled.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="onWaitCompleted">Called after every wait that actually idled and ended normally (not the first wait of a session), so the caller can reset its backoff.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Why the watch ended without throwing.</returns>
    private async Task<WatchOutcome> WatchAsync(Guid accountId, Action onWaitCompleted, CancellationToken cancellationToken)
    {
        MailAccount account;
        string inboxName;
        while (true)
        {
            MailFolder? inbox;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var found = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAsync(accountId, cancellationToken);
                if (found is null)
                {
                    return WatchOutcome.AccountGone;
                }

                account = found;
                var folders = await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetByAccountAsync(accountId, cancellationToken);
                inbox = folders.FirstOrDefault(f => f.Role == FolderRole.Inbox);
            }

            if (inbox is not null)
            {
                inboxName = inbox.FullName;
                break;
            }

            // The first sync has not created the folders yet; do not open a connection until there is something to watch.
            await Task.Delay(ReconcileInterval, timeProvider, cancellationToken);
        }

        IImapSession session;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            session = await scope.ServiceProvider.GetRequiredService<IImapSessionFactory>().ConnectAsync(account, cancellationToken);
        }

        await using (session)
        {
            if (!session.SupportsIdle)
            {
                logger.LogInformation("The server of account {Account} does not support IDLE; relying on periodic sync.", account.EmailAddress);
                return WatchOutcome.IdleUnsupported;
            }

            // The first wait of a session always reports a change (it records the baseline), so a sync follows every (re)connect and
            // the wait after it is compared against the baseline taken before that sync: mail arriving meanwhile is not lost.
            var firstWait = true;
            while (true)
            {
                var changed = await session.WaitForInboxChangeAsync(inboxName, MaxWait, cancellationToken);

                // The first wait returns right after the SELECT without idling, so only later waits prove IDLE works and reset the backoff.
                if (!firstWait)
                {
                    onWaitCompleted();
                }

                firstWait = false;
                if (changed)
                {
                    await SyncAsync(accountId, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Runs a sync of one account in a fresh scope, which opens its own connection; a failure is logged and swallowed.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the sync attempt is over.</returns>
    private async Task SyncAsync(Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new SyncAccountCommand(accountId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failed sync (offline, bad credentials, a timed-out token refresh) must not tear down the IDLE connection; the next change or poll retries.
            logger.LogError(ex, "IDLE-triggered sync failed for account {Account}.", _names.Account(accountId));
        }
    }

    /// <summary>
    /// Why a watch ended without an exception.
    /// </summary>
    private enum WatchOutcome
    {
        /// <summary>
        /// The account no longer exists.
        /// </summary>
        AccountGone,

        /// <summary>
        /// The server does not advertise IDLE.
        /// </summary>
        IdleUnsupported,
    }

    /// <summary>
    /// The bookkeeping of one running loop.
    /// </summary>
    /// <param name="Cancellation">The source that cancels the loop.</param>
    /// <param name="Revision">The connection settings revision the loop was started with.</param>
    private sealed record AccountLoop(CancellationTokenSource Cancellation, int Revision)
    {
        /// <summary>
        /// Gets or sets the loop task.
        /// </summary>
        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>
        /// Gets or sets a value indicating whether the loop ended because the server has no IDLE.
        /// </summary>
        public bool IdleUnsupported { get; set; }
    }
}
