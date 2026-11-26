using System.Threading.Channels;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Tells the background flag push which account has a local read or star change to write to the server.
/// </summary>
public sealed class FlagPushSignal
{
    /// <summary>
    /// The accounts with new changes, in the order of the changes.
    /// </summary>
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    /// <summary>
    /// Records that an account has a new change.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    public void Notify(Guid accountId)
    {
        _channel.Writer.TryWrite(accountId);
    }

    /// <summary>
    /// Waits for the next notification and consumes it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The account identifier.</returns>
    public ValueTask<Guid> ReadAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }
}
