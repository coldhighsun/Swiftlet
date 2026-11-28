namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Runs the preparation behind the splash screen.
/// </summary>
public static class SplashStartup
{
    /// <summary>
    /// Runs the preparation on the thread pool and completes once it has ended and the minimum duration has passed, so
    /// that a fast start does not only flash the splash screen. Both run side by side.
    /// </summary>
    /// <param name="prepare">The preparation.</param>
    /// <param name="minimumDuration">How long this takes at least.</param>
    /// <param name="timeProvider">Measures the duration.</param>
    /// <returns>The result of the preparation.</returns>
    public static async Task<bool> RunAsync(Func<Task<bool>> prepare, TimeSpan minimumDuration, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var delay = Task.Delay(minimumDuration, timeProvider);

        // SQLite works synchronously, so the preparation runs elsewhere to keep the splash screen moving.
        var preparation = Task.Run(prepare);
        await Task.WhenAll(preparation, delay);
        return await preparation;
    }
}
