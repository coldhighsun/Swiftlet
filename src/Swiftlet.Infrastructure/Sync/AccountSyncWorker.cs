using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;

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
        await using var scope = scopeFactory.CreateAsyncScope();
        var accounts = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAllAsync(cancellationToken);
        var due = schedule.GetDue(accounts, timeProvider.GetUtcNow());
        if (due.Count == 0)
        {
            return;
        }

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        foreach (var account in due)
        {
            schedule.MarkAttempted(account.Id, timeProvider.GetUtcNow());
            try
            {
                await sender.Send(new SyncAccountCommand(account.Id), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failing account (network down, bad credentials) must not stop the loop for the other accounts.
                logger.LogError(ex, "Background sync failed for account {Account}.", account.EmailAddress);
            }
        }
    }
}
