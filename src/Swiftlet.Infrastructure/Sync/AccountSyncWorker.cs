using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Resilience;

namespace Swiftlet.Infrastructure.Sync;

/// <summary>
/// Background service that synchronizes each account at its configured interval.
/// </summary>
/// <param name="scopeFactory">The service scope factory.</param>
/// <param name="schedule">The sync schedule.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
public sealed class AccountSyncWorker(
    IServiceScopeFactory scopeFactory,
    SyncSchedule schedule,
    TimeProvider timeProvider,
    ILogger<AccountSyncWorker> logger) : BackgroundService
{
    /// <summary>
    /// How often the worker checks which accounts are due.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times a sync that failed with a passing error is repeated before the account waits for its next interval.
    /// </summary>
    private const int Retries = 2;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Synchronizes every account that is currently due.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when all due accounts have been attempted.</returns>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MailAccount> due;
        await using (var listScope = scopeFactory.CreateAsyncScope())
        {
            var accounts = await listScope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAllAsync(cancellationToken);
            due = schedule.GetDue(accounts, timeProvider.GetUtcNow());
        }

        foreach (var account in due)
        {
            schedule.MarkAttempted(account.Id, timeProvider.GetUtcNow());
        }

        // Side by side, so one account waiting for a retry does not hold up the others.
        await Task.WhenAll(due.Select(account => SyncAsync(account, cancellationToken)));
    }

    /// <summary>
    /// Synchronizes one account, repeating the attempt after passing errors; a failure is logged, not thrown.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the account has been attempted.</returns>
    private async Task SyncAsync(MailAccount account, CancellationToken cancellationToken)
    {
        using var logScope = logger.BeginMailScope(account.EmailAddress);
        var pipeline = RetryPipelines.Transient(timeProvider, Retries, RetryPipelines.InitialDelay, logger, $"Sync of {account.EmailAddress}");
        try
        {
            await pipeline.ExecuteAsync(
                async token =>
                {
                    // One scope (and DbContext) per attempt: a failed SaveChanges leaves tracked entities behind that must not leak into another sync.
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                    await sender.Send(new SyncAccountCommand(account.Id), token);
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failing account (network down, bad credentials) must not stop the loop for the other accounts.
            logger.LogError(ex, "Background sync failed for account {Account}.", account.EmailAddress);
        }
    }
}
