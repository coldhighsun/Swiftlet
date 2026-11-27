using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.AutoUpdate;
using GitHubReleaseUpdater.Installation;
using GitHubReleaseUpdater.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Updates the application from its GitHub releases. Checking, downloading, the SHA-256 verification and starting the
/// installer are done by GitHubReleaseUpdater's <see cref="UpdateController"/>; this class adds the application's own
/// rules: which copies may install, and that nothing is installed while unsaved work is open.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService, IUpdatePresenter, IDisposable
{
    /// <summary>
    /// The owner of the repository the releases come from.
    /// </summary>
    internal const string RepositoryOwner = "coldhighsun";

    /// <summary>
    /// The name of the repository the releases come from.
    /// </summary>
    internal const string RepositoryName = "Swiftlet";

    /// <summary>
    /// Matches the Windows installer of a release (for example <c>Swiftlet-Setup-v1.2.3.exe</c>).
    /// </summary>
    internal const string InstallerAssetPattern = "Swiftlet-Setup-{tag}.exe";

    /// <summary>
    /// The uninstaller Inno Setup puts beside the application; its presence marks an installed copy.
    /// </summary>
    private const string UninstallerFileName = "unins000.exe";

    /// <summary>
    /// The least time between two automatic checks.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// The wait before the first check, so it does not compete with the application starting.
    /// </summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The updater, or <see langword="null"/> when this build does not look for updates.
    /// </summary>
    private readonly ReleaseUpdater? _updater;

    /// <summary>
    /// The update flow, or <see langword="null"/> when this build does not look for updates.
    /// </summary>
    private readonly UpdateController? _controller;

    /// <summary>
    /// Whether this copy was set up by the installer, so running a newer installer replaces it.
    /// </summary>
    private readonly bool _isInstalled;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Whether the installer of the running installation was started.
    /// </summary>
    private bool _installerStarted;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubUpdateService"/> class.
    /// </summary>
    /// <param name="updater">The updater, disposed with this instance; <see langword="null"/> to look for nothing.</param>
    /// <param name="settings">The settings, which switch the automatic checks.</param>
    /// <param name="isInstalled">Whether this copy can be replaced by running an installer.</param>
    /// <param name="downloadDirectory">The directory installers are downloaded to; used for nothing else.</param>
    /// <param name="hasUnsavedWork">Whether quitting now would lose something the user is working on.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="logger">The logger; optional.</param>
    /// <param name="initialDelay">The wait before the first automatic check; shortened by tests.</param>
    internal GitHubUpdateService(
        ReleaseUpdater? updater,
        IAppSettings settings,
        bool isInstalled,
        string downloadDirectory,
        Func<bool> hasUnsavedWork,
        ILocalizer localizer,
        ILogger<GitHubUpdateService>? logger = null,
        TimeSpan? initialDelay = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        _updater = updater;
        _isInstalled = isInstalled;
        _logger = logger ?? NullLogger<GitHubUpdateService>.Instance;
        if (updater is null)
        {
            return;
        }

        _controller = new UpdateController(updater, this, new UpdateControllerOptions
        {
            InitialDelay = initialDelay ?? InitialDelay,
            CheckInterval = CheckInterval,
            IsAutoCheckEnabled = () => settings.CheckForUpdates,
            DownloadDirectory = downloadDirectory,
            CleanDownloadDirectory = true,
            InstallerLaunchOptions = InstallerLaunchOptions.InnoSetupSilent,
            AskToInstallAfterManualCheck = false,
            BeforeInstallAsync = _ => hasUnsavedWork()
                ? Task.FromException(new InvalidOperationException(localizer["Update_CloseWindowsFirst"]))
                : Task.CompletedTask,
            BeforeInstallMustSucceed = true,
            OnError = ex => _logger.LogWarning(ex, "The update flow reported an error"),
        });
        _controller.Changed += (_, _) => OnControllerChanged();
        _controller.ProgressChanged += (_, _) => ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public event EventHandler? ProgressChanged;

    /// <inheritdoc />
    public event EventHandler? Announced;

    /// <inheritdoc />
    public event EventHandler? InstallerStarted;

    /// <inheritdoc />
    public bool IsSupported => _controller is not null;

    /// <inheritdoc />
    public UpdateStatus Status => _controller?.State switch
    {
        UpdateState.Checking => UpdateStatus.Checking,
        UpdateState.UpToDate => UpdateStatus.UpToDate,
        UpdateState.Available or UpdateState.Downloaded => UpdateStatus.Available,
        UpdateState.Downloading or UpdateState.Installing => UpdateStatus.Installing,
        UpdateState.Failed => UpdateStatus.Failed,
        _ => UpdateStatus.Idle,
    };

    /// <inheritdoc />
    public AvailableVersion? Available => _controller?.Available is { } update
        ? new AvailableVersion(
            update.Version.ToString(includeMetadata: false),
            update.Release.Body,
            ReleasePageOf(update.Release.HtmlUrl))
        : null;

    /// <inheritdoc />
    public bool CanInstall => _isInstalled && _controller is { CanInstall: true };

    /// <inheritdoc />
    public UpdateProgress? Progress => _controller?.Progress is { } progress
        ? new UpdateProgress(progress.BytesReceived, progress.TotalBytes, progress.BytesPerSecond)
        : null;

    /// <inheritdoc />
    public Exception? CheckError =>
        _controller?.LastFailure is { Stage: UpdateFailureStage.Check, UserInitiated: true } failure ? failure.Error : null;

    /// <inheritdoc />
    public Exception? InstallError =>
        _controller?.LastFailure is { Stage: not UpdateFailureStage.Check } failure ? failure.Error : null;

    /// <summary>
    /// Creates the service for the running application.
    /// </summary>
    /// <param name="version">The running version, without build metadata.</param>
    /// <param name="dataDirectory">The application data directory.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="hasUnsavedWork">Whether quitting now would lose something the user is working on.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="logger">The logger; optional.</param>
    /// <returns>The service; for a development build one that looks for nothing.</returns>
    public static GitHubUpdateService Create(
        string version,
        string dataDirectory,
        IAppSettings settings,
        Func<bool> hasUnsavedWork,
        ILocalizer localizer,
        ILogger<GitHubUpdateService>? logger = null)
    {
        var updater = TryGetReleaseVersion(version, out var current)
            ? new ReleaseUpdater(new UpdaterOptions
            {
                Owner = RepositoryOwner,
                Repo = RepositoryName,
                CurrentVersion = current,
                AssetSelector = new PatternAssetSelector(InstallerAssetPattern),
                LastCheckStore = new UpdateLastCheckStore(settings),
                MinimumCheckInterval = CheckInterval,
            })
            : null;
        var isInstalled = OperatingSystem.IsWindows()
            && File.Exists(Path.Combine(AppContext.BaseDirectory, UninstallerFileName));
        return new GitHubUpdateService(
            updater,
            settings,
            isInstalled,
            Path.Combine(dataDirectory, "updates"),
            hasUnsavedWork,
            localizer,
            logger);
    }

    /// <summary>
    /// Reads the version of a build that came from a release tag. MinVer gives builds without such a tag a version
    /// ending in <c>alpha.0</c> and the commit count; those have nothing sensible to compare releases with.
    /// </summary>
    /// <param name="version">The running version.</param>
    /// <param name="parsed">The version, when it is that of a release.</param>
    /// <returns><see langword="true"/> if updates are looked for.</returns>
    internal static bool TryGetReleaseVersion(
        string version,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SemanticVersion? parsed)
    {
        if (!SemanticVersion.TryParse(version, out parsed))
        {
            return false;
        }

        var isDevelopmentBuild = parsed.Prerelease == "alpha.0"
            || parsed.Prerelease.StartsWith("alpha.0.", StringComparison.Ordinal);
        if (isDevelopmentBuild)
        {
            parsed = null;
        }

        return !isDevelopmentBuild;
    }

    /// <inheritdoc />
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _controller?.RunAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CheckAsync(CancellationToken cancellationToken)
    {
        return _controller?.CheckAsync(manual: true, cancellationToken) ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> InstallAsync(CancellationToken cancellationToken)
    {
        if (!CanInstall)
        {
            return false;
        }

        _installerStarted = false;
        await _controller!.InstallAsync(cancellationToken);
        if (InstallError is { } error)
        {
            _logger.LogWarning(error, "The update could not be installed");
        }

        return _installerStarted;
    }

    /// <inheritdoc />
    public async Task SkipAsync()
    {
        if (_controller is not null)
        {
            await _controller.SkipVersionAsync();
        }
    }

    /// <summary>
    /// Disposes the updater.
    /// </summary>
    public void Dispose()
    {
        _updater?.Dispose();
    }

    /// <inheritdoc />
    void IUpdatePresenter.Announce(AvailableUpdate update)
    {
        _logger.LogInformation("Version {Version} is available", update.Version);
        Announced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Agrees to install: the controller is only asked to install after the user chose to.
    /// </summary>
    /// <param name="update">The release.</param>
    /// <param name="currentVersion">The running version.</param>
    /// <returns><see langword="true"/>.</returns>
    ValueTask<bool> IUpdatePresenter.ConfirmInstallAsync(AvailableUpdate update, SemanticVersion currentVersion)
    {
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    void IUpdatePresenter.InstallerStarted(AvailableUpdate update)
    {
        _installerStarted = true;
        _logger.LogInformation("The installer of version {Version} was started", update.Version);
        InstallerStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Chooses the page to open for a release.
    /// </summary>
    /// <param name="address">The address GitHub reported.</param>
    /// <returns>That address if it is a secure web address, else the list of releases.</returns>
    private static Uri ReleasePageOf(string? address)
    {
        // The value comes from the network and is opened by the operating system, so only a web address is accepted.
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : new Uri($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases");
    }

    /// <summary>
    /// Passes a change of the controller on.
    /// </summary>
    private void OnControllerChanged()
    {
        if (_controller?.LastFailure is { Stage: UpdateFailureStage.Check } failure)
        {
            _logger.LogInformation(failure.Error, "Checking for updates failed");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
