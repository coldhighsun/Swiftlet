using System.Threading.Channels;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Wakes the background deletion when there is new work; several notifications before it wakes count as one.
/// </summary>
public sealed class DeletionSignal
{
    /// <summary>
    /// Holds at most one pending notification.
    /// </summary>
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>
    /// Records that there is new work.
    /// </summary>
    public void Notify()
    {
        _channel.Writer.TryWrite(true);
    }

    /// <summary>
    /// Waits until there is a notification and consumes it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when notified.</returns>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _channel.Reader.ReadAsync(cancellationToken);
    }
}
