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
}
