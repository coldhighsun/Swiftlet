using CommunityToolkit.Mvvm.ComponentModel;
using Swiftlet.Application;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the splash screen, shown while the application starts.
/// </summary>
public sealed partial class SplashViewModel : ObservableObject, IProgress<StartupProgress>
{
    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Brings progress to the UI thread.
    /// </summary>
    private readonly IUiThread _uiThread;

    /// <summary>
    /// Initializes a new instance of the <see cref="SplashViewModel"/> class, showing the first step.
    /// </summary>
    /// <param name="localizer">The localizer.</param>
    /// <param name="uiThread">Brings progress to the UI thread.</param>
    public SplashViewModel(ILocalizer localizer, IUiThread uiThread)
    {
        _localizer = localizer;
        _uiThread = uiThread;
        Status = Describe(new StartupProgress(StartupStage.CheckingDatabase));
    }

    /// <summary>
    /// Gets the version of the application.
    /// </summary>
    public string Version { get; } = AppVersion.Display;

    /// <summary>
    /// Gets or sets the text naming the current step.
    /// </summary>
    [ObservableProperty]
    public partial string Status { get; set; }

    /// <summary>
    /// Shows the step the startup reached; may be called from any thread.
    /// </summary>
    /// <param name="value">The step.</param>
    public void Report(StartupProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = _uiThread.InvokeAsync(() =>
        {
            Status = Describe(value);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Gets the text of a step.
    /// </summary>
    /// <param name="progress">The step.</param>
    /// <returns>The localized text.</returns>
    private string Describe(StartupProgress progress)
    {
        return progress.Stage switch
        {
            StartupStage.CheckingDatabase => _localizer["Splash_CheckingDatabase"],
            StartupStage.OpeningDatabase => _localizer["Splash_OpeningDatabase"],
            StartupStage.OpeningAccount => _localizer.Format("Splash_OpeningAccount", progress.Current, progress.Total),
            StartupStage.StartingServices => _localizer["Splash_StartingServices"],
            StartupStage.LoadingWindow => _localizer["Splash_LoadingWindow"],
            _ => throw new ArgumentOutOfRangeException(nameof(progress), progress.Stage, null),
        };
    }
}
