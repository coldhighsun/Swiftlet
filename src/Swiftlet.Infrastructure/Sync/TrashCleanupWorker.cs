using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Sync;

/// <summary>
/// Background service that permanently deletes mail that has been in Trash longer than its account keeps it, shortly after
/// startup and then every hour. Each batch runs under the account's <see cref="SyncCoordinator"/> lock and releases it
/// afterwards. A failure is not repeated at once: the next pass tries again.
/// </summary>
/// <param name="scopeFactory">The service scope factory.</param>
/// <param name="imapFactory">Opens the connection used for an account.</param>
/// <param name="coordinator">Keeps the batches apart from the syncs of the same account.</param>
/// <param name="events">Receives the report of what was deleted.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="revisions">Reports changed connection settings, which end the work on a connection.</param>
/// <param name="logger">The logger.</param>
public sealed class TrashCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IImapSessionFactory imapFactory,
    SyncCoordinator coordinator,
    SyncEvents events,
    TimeProvider timeProvider,
    AccountConnectionRevisions revisions,
    ILogger<TrashCleanupWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long after startup the first pass begins, so it does not compete with the first load and sync.
    /// </summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The time between two passes.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, timeProvider, stoppingToken);
            while (true)
            {
                try
                {
                    await RunPassAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A failing database read must not kill the worker; the next pass tries again.
                    logger.LogError(ex, "The Trash cleanup pass failed.");
                }

                await Task.Delay(Interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown requested; what is left has still expired at the next start.
        }
    }

    /// <summary>
    /// Cleans the Trash of every account that asks for it, one after the other.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when all accounts have been handled.</returns>
    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MailAccount> accounts;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            accounts = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAllAsync(cancellationToken);
        }

        foreach (var account in accounts.Where(a => a.TrashRetentionDays is not null))
        {
            using var logScope = logger.BeginMailScope(account.EmailAddress);
            await CleanAsync(account.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Deletes everything expired in one account's Trash and reports it; failures end the work on this account only.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the account has been handled.</returns>
    private async Task CleanAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var deleted = 0;
        string? refusal = null;
        try
        {
            // Read before the account, so a change in between is noticed.
            var revision = revisions.Get(accountId);
            MailAccount? account;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                account = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAsync(accountId, cancellationToken);
            }

            if (account is null)
            {
                return;
            }

            await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
            TrashCleanupResult? result;
            do
            {
                result = await RunBatchAsync(session, accountId, revision, cancellationToken);
                deleted += result?.Deleted ?? 0;
            }
            while (result is { Remaining: > 0 });
        }
        catch (Exception ex) when (ex is MailOperationRejectedException or NotSupportedException)
        {
            // The user chose the cleanup, so they are told that the server does not allow it.
            logger.LogWarning(ex, "The server refused to clean up Trash.");
            refusal = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A lost connection or an expired sign-in only postpones the work; the other accounts are still handled.
            logger.LogWarning(ex, "Cleaning up Trash failed; trying again later.");
        }

        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} expired messages from Trash.", deleted);
        }

        if (deleted > 0 || refusal is not null)
        {
            events.RaiseDeletionProgressed(new DeletionProgressEventArgs(accountId, deleted, 0, true, refusal));
        }
    }

    /// <summary>
    /// Runs one batch under the account's lock, with the settings the account has by then.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="revision">The connection settings revision the session was opened with.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result, or <see langword="null"/> when there is nothing more to do on this connection.</returns>
    private async Task<TrashCleanupResult?> RunBatchAsync(
        IImapSession session,
        Guid accountId,
        int revision,
        CancellationToken cancellationToken)
    {
        return await coordinator.RunExclusiveAsync(
            accountId,
            async () =>
            {
                // Checked under the lock: nothing may be deleted on a server the account no longer uses.
                if (revisions.Get(accountId) != revision)
                {
                    return null;
                }

                await using var scope = scopeFactory.CreateAsyncScope();

                // The account may have been removed or its cleanup switched off meanwhile.
                var account = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAsync(accountId, cancellationToken);
                return account is null
                    ? null
                    : await scope.ServiceProvider.GetRequiredService<TrashCleanupService>()
                        .ProcessBatchAsync(session, account, cancellationToken);
            },
            cancellationToken);
    }
}
