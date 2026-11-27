using AwesomeAssertions;
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.Versioning;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the update service and the store behind it. Nothing reaches the network and no installer runs.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    /// <summary>
    /// The download directory of a test.
    /// </summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "swiftlet-update-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The releases.
    /// </summary>
    private readonly FakeReleaseClient _client = new();

    /// <summary>
    /// The installer starts.
    /// </summary>
    private readonly FakeInstallerLauncher _launcher = new();

    /// <summary>
    /// The settings.
    /// </summary>
    private readonly FakeAppSettings _settings = new();

    /// <summary>
    /// Whether unsaved work is open.
    /// </summary>
    private bool _unsavedWork;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies only builds made from a release tag look for updates.
    /// </summary>
    /// <param name="version">The running version.</param>
    /// <param name="expected">Whether updates are looked for.</param>
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("0.0.0-alpha.0", false)]
    [InlineData("0.0.0-alpha.0.42", false)]
    [InlineData("1.2.4-alpha.0.3", false)]
    [InlineData("1.2.3-alpha.1", true)]
    [InlineData("", false)]
    public void TryGetReleaseVersion_Version_TellsWhetherToCheck(string version, bool expected)
    {
        GitHubUpdateService.TryGetReleaseVersion(version, out var parsed).Should().Be(expected);
        (parsed is not null).Should().Be(expected);
    }

    /// <summary>
    /// Verifies a development build looks for nothing and does nothing.
    /// </summary>
    [Fact]
    public async Task CheckAsync_DevelopmentBuild_DoesNothing()
    {
        using var service = new GitHubUpdateService(
            null, _settings, true, _directory, () => false, TestLocalizer.English());

        await service.CheckAsync(TestContext.Current.CancellationToken);
        await service.RunAsync(TestContext.Current.CancellationToken);
        await service.SkipAsync();

        service.IsSupported.Should().BeFalse();
        service.Status.Should().Be(UpdateStatus.Idle);
        (await service.InstallAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a newer release is offered to an installed copy.
    /// </summary>
    [Fact]
    public async Task CheckAsync_NewerRelease_OffersInstallation()
    {
        using var service = Create();

        await service.CheckAsync(TestContext.Current.CancellationToken);

        service.Status.Should().Be(UpdateStatus.Available);
        service.CanInstall.Should().BeTrue();
        service.Available.Should().Be(new AvailableVersion("2.0.0", "- Faster sync", new Uri(_client.Page)));
        _settings.UpdateCheckState.Pending.Should().BeTrue();
        _settings.UpdateCheckState.LastCheckedAt.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies a page address that is not a secure web address is not passed on.
    /// </summary>
    [Fact]
    public async Task CheckAsync_UnsafePageAddress_UsesReleaseList()
    {
        _client.Page = "file:///C:/Windows/notepad.exe";
        using var service = Create();

        await service.CheckAsync(TestContext.Current.CancellationToken);

        service.Available!.ReleasePage.AbsoluteUri.Should().Be("https://github.com/coldhighsun/Swiftlet/releases");
    }

    /// <summary>
    /// Verifies a copy that was not installed hears of the release but never downloads it.
    /// </summary>
    [Fact]
    public async Task InstallAsync_NotInstalledCopy_DownloadsNothing()
    {
        using var service = Create(isInstalled: false);
        await service.CheckAsync(TestContext.Current.CancellationToken);

        var started = await service.InstallAsync(TestContext.Current.CancellationToken);

        started.Should().BeFalse();
        service.Available.Should().NotBeNull();
        service.CanInstall.Should().BeFalse();
        _client.Downloads.Should().Be(0);
        _launcher.Launches.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies an equal or missing release means the application is up to date.
    /// </summary>
    /// <param name="tag">The latest release, if any.</param>
    [Theory]
    [InlineData("v1.0.0")]
    [InlineData(null)]
    public async Task CheckAsync_NothingNewer_ReportsUpToDate(string? tag)
    {
        _client.Tag = tag;
        using var service = Create();

        await service.CheckAsync(TestContext.Current.CancellationToken);

        service.Status.Should().Be(UpdateStatus.UpToDate);
        service.Available.Should().BeNull();
    }

    /// <summary>
    /// Verifies a failed check the user asked for is reported.
    /// </summary>
    [Fact]
    public async Task CheckAsync_RequestFails_ReportsError()
    {
        _client.Failure = new HttpRequestException("offline");
        using var service = Create();
        var changes = 0;
        service.Changed += (_, _) => changes++;

        await service.CheckAsync(TestContext.Current.CancellationToken);

        service.Status.Should().Be(UpdateStatus.Failed);
        service.CheckError!.Message.Should().Be("offline");
        service.InstallError.Should().BeNull();
        changes.Should().BePositive();
    }

    /// <summary>
    /// Verifies the verified installer is started silently and the application is told.
    /// </summary>
    [Fact]
    public async Task InstallAsync_VerifiedInstaller_StartsItSilently()
    {
        using var service = Create();
        var started = 0;
        service.InstallerStarted += (_, _) => started++;
        await service.CheckAsync(TestContext.Current.CancellationToken);

        var result = await service.InstallAsync(TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        started.Should().Be(1);
        var launch = _launcher.Launches.Should().ContainSingle().Which;
        launch.Path.Should().Be(Path.Combine(_directory, "Swiftlet-Setup-v2.0.0.exe"));
        launch.Arguments.Should().Contain("/SILENT");
    }

    /// <summary>
    /// Verifies an installer with the wrong checksum is never started.
    /// </summary>
    [Fact]
    public async Task InstallAsync_ChecksumMismatch_StartsNothing()
    {
        _client.WrongChecksum = true;
        using var service = Create();
        await service.CheckAsync(TestContext.Current.CancellationToken);

        var result = await service.InstallAsync(TestContext.Current.CancellationToken);

        result.Should().BeFalse();
        service.InstallError.Should().NotBeNull();
        _launcher.Launches.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies open work stops the installation, which succeeds once the work is closed without downloading again.
    /// </summary>
    [Fact]
    public async Task InstallAsync_UnsavedWork_StartsOnlyAfterItIsClosed()
    {
        _unsavedWork = true;
        using var service = Create();
        await service.CheckAsync(TestContext.Current.CancellationToken);

        var first = await service.InstallAsync(TestContext.Current.CancellationToken);

        first.Should().BeFalse();
        service.InstallError!.Message.Should().Be(TestLocalizer.English()["Update_CloseWindowsFirst"]);
        _launcher.Launches.Should().BeEmpty();

        _unsavedWork = false;
        var second = await service.InstallAsync(TestContext.Current.CancellationToken);

        second.Should().BeTrue();
        _client.Downloads.Should().Be(1);
    }

    /// <summary>
    /// Verifies a skipped version is remembered and offered again only when the user checks.
    /// </summary>
    [Fact]
    public async Task SkipAsync_AvailableVersion_RemembersIt()
    {
        using var service = Create();
        await service.CheckAsync(TestContext.Current.CancellationToken);

        await service.SkipAsync();

        service.Available.Should().BeNull();
        service.Status.Should().Be(UpdateStatus.UpToDate);
        _settings.UpdateCheckState.SkippedVersion.Should().Be("2.0.0");
        _settings.UpdateCheckState.Pending.Should().BeFalse();

        await service.CheckAsync(TestContext.Current.CancellationToken);

        service.Available.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies the automatic check announces a newer version.
    /// </summary>
    [Fact]
    public async Task RunAsync_NewerRelease_Announces()
    {
        using var service = Create();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Announced += (_, _) => announced.TrySetResult();

        var running = service.RunAsync(stop.Token);
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await running;

        service.Available!.Version.Should().Be("2.0.0");
    }

    /// <summary>
    /// Verifies no request is made while automatic checks are switched off.
    /// </summary>
    [Fact]
    public async Task RunAsync_ChecksSwitchedOff_AsksNothing()
    {
        _settings.CheckForUpdates = false;
        using var service = Create();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var running = service.RunAsync(stop.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await running;

        _client.Checks.Should().Be(0);
    }

    /// <summary>
    /// Verifies a waiting update hides the last check time, so the next start checks again.
    /// </summary>
    [Fact]
    public async Task GetLastCheckedAtAsync_UpdatePending_ReportsNone()
    {
        var store = new UpdateLastCheckStore(_settings);
        var checkedAt = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        await store.SetLastCheckedAtAsync(checkedAt, TestContext.Current.CancellationToken);

        (await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken)).Should().Be(checkedAt);
        await store.SetUpdatePendingAsync(true, TestContext.Current.CancellationToken);

        (await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    /// <summary>
    /// Verifies the skipped version is stored and can be forgotten.
    /// </summary>
    [Fact]
    public async Task SetSkippedVersionAsync_ThenCleared_RoundTrips()
    {
        var store = new UpdateLastCheckStore(_settings);
        var version = new SemanticVersion(2, 1, 0, "beta.1");

        await store.SetSkippedVersionAsync(version, TestContext.Current.CancellationToken);
        var stored = await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken);
        await store.ClearSkippedVersionAsync(TestContext.Current.CancellationToken);

        stored.Should().Be(version);
        (await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    /// <summary>
    /// Creates a service for version 1.0.0 over the fakes.
    /// </summary>
    /// <param name="isInstalled">Whether the copy was set up by the installer.</param>
    /// <returns>The service.</returns>
    private GitHubUpdateService Create(bool isInstalled = true)
    {
        var updater = new ReleaseUpdater(
            new UpdaterOptions
            {
                Owner = GitHubUpdateService.RepositoryOwner,
                Repo = GitHubUpdateService.RepositoryName,
                CurrentVersion = new SemanticVersion(1, 0, 0),
                AssetSelector = new PatternAssetSelector(GitHubUpdateService.InstallerAssetPattern),
                LastCheckStore = new UpdateLastCheckStore(_settings),
                InstallerLauncher = _launcher,
            },
            _client);
        return new GitHubUpdateService(
            updater,
            _settings,
            isInstalled,
            _directory,
            () => _unsavedWork,
            TestLocalizer.English(),
            initialDelay: TimeSpan.Zero);
    }
}
