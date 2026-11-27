using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Resilience;

namespace Swiftlet.Infrastructure.Sync;

/// <summary>
/// Background service that downloads the older history of every account in small batches after the first sync has fetched the newest messages.
/// Each batch runs under the account's <see cref="SyncCoordinator"/> lock and releases it afterwards. A batch also ends early when a regular sync,
/// an IDLE-triggered sync or the Sync button waits for the lock, so those are only held up by the downloads already running.
/// </summary>
/// <param name="scopeFactory">The service scope factory.</param>
/// <param name="imapFactory">Opens the dedicated backfill connection of an account.</param>
/// <param name="coordinator">Serializes the batches with the regular syncs of the same account.</param>
/// <param name="events">Receives the progress notifications.</param>
/// <param name="settings">The user-configurable sync behavior, read before every batch.</param>
/// <param name="options">The synchronization limits.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="revisions">Reports changed connection settings, which end the running pass and lift the pause of the account.</param>
/// <param name="logger">The logger.</param>
public sealed class BackfillWorker(
    IServiceScopeFactory scopeFactory,
    IImapSessionFactory imapFactory,
    SyncCoordinator coordinator,
    SyncEvents events,
    ISyncSettings settings,
    MailSyncOptions options,
    TimeProvider timeProvider,
    AccountConnectionRevisions revisions,
    ILogger<BackfillWorker> logger) : BackgroundService
{
    /// <summary>
    /// How often the worker looks for accounts that still need a history pass.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The pause between two batches of one account, which keeps the CPU, disk and server load modest and lets waiting syncs go first.
    /// </summary>
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a finished account is left alone before it is checked again (a folder created later has history that was never fetched).
    /// </summary>
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// How long an account waits after a failed pass (offline, bad credentials) before the next attempt.
    /// </summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many times a pass that failed with a passing error is repeated at once, on a new connection, before the account waits <see cref="RetryInterval"/>.
    /// </summary>
    private const int Retries = 3;

    /// <summary>
    /// The running passes by account identifier.
    /// </summary>
    private readonly Dictionary<Guid, Task> _passes = [];

    /// <summary>
    /// The earliest time each account may start another pass.
    /// </summary>
    private readonly Dictionary<Guid, DateTimeOffset> _notBefore = [];

    /// <summary>
    /// The connection settings revision each account's latest pass was started with.
    /// </summary>
    private readonly Dictionary<Guid, int> _passRevisions = [];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval, timeProvider);
            do
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A failing database read must not kill the worker; the next tick tries again.
                    logger.LogError(ex, "Reconciling the history backfill failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown requested; the passes observe the same token and end themselves.
        }
        finally
        {
            await Task.WhenAll(_passes.Values);
        }
    }

    /// <summary>
    /// Starts a pass for every account that has none running and is not waiting out a pause.
    /// </summary>
    /// <param name="stoppingToken">The token that ends every pass on shutdown.</param>
    /// <returns>A task that completes when the passes have been started.</returns>
    internal async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        foreach (var id in _passes.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToList())
        {
            _passes.Remove(id);
        }

        if (!settings.DownloadFullHistory)
        {
            return;
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
            if (_passes.ContainsKey(accountId))
            {
                continue;
            }

            // A pause earned with outdated connection settings says nothing about the new ones.
            var revision = revisions.Get(accountId);
            if (_notBefore.TryGetValue(accountId, out var due) && due > now && _passRevisions.GetValueOrDefault(accountId) == revision)
            {
                continue;
            }

            _passRevisions[accountId] = revision;
            _passes[accountId] = Task.Run(() => RunPassAsync(account, stoppingToken), CancellationToken.None);
        }
    }

    /// <summary>
    /// Waits until every pass that is running now has ended.
    /// </summary>
    /// <returns>A task that completes when the running passes are done.</returns>
    internal Task WhenPassesCompleteAsync()
    {
        return Task.WhenAll(_passes.Values);
    }

    /// <summary>
    /// Runs one history pass of an account and schedules the next one according to how it ended.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">The shutdown token.</param>
    /// <returns>A task that completes when the pass is over.</returns>
    private async Task RunPassAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var downloaded = 0;
        try
        {
            var pipeline = RetryPipelines.Transient(
                timeProvider,
                Retries,
                RetryPipelines.InitialDelay,
                logger,
                $"History backfill of {account.EmailAddress}");
            var complete = await pipeline.ExecuteAsync(
                async token =>
                {
                    // A repeated attempt resumes from the cache, so what earlier attempts downloaded still counts.
                    var earlier = downloaded;
                    return await BackfillAccountAsync(account, count => downloaded = earlier + count, token);
                },
                cancellationToken);
            _notBefore[account.Id] = timeProvider.GetUtcNow() + (complete ? RecheckInterval : TimeSpan.FromMinutes(1));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Offline, revoked sign-in or a server refusing the connection: nothing here is fatal, the next attempt starts from the cache.
            logger.LogWarning(ex, "History backfill of account {Account} failed after {Count} messages; retrying later.", account.EmailAddress, downloaded);
            _notBefore[account.Id] = timeProvider.GetUtcNow() + RetryInterval;
        }

        if (downloaded > 0)
        {
            events.RaiseBackfillProgressed(account.Id, downloaded, true);
        }
    }

    /// <summary>
    /// Downloads the history of every eligible folder of an account.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="onProgress">Receives the running total of downloaded messages.</param>
    /// <param name="cancellationToken">The shutdown token.</param>
    /// <returns><see langword="true"/> if every folder ended with nothing older left; <see langword="false"/> if some folder could not be finished yet.</returns>
    internal async Task<bool> BackfillAccountAsync(MailAccount account, Action<int> onProgress, CancellationToken cancellationToken)
    {
        var revision = revisions.Get(account.Id);
        IReadOnlyList<Domain.Folders.MailFolder> targets;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            // Read again after the revision, so the connections never use settings older than the revision stands for.
            var current = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAsync(account.Id, cancellationToken);
            if (current is null)
            {
                return true;
            }

            account = current;
            targets = await scope.ServiceProvider.GetRequiredService<MailBackfillService>()
                .GetFoldersToBackfillAsync(account.Id, cancellationToken);
        }

        if (targets.Count == 0)
        {
            return false;
        }

        // The connections serve every batch, so the (possibly OAuth) sign-in is not repeated per batch.
        await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
        await using var connections = new BackfillConnections(
            session,
            options.ConnectionsPerAccount - 1,
            token => imapFactory.ConnectAsync(account, token),
            logger);
        var downloaded = 0;
        var complete = true;
        foreach (var folder in targets)
        {
            while (settings.DownloadFullHistory && revisions.Get(account.Id) == revision)
            {
                var before = downloaded;
                var result = await coordinator.RunExclusiveAsync(
                    account.Id,
                    async () =>
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        return await scope.ServiceProvider.GetRequiredService<MailBackfillService>()
                            .BackfillBatchAsync(
                                connections,
                                account.Id,
                                folder.Id,
                                (stored, remaining) =>
                                {
                                    // Every committed message is reported, so the UI shows live progress instead of one jump per batch.
                                    downloaded = before + stored;
                                    onProgress(downloaded);
                                    events.RaiseBackfillProgressed(account.Id, downloaded, false, folder.FullName, remaining);
                                },
                                () => coordinator.HasWaiters(account.Id),
                                cancellationToken);
                    },
                    cancellationToken);

                if (result.Added > 0)
                {
                    downloaded = before + result.Added;
                    onProgress(downloaded);
                    events.RaiseAccountSynced(account.Id, result.Added);
                }

                if (result.Outcome != BackfillOutcome.Progressed)
                {
                    // A folder with nothing cached yet has no history to fetch until the regular sync stores its newest messages;
                    // it must not keep the account in the one-minute retry loop, so it counts as done for this pass.
                    complete &= result.Outcome is BackfillOutcome.Completed or BackfillOutcome.NotStarted;
                    break;
                }

                await Task.Delay(BatchDelay, timeProvider, cancellationToken);
            }
        }

        logger.LogInformation("History backfill of {Account} finished a pass: {Count} messages, complete={Complete}.", account.EmailAddress, downloaded, complete);
        return complete && settings.DownloadFullHistory && revisions.Get(account.Id) == revision;
    }
}
