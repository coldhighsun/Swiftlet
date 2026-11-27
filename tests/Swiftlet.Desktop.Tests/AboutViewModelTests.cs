using AwesomeAssertions;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.ViewModels.Settings;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the "About" page of the settings window.
/// </summary>
public class AboutViewModelTests
{
    /// <summary>
    /// The folders used by the tests.
    /// </summary>
    private static readonly AppPaths Paths = new(Path.Combine(Path.GetTempPath(), "swiftlet-about"));

    /// <summary>
    /// Verifies the page shows the version of the application.
    /// </summary>
    [Fact]
    public void VersionText_English_ContainsVersion()
    {
        var viewModel = new AboutViewModel(new FakeDialogService(), TestLocalizer.English(), Paths);

        viewModel.Version.Should().Be(Swiftlet.Application.AppVersion.Display).And.NotBeEmpty();
        viewModel.VersionText.Should().Be($"Version {viewModel.Version}");
    }

    /// <summary>
    /// Verifies the version line follows a language switch.
    /// </summary>
    [Fact]
    public void RefreshLanguage_LanguageSwitched_RaisesVersionText()
    {
        var localizer = TestLocalizer.English();
        var viewModel = new AboutViewModel(new FakeDialogService(), localizer, Paths);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        localizer.SetPreference("zh-Hans");

        viewModel.RefreshLanguage();

        changed.Should().Equal(nameof(AboutViewModel.VersionText));
        viewModel.VersionText.Should().Be($"版本 {viewModel.Version}");
    }

    /// <summary>
    /// Verifies every link points into the project.
    /// </summary>
    [Fact]
    public void Links_Always_PointToTheProject()
    {
        var viewModel = new AboutViewModel(new FakeDialogService(), TestLocalizer.English(), Paths);

        Uri[] links =
        [
            viewModel.HomePage, viewModel.IssuesPage, viewModel.LicensePage, viewModel.ThirdPartyNoticesPage,
            viewModel.PrivacyPage, viewModel.TermsPage,
        ];

        links.Select(l => l.AbsoluteUri).Should().Equal(
            "https://github.com/coldhighsun/Swiftlet",
            "https://github.com/coldhighsun/Swiftlet/issues",
            "https://github.com/coldhighsun/Swiftlet/blob/main/LICENSE",
            "https://github.com/coldhighsun/Swiftlet/blob/main/THIRD-PARTY-NOTICES.md",
            "https://github.com/coldhighsun/Swiftlet/blob/main/PRIVACY.md",
            "https://github.com/coldhighsun/Swiftlet/blob/main/TERMS.md");
    }

    /// <summary>
    /// Verifies each button opens its own folder.
    /// </summary>
    [Fact]
    public async Task OpenFolderCommands_Executed_OpenTheirFolders()
    {
        var dialogs = new FakeDialogService();
        var viewModel = new AboutViewModel(dialogs, TestLocalizer.English(), Paths);

        await viewModel.OpenLogFolderCommand.ExecuteAsync(null);
        await viewModel.OpenDataFolderCommand.ExecuteAsync(null);

        dialogs.OpenedFolders.Should().Equal(Path.Combine(Paths.DataDirectory, "logs"), Paths.DataDirectory);
        viewModel.ErrorText.Should().BeNull();
    }

    /// <summary>
    /// Verifies a folder that cannot be opened is reported on the page.
    /// </summary>
    /// <param name="throws">Whether opening throws instead of returning <see langword="false"/>.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenLogFolderCommand_Fails_ShowsError(bool throws)
    {
        var dialogs = new FakeDialogService
        {
            OpenFolderResult = false,
            OpenFolderError = throws ? new InvalidOperationException("no file manager") : null,
        };
        var viewModel = new AboutViewModel(dialogs, TestLocalizer.English(), Paths);

        await viewModel.OpenLogFolderCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().Be($"Could not open the folder: {Paths.LogDirectory}");
    }

    /// <summary>
    /// Verifies a successful attempt clears the error of an earlier one.
    /// </summary>
    [Fact]
    public async Task OpenDataFolderCommand_SucceedsAfterFailure_ClearsError()
    {
        var dialogs = new FakeDialogService { OpenFolderResult = false };
        var viewModel = new AboutViewModel(dialogs, TestLocalizer.English(), Paths);
        await viewModel.OpenDataFolderCommand.ExecuteAsync(null);
        dialogs.OpenFolderResult = true;

        await viewModel.OpenDataFolderCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().BeNull();
    }

    /// <summary>
    /// Verifies the page offers no updates without a service, or in a development build.
    /// </summary>
    [Fact]
    public void HasUpdates_NoServiceOrUnsupported_IsFalse()
    {
        var none = new AboutViewModel(new FakeDialogService(), TestLocalizer.English(), Paths);
        var development = new AboutViewModel(
            new FakeDialogService(), TestLocalizer.English(), Paths,
            updates: new FakeUpdateService { IsSupported = false }, settings: new FakeAppSettings());

        none.HasUpdates.Should().BeFalse();
        development.HasUpdates.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the status line follows the update service while attached.
    /// </summary>
    [Fact]
    public void Attach_ServiceChanges_UpdatesStatus()
    {
        var updates = new FakeUpdateService();
        var viewModel = Updating(updates, new FakeAppSettings());
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        viewModel.Attach();

        updates.Status = Services.UpdateStatus.Available;
        updates.Available = FakeUpdateService.Newer;
        updates.RaiseChanged();

        viewModel.UpdateStatusText.Should().Be("Version 2.0.0 is available.");
        viewModel.HasAvailableUpdate.Should().BeTrue();
        changed.Should().Contain(nameof(AboutViewModel.UpdateStatusText));

        changed.Clear();
        viewModel.Detach();
        updates.RaiseChanged();

        changed.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a failed check is explained.
    /// </summary>
    [Fact]
    public void UpdateStatusText_CheckFailed_ShowsReason()
    {
        var updates = new FakeUpdateService { CheckError = new HttpRequestException("offline") };

        var viewModel = Updating(updates, new FakeAppSettings());

        viewModel.UpdateStatusText.Should().Be("Could not check for updates: offline");
    }

    /// <summary>
    /// Verifies the buttons check for and open updates, and no check starts while one runs.
    /// </summary>
    [Fact]
    public async Task UpdateCommands_Executed_CheckAndShow()
    {
        var updates = new FakeUpdateService();
        var dialogs = new FakeDialogService();
        var viewModel = new AboutViewModel(
            dialogs, TestLocalizer.English(), Paths, updates: updates, settings: new FakeAppSettings());

        await viewModel.CheckNowCommand.ExecuteAsync(null);
        await viewModel.ShowUpdateCommand.ExecuteAsync(null);
        updates.Status = Services.UpdateStatus.Checking;

        updates.Checks.Should().Be(1);
        dialogs.UpdateCalls.Should().Be(1);
        viewModel.CheckNowCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Verifies the checkbox saves at once and falls back when saving fails.
    /// </summary>
    [Fact]
    public void CheckForUpdates_Changed_SavesOrRestores()
    {
        var settings = new FakeAppSettings();
        var viewModel = Updating(new FakeUpdateService(), settings);

        viewModel.CheckForUpdates = false;

        settings.CheckForUpdates.Should().BeFalse();

        settings.Failure = new IOException("read only");
        viewModel.CheckForUpdates = true;

        viewModel.CheckForUpdates.Should().BeFalse();
        viewModel.ErrorText.Should().Contain("read only");
    }

    /// <summary>
    /// Creates the page with updates.
    /// </summary>
    /// <param name="updates">The update service.</param>
    /// <param name="settings">The settings.</param>
    /// <returns>The view model.</returns>
    private static AboutViewModel Updating(FakeUpdateService updates, FakeAppSettings settings)
    {
        return new AboutViewModel(
            new FakeDialogService(), TestLocalizer.English(), Paths, updates: updates, settings: settings);
    }
}
