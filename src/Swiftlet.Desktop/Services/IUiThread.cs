namespace Swiftlet.Desktop.Services;

/// <summary>
/// Runs work on the UI thread so view models need no static dispatcher access.
/// </summary>
public interface IUiThread
{
    /// <summary>
    /// Runs an asynchronous action on the UI thread.
    /// </summary>
    /// <param name="action">The action to run.</param>
    /// <returns>A task that completes when the action has finished.</returns>
    Task InvokeAsync(Func<Task> action);
}
