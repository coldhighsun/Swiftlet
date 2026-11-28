using Avalonia;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Whether the graphics card draws the application. It is chosen once, before the first window, because neither the UI
/// framework nor the web view can switch while running. Drawing on the processor instead needs clearly less memory.
/// </summary>
internal static class HardwareRendering
{
    /// <summary>
    /// The browser switch that stops the web view from using the graphics card.
    /// </summary>
    private const string DisableGpuArgument = "--disable-gpu";

    /// <summary>
    /// Gets a value indicating whether this run uses the graphics card.
    /// </summary>
    public static bool IsEnabled { get; private set; } = true;

    /// <summary>
    /// Applies the choice to the application that is about to start.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="enabled">Whether the graphics card may be used.</param>
    /// <returns>The builder.</returns>
    public static AppBuilder UseHardwareRendering(this AppBuilder builder, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IsEnabled = enabled;
        if (enabled)
        {
            // The platform defaults pick the best renderer and fall back by themselves.
            return builder;
        }

        return builder
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Software] });
    }

    /// <summary>
    /// Builds the extra command line of the Windows web view.
    /// </summary>
    /// <param name="current">The arguments already requested, if any.</param>
    /// <param name="enabled">Whether the graphics card may be used.</param>
    /// <returns>The arguments to use.</returns>
    public static string? BrowserArguments(string? current, bool enabled)
    {
        if (enabled)
        {
            return current;
        }

        return string.IsNullOrWhiteSpace(current) ? DisableGpuArgument : $"{current} {DisableGpuArgument}";
    }
}
