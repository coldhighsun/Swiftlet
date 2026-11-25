using Avalonia.Threading;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// The <see cref="IUiThread"/> implementation that marshals to the Avalonia UI thread.
/// </summary>
public sealed class AvaloniaUiThread : IUiThread
{
    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> action)
    {
        return Dispatcher.UIThread.InvokeAsync(action);
    }
}
