using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Resilience;

namespace Swiftlet.Infrastructure.Sync;

/// <summary>
/// Background service that carries out pending deletions on the mail servers, one run per account at a time.
/// Each batch runs under the account's <see cref="SyncCoordinator"/> lock and releases it afterwards. A lost connection is
/// retried until it works; the pending marks are stored, so deletions also survive a restart.
/// </summary>
/// <param name="scopeFactory">The service scope factory.</param>
/// <param name="imapFactory">Opens the connection of a run.</param>
/// <param name="coordinator">Serializes the batches with the syncs of the same account.</param>
/// <param name="signal">Wakes the worker when messages were deleted.</param>
/// <param name="events">Receives the progress notifications.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="revisions">Reports changed connection settings, which replace the connection and lift a block.</param>
/// <param name="logger">The logger.</param>
/// <param name="names">Resolves the names shown in the log; optional.</param>
public sealed class DeletionWorker(
    IServiceScopeFactory scopeFactory,
    IImapSessionFactory imapFactory,
    SyncCoordinator coordinator,
    DeletionSignal signal,
    SyncEvents events,
    TimeProvider timeProvider,
    AccountConnectionRevisions revisions,
    ILogger<DeletionWorker> logger,
    LogNames? names = null) : BackgroundService
{
    /// <summary>
    /// The names shown in the log; identifiers when no directory is supplied.
    /// </summary>
    private readonly LogNames _names = names ?? new LogNames();

    /// <summary>
    /// How often the worker looks for pending deletions when nobody signals, which picks up those left from the last run.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an account waits after a failure that retrying at once cannot fix, such as a rejected password.
    /// </summary>
    internal static readonly TimeSpan BlockedInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The running passes by account identifier.
    /// </summary>
    private readonly Dictionary<Guid, Task> _runs = [];

    /// <summary>
    /// Guards <see cref="_active"/> and <see cref="_rerun"/>, so a request for another pass cannot slip past an ending run.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The accounts whose run still takes requests for another pass.
    /// </summary>
    private readonly HashSet<Guid> _active = [];

    /// <summary>
    /// The active accounts that need another pass, because new work may have arrived after the current one looked.
    /// </summary>
    private readonly HashSet<Guid> _rerun = [];

    /// <summary>
    /// The earliest time each blocked account may start another run, with the connection settings revision that was blocked;
    /// written by the runs and read by the reconcile pass.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset Until, int Revision)> _notBefore = new();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A failing database read must not kill the worker; the next tick tries again.
                    logger.LogError(ex, "Reconciling the pending deletions failed.");
                }

                // Cancelling the signal wait when the poll interval wins keeps it from swallowing a later notification.
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var signalled = signal.WaitAsync(wake.Token);
                var polled = Task.Delay(PollInterval, timeProvider, wake.Token);
                await Task.WhenAny(signalled, polled);
                await wake.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown requested; the runs observe the same token and end themselves.
        }
        finally
        {
            await Task.WhenAll(_runs.Values);
        }
    }

    /// <summary>
    /// Starts a run for every account that has none and is not waiting out a failure, and asks every running one for another
    /// pass, so a signal that arrives while a run is ending is not lost until the next poll.
    /// </summary>
    /// <param name="stoppingToken">The token that ends every run on shutdown.</param>
    /// <returns>A task that completes when the runs have been started.</returns>
    internal async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        foreach (var id in _runs.Where(r => r.Value.IsCompleted).Select(r => r.Key).ToList())
        {
            _runs.Remove(id);
        }

        IReadOnlyList<MailAccount> accounts;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            accounts = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAllAsync(stoppingToken);
        }

        var now = timeProvider.GetUtcNow();
        foreach (var account in accounts)
        {
            var accountId = account.Id;
            lock (_gate)
            {
                if (_active.Contains(accountId))
                {
                    _rerun.Add(accountId);
                    continue;
                }

                if (IsBlocked(accountId, now))
                {
                    continue;
                }

                _active.Add(accountId);
            }

            _runs[accountId] = Task.Run(() => RunAccountAsync(accountId, stoppingToken), CancellationToken.None);
        }
    }

    /// <summary>
    /// Waits until every run that is active now has ended.
    /// </summary>
    /// <returns>A task that completes when the active runs are done.</returns>
    internal Task WhenRunsCompleteAsync()
    {
        return Task.WhenAll(_runs.Values);
    }

    /// <summary>
    /// Determines whether an account is still waiting out a failure of its current connection settings.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> if no pass may start yet.</returns>
    private bool IsBlocked(Guid accountId, DateTimeOffset now)
    {
        return _notBefore.TryGetValue(accountId, out var block)
            && block.Until > now
            && block.Revision == revisions.Get(accountId);
    }

    /// <summary>
    /// Makes passes over one account for as long as another one is requested.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The shutdown token.</param>
    /// <returns>A task that completes when no further pass is wanted or allowed.</returns>
    private async Task RunAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        using var logScope = logger.BeginMailScope(_names.Account(accountId));
        var released = false;
        try
        {
            while (!released)
            {
                await DeletePendingAsync(accountId, cancellationToken);
                lock (_gate)
                {
                    // Deciding to end and giving up the account happen in one step, so no request can fall in between.
                    if (!_rerun.Remove(accountId)
                        || cancellationToken.IsCancellationRequested
                        || IsBlocked(accountId, timeProvider.GetUtcNow()))
                    {
                        _active.Remove(accountId);
                        released = true;
                    }
                }
            }
        }
        finally
        {
            // Only an unexpected error leaves the loop without releasing; a later reconcile must be able to start again.
            if (!released)
            {
                lock (_gate)
                {
                    _active.Remove(accountId);
                    _rerun.Remove(accountId);
                }
            }
        }
    }

    /// <summary>
    /// Deletes everything pending for one account, retrying for as long as the failures may pass.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The shutdown token.</param>
    /// <returns>A task that completes when nothing is left, the account is blocked or the application shuts down.</returns>
    private async Task DeletePendingAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var deleted = 0;
        string? refusal = null;
        var revision = revisions.Get(accountId);
        var streak = new FailureStreak();
        var pipeline = RetryPipelines.Endless(
            timeProvider,
            TransientErrors.IsTransient,
            (error, delay) =>
            {
                logger.LogWarning(error, "Deleting messages of account {Account} failed; retrying in {Delay}.", _names.Account(accountId), delay);
                events.RaiseDeletionProgressed(new DeletionProgressEventArgs(accountId, deleted, 0, false, error.Message, true));
            });
        var context = RetryPipelines.Rent(streak, cancellationToken);
        try
        {
            await pipeline.ExecuteAsync(
                async resilience => await DrainAsync(
                    accountId,
                    () => revision = revisions.Get(accountId),
                    result =>
                    {
                        // Progress proves the connection works again, so a later failure starts with the shortest delay.
                        streak.Reset();
                        deleted += result.Deleted;
                        refusal = result.Error ?? refusal;
                        events.RaiseDeletionProgressed(
                            new DeletionProgressEventArgs(accountId, deleted, result.Remaining, false, result.Error));
                    },
                    resilience.CancellationToken),
                context);
            _notBefore.TryRemove(accountId, out _);
            if (deleted > 0 || refusal is not null)
            {
                events.RaiseDeletionProgressed(new DeletionProgressEventArgs(accountId, deleted, 0, true, refusal));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; the pending marks are stored and the next start continues.
        }
        catch (Exception ex)
        {
            // A rejected password, an expired sign-in or an unexpected error: the marks stay and a later run tries again.
            logger.LogWarning(ex, "Deleting messages of account {Account} is blocked; trying again later.", _names.Account(accountId));
            _notBefore[accountId] = (timeProvider.GetUtcNow() + BlockedInterval, revision);
            events.RaiseDeletionProgressed(new DeletionProgressEventArgs(accountId, deleted, 0, true, ex.Message, true));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Processes batches until nothing is pending, on a new connection whenever the connection settings changed.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="readRevision">Reads the connection settings revision; called before every connection.</param>
    /// <param name="onBatch">Told about every batch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when nothing is pending or the account is gone.</returns>
    private async Task DrainAsync(
        Guid accountId,
        Func<int> readRevision,
        Action<DeletionBatchResult> onBatch,
        CancellationToken cancellationToken)
    {
        while (await DrainConnectionAsync(accountId, readRevision(), onBatch, cancellationToken))
        {
            logger.LogInformation("The connection settings of account {Account} changed; deleting continues on a new connection.", _names.Account(accountId));
        }
    }

    /// <summary>
    /// Connects and processes batches until nothing is pending or the connection is outdated; does not connect when there is
    /// nothing to do.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="revision">The connection settings revision, read before the account.</param>
    /// <param name="onBatch">Told about every batch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the connection settings changed and another connection is needed.</returns>
    private async Task<bool> DrainConnectionAsync(
        Guid accountId,
        int revision,
        Action<DeletionBatchResult> onBatch,
        CancellationToken cancellationToken)
    {
        MailAccount? account;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            account = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAsync(accountId, cancellationToken);
            if (account is null
                || !await scope.ServiceProvider.GetRequiredService<PendingDeletionService>().HasWorkAsync(accountId, cancellationToken))
            {
                return false;
            }
        }

        await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
        var outdated = false;
        while (true)
        {
            var result = await coordinator.RunExclusiveAsync(
                accountId,
                async () =>
                {
                    // Checked under the lock: nothing may be deleted on a server the account no longer uses.
                    if (revisions.Get(accountId) != revision)
                    {
                        outdated = true;
                        return null;
                    }

                    await using var scope = scopeFactory.CreateAsyncScope();

                    // The account may have been removed while this run waited for the lock.
                    return await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAsync(accountId, cancellationToken) is null
                        ? null
                        : await scope.ServiceProvider.GetRequiredService<PendingDeletionService>()
                            .ProcessBatchAsync(session, accountId, cancellationToken);
                },
                cancellationToken);
            if (result is null)
            {
                return outdated;
            }

            onBatch(result);
        }
    }
}
