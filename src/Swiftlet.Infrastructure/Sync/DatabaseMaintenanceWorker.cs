using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Persistence;

namespace Swiftlet.Infrastructure.Sync;

/// <summary>
/// Background service that keeps the account databases small, shortly after startup and then every few hours.
/// Every step runs under the account's <see cref="SyncCoordinator"/> lock. Nothing is started during shutdown, and a
/// running step is interrupted by it.
/// </summary>
/// <param name="scopeFactory">The service scope factory.</param>
/// <param name="databases">Opens the account databases.</param>
/// <param name="maintenance">Performs the maintenance steps.</param>
/// <param name="coordinator">Keeps the steps apart from the syncs of the same account.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed class DatabaseMaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    IAccountDbContextFactory databases,
    AccountDatabaseMaintenance maintenance,
    SyncCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<DatabaseMaintenanceWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long after startup the first pass begins, so it does not compete with the first load and sync.
    /// </summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The time between two passes.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

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
                    logger.LogError(ex, "The database maintenance pass failed.");
                }

                await Task.Delay(Interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown requested; unfinished work is recorded in the database and done by a later run.
        }
    }

    /// <summary>
    /// Maintains every account database once, one after the other.
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

        foreach (var account in accounts)
        {
            using var logScope = logger.BeginMailScope(account.EmailAddress);
            try
            {
                await MaintainAsync(account.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A busy database or a full disk only postpones the work of this account; the others are still handled.
                logger.LogWarning(ex, "Maintaining the database of account {Account} failed; trying again later.", account.EmailAddress);
            }
        }
    }

    /// <summary>
    /// Compresses leftover HTML bodies batch by batch, releasing the lock in between, then compacts if needed.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the account's database needs nothing more.</returns>
    private async Task MaintainAsync(Guid accountId, CancellationToken cancellationToken)
    {
        long? row = 0;
        while (row is { } after)
        {
            row = await RunStepAsync(accountId, context => maintenance.CompressHtmlBatchAsync(context, after, cancellationToken), cancellationToken);
        }

        var result = await RunStepAsync(accountId, context => maintenance.CompactIfNeededAsync(context, cancellationToken), cancellationToken);
        if (result is not null)
        {
            logger.LogInformation(
                "Compacted the database of account {AccountId} from {BytesBefore} to {BytesAfter} bytes (search index repaired: {Repaired})",
                accountId,
                result.BytesBefore,
                result.BytesAfter,
                result.IndexRepaired);
        }
    }

    /// <summary>
    /// Runs one step under the account's lock, unless the account has been removed.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="step">The step.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The step's result, or the default value when the account is gone.</returns>
    private async Task<T?> RunStepAsync<T>(Guid accountId, Func<AccountDbContext, Task<T>> step, CancellationToken cancellationToken)
    {
        return await coordinator.RunExclusiveAsync(
            accountId,
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    // Opening the database of a removed account would create its file again.
                    if (await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAsync(accountId, cancellationToken) is null)
                    {
                        return default(T?);
                    }
                }

                await using var context = await databases.CreateAsync(accountId, cancellationToken);
                return await step(context);
            },
            cancellationToken);
    }
}
