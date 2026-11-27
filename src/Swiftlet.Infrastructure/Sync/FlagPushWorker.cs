using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Resilience;

namespace Swiftlet.Infrastructure.Sync;

/// <summary>
/// Background service that writes local read and star changes to the mail server right after they are made.
/// Each account has its own connection, kept for a while so that further changes go out without signing in again.
/// </summary>
/// <param name="scopeFactory">The service scope factory.</param>
/// <param name="imapFactory">Opens the connections.</param>
/// <param name="signal">Reports the accounts with new changes.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="revisions">Reports changed connection settings, which retire a kept connection.</param>
/// <param name="logger">The logger.</param>
public sealed class FlagPushWorker(
    IServiceScopeFactory scopeFactory,
    IImapSessionFactory imapFactory,
    FlagPushSignal signal,
    TimeProvider timeProvider,
    AccountConnectionRevisions revisions,
    ILogger<FlagPushWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long a change waits for further ones, so several quick changes go out together.
    /// </summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long an unused connection stays open.
    /// </summary>
    internal static readonly TimeSpan KeepConnection = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many times a push that failed with a passing error is repeated before it is left to the next sync.
    /// </summary>
    private const int Retries = 3;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var accounts = new Dictionary<Guid, (ChannelWriter<bool> Wake, Task Loop)>();
        try
        {
            while (true)
            {
                var accountId = await signal.ReadAsync(stoppingToken);
                if (!accounts.TryGetValue(accountId, out var account))
                {
                    // One slot: further notifications before the loop wakes up count as one.
                    var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
                    var loop = Task.Run(() => RunAccountAsync(accountId, channel.Reader, stoppingToken), CancellationToken.None);
                    account = (channel.Writer, loop);
                    accounts[accountId] = account;
                }

                account.Wake.TryWrite(true);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown requested; unwritten changes are stored as pending and the next sync writes them.
        }
        finally
        {
            await Task.WhenAll(accounts.Values.Select(a => a.Loop));
        }
    }

    /// <summary>
    /// Pushes the changes of one account whenever it is woken, until shutdown.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="wake">Signals new changes.</param>
    /// <param name="cancellationToken">The shutdown token.</param>
    /// <returns>A task that completes at shutdown.</returns>
    private async Task RunAccountAsync(Guid accountId, ChannelReader<bool> wake, CancellationToken cancellationToken)
    {
        IImapSession? session = null;
        var sessionRevision = 0;
        var pipeline = RetryPipelines.Transient(timeProvider, Retries, RetryPipelines.InitialDelay, logger, $"Flag push of account {accountId}");
        try
        {
            while (true)
            {
                if (session is not null && !await WaitWhileConnectedAsync(wake, cancellationToken))
                {
                    await session.DisposeAsync();
                    session = null;
                    continue;
                }

                if (session is null)
                {
                    await wake.ReadAsync(cancellationToken);
                }

                await Task.Delay(Debounce, timeProvider, cancellationToken);
                wake.TryRead(out _);
                try
                {
                    await pipeline.ExecuteAsync(
                        async token =>
                        {
                            // Read before the account, which PushAsync loads when it connects.
                            var revision = revisions.Get(accountId);
                            if (session is not null && revision != sessionRevision)
                            {
                                await session.DisposeAsync();
                                session = null;
                            }

                            sessionRevision = revision;
                            var reused = session is not null;
                            try
                            {
                                session = await PushAsync(accountId, session, token);
                            }
                            catch (Exception ex) when (!token.IsCancellationRequested)
                            {
                                // A failed connection is not used again; PushAsync disposes one it opened itself.
                                if (session is not null)
                                {
                                    await session.DisposeAsync();
                                    session = null;
                                }

                                if (!reused || !TransientErrors.IsTransient(ex))
                                {
                                    throw;
                                }

                                // A kept connection may simply have been dropped by the server, so a new one is tried at once.
                                session = await PushAsync(accountId, null, token);
                            }
                        },
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // The changes stay pending and the next regular sync writes them.
                    logger.LogWarning(ex, "Pushing flag changes of account {Account} failed; leaving them to the next sync.", accountId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Waits for the next change while a connection is open.
    /// </summary>
    /// <param name="wake">Signals new changes.</param>
    /// <param name="cancellationToken">The shutdown token.</param>
    /// <returns><see langword="true"/> on a new change; <see langword="false"/> when the connection has been unused for too long.</returns>
    private async Task<bool> WaitWhileConnectedAsync(ChannelReader<bool> wake, CancellationToken cancellationToken)
    {
        using var expired = new CancellationTokenSource(KeepConnection, timeProvider);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(expired.Token, cancellationToken);
        try
        {
            await wake.ReadAsync(either.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes every pending change of an account, connecting only when there is one.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="session">An open connection, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connection to keep, if any.</returns>
    private async Task<IImapSession?> PushAsync(Guid accountId, IImapSession? session, CancellationToken cancellationToken)
    {
        MailAccount? account;
        IReadOnlyList<Guid> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            account = await scope.ServiceProvider.GetRequiredService<IAccountRepository>().GetAsync(accountId, cancellationToken);
            if (account is null)
            {
                return session;
            }

            pending = await scope.ServiceProvider.GetRequiredService<PendingFlagService>().GetPendingFoldersAsync(accountId, cancellationToken);
        }

        if (pending.Count == 0)
        {
            return session;
        }

        var connection = session ?? await imapFactory.ConnectAsync(account, cancellationToken);
        try
        {
            Exception? failure = null;
            foreach (var folderId in pending)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<PendingFlagService>()
                        .PushFolderAsync(connection, accountId, folderId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One folder that refuses changes (read-only, shared) must not hold back the others.
                    failure ??= ex;
                }
            }

            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            }

            return connection;
        }
        catch when (session is null)
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
