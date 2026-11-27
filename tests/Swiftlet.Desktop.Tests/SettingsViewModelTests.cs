using AwesomeAssertions;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels.Settings;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="SettingsViewModel"/>.
/// </summary>
public class SettingsViewModelTests
{
    /// <summary>
    /// Verifies toggling persists the value and the sync settings see it.
    /// </summary>
    [Fact]
    public void Toggle_PersistsAndUpdatesSyncSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}", "settings.json");
        try
        {
            var store = new AppSettings(path);
            var viewModel = new SettingsViewModel(store, TestLocalizer.English(), new FakeRemoteImageRules());
            viewModel.RemoveServerDeletedMessages.Should().BeTrue();

            viewModel.RemoveServerDeletedMessages = false;

            store.RemoveServerDeletedMessages.Should().BeFalse();
            new AppSettings(path).RemoveServerDeletedMessages.Should().BeFalse();

            viewModel.RemoveServerDeletedMessages = true;
            store.RemoveServerDeletedMessages.Should().BeTrue();
            new AppSettings(path).RemoveServerDeletedMessages.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>
    /// Verifies the view model starts from the stored value.
    /// </summary>
    [Fact]
    public void Constructor_StoredSettingOn_StartsChecked()
    {
        var settings = new FakeAppSettings { RemoveServerDeletedMessages = true };

        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.RemoveServerDeletedMessages.Should().BeTrue();
        settings.SetCalls.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a failed save is reported and the checkbox returns to the stored value.
    /// </summary>
    [Fact]
    public void Toggle_SaveFails_ShowsErrorAndRevertsValue()
    {
        var settings = new FakeAppSettings { Failure = new IOException("disk full") };
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.RemoveServerDeletedMessages = true;

        viewModel.RemoveServerDeletedMessages.Should().BeFalse();
        viewModel.ErrorText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies the saved rules are listed when the window opens.
    /// </summary>
    [Fact]
    public async Task Constructor_SavedRules_AreListedWithCount()
    {
        var rules = new FakeRemoteImageRules();
        await rules.AddAsync("shop.com", TestContext.Current.CancellationToken);
        await rules.AddAsync("news@other.org", TestContext.Current.CancellationToken);

        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules);

        viewModel.RemoteImageRules.Should().Equal("news@other.org", "shop.com");
        viewModel.RemoteImageRuleCountText.Should().Be("Showing 2 of 2");
    }

    /// <summary>
    /// Verifies a typed rule is normalized, saved, listed and the input is cleared.
    /// </summary>
    [Fact]
    public async Task AddRemoteImageRuleCommand_ValidRule_SavesAndListsIt()
    {
        var rules = new FakeRemoteImageRules();
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules)
        {
            NewRemoteImageRule = "  News@Shop.COM ",
        };

        await viewModel.AddRemoteImageRuleCommand.ExecuteAsync(null);

        rules.AddCalls.Should().Equal("news@shop.com");
        viewModel.RemoteImageRules.Should().Equal("news@shop.com");
        viewModel.NewRemoteImageRule.Should().BeEmpty();
        viewModel.ErrorText.Should().BeNull();
    }

    /// <summary>
    /// Verifies an invalid rule is not saved and the problem is shown.
    /// </summary>
    [Fact]
    public async Task AddRemoteImageRuleCommand_InvalidRule_ShowsErrorAndSavesNothing()
    {
        var rules = new FakeRemoteImageRules();
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules)
        {
            NewRemoteImageRule = "not a domain",
        };

        await viewModel.AddRemoteImageRuleCommand.ExecuteAsync(null);

        rules.AddCalls.Should().BeEmpty();
        viewModel.ErrorText.Should().Be("Enter a valid sender address or domain, for example news@shop.com or shop.com.");
        viewModel.NewRemoteImageRule.Should().Be("not a domain");
    }

    /// <summary>
    /// Verifies adding an existing rule again does not duplicate it.
    /// </summary>
    [Fact]
    public async Task AddRemoteImageRuleCommand_DuplicateRule_ListsItOnce()
    {
        var rules = new FakeRemoteImageRules();
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules);
        viewModel.NewRemoteImageRule = "shop.com";
        await viewModel.AddRemoteImageRuleCommand.ExecuteAsync(null);

        viewModel.NewRemoteImageRule = "Shop.com";
        await viewModel.AddRemoteImageRuleCommand.ExecuteAsync(null);

        viewModel.RemoteImageRules.Should().Equal("shop.com");
        viewModel.ErrorText.Should().BeNull();
    }

    /// <summary>
    /// Verifies a failed save is shown in the window.
    /// </summary>
    [Fact]
    public async Task AddRemoteImageRuleCommand_SaveFails_ShowsError()
    {
        var rules = new FakeRemoteImageRules { AddFailure = new IOException("disk full") };
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules)
        {
            NewRemoteImageRule = "shop.com",
        };

        await viewModel.AddRemoteImageRuleCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().Contain("Failed to save settings").And.Contain("disk full");
        viewModel.RemoteImageRules.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the selected rule is deleted and disappears from the list.
    /// </summary>
    [Fact]
    public async Task RemoveRemoteImageRuleCommand_SelectedRule_DeletesIt()
    {
        var rules = new FakeRemoteImageRules();
        await rules.AddAsync("shop.com", TestContext.Current.CancellationToken);
        await rules.AddAsync("other.org", TestContext.Current.CancellationToken);
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules);
        viewModel.SelectedRemoteImageRule = "shop.com";

        await viewModel.RemoveRemoteImageRuleCommand.ExecuteAsync(null);

        rules.RemoveCalls.Should().Equal("shop.com");
        viewModel.RemoteImageRules.Should().Equal("other.org");
        viewModel.RemoteImageRuleCountText.Should().Be("Showing 1 of 1");
    }

    /// <summary>
    /// Verifies removing with nothing selected does nothing.
    /// </summary>
    [Fact]
    public async Task RemoveRemoteImageRuleCommand_NothingSelected_DoesNothing()
    {
        var rules = new FakeRemoteImageRules();
        await rules.AddAsync("shop.com", TestContext.Current.CancellationToken);
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules);

        await viewModel.RemoveRemoteImageRuleCommand.ExecuteAsync(null);

        rules.RemoveCalls.Should().BeEmpty();
        viewModel.RemoteImageRules.Should().Equal("shop.com");
    }

    /// <summary>
    /// Verifies the filter box narrows the list and the count text.
    /// </summary>
    [Fact]
    public async Task RemoteImageFilter_Text_NarrowsListAndCount()
    {
        var rules = new FakeRemoteImageRules();
        foreach (var rule in new[] { "shop.com", "news@shop.net", "other.org" })
        {
            await rules.AddAsync(rule, TestContext.Current.CancellationToken);
        }

        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules);

        viewModel.RemoteImageFilter = "SHOP";

        viewModel.RemoteImageRules.Should().Equal("news@shop.net", "shop.com");
        viewModel.RemoteImageRuleCountText.Should().Be("Showing 2 of 2");

        viewModel.RemoteImageFilter = string.Empty;

        viewModel.RemoteImageRules.Should().HaveCount(3);
    }

    /// <summary>
    /// Verifies at most 200 rules are listed while the count text shows the real total.
    /// </summary>
    [Fact]
    public async Task Constructor_MoreThanTwoHundredRules_ListsFirstTwoHundredWithTotal()
    {
        var rules = new FakeRemoteImageRules();
        for (var i = 0; i < 250; i++)
        {
            await rules.AddAsync($"site{i:D3}.example.com", TestContext.Current.CancellationToken);
        }

        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), rules);

        viewModel.RemoteImageRules.Should().HaveCount(200);
        viewModel.RemoteImageRules[0].Should().Be("site000.example.com");
        viewModel.RemoteImageRuleCountText.Should().Be("Showing 200 of 250");
    }

    /// <summary>
    /// Verifies the close command raises the close request.
    /// </summary>
    [Fact]
    public void Close_Executed_RaisesCloseRequested()
    {
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), new FakeRemoteImageRules());
        var raised = 0;
        viewModel.CloseRequested += (_, _) => raised++;

        viewModel.CloseCommand.Execute(null);

        raised.Should().Be(1);
    }

    /// <summary>
    /// Verifies toggling the history download saves the choice and starts from the stored value.
    /// </summary>
    [Fact]
    public void DownloadFullHistory_Toggle_SavesValue()
    {
        var settings = new FakeAppSettings();
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());
        viewModel.DownloadFullHistory.Should().BeTrue();

        viewModel.DownloadFullHistory = false;

        settings.HistoryCalls.Should().Equal(false);
        settings.DownloadFullHistory.Should().BeFalse();
    }

    /// <summary>
    /// Verifies choosing a tray style saves it, and the names follow the language.
    /// </summary>
    [Fact]
    public void SelectedTrayUnreadStyle_Changed_SavesAndFollowsLanguage()
    {
        var settings = new FakeAppSettings();
        var localizer = TestLocalizer.English();
        var viewModel = new SettingsViewModel(settings, localizer, new FakeRemoteImageRules());
        viewModel.SelectedTrayUnreadStyle.Code.Should().Be("dot");

        viewModel.SelectedTrayUnreadStyle = viewModel.TrayUnreadStyleOptions.Single(o => o.Code == "blink");

        settings.TrayUnreadStyle.Should().Be("blink");
        viewModel.SelectedTrayUnreadStyle.DisplayName.Should().Be("Blinking");
        localizer.SetPreference("zh-Hans");
        viewModel.SelectedTrayUnreadStyle.DisplayName.Should().Be("图标闪烁");
    }

    /// <summary>
    /// Verifies a failed save of the tray style is reported and the picker returns to the stored value.
    /// </summary>
    [Fact]
    public void SelectedTrayUnreadStyle_SaveFails_ShowsErrorAndRevertsValue()
    {
        var settings = new FakeAppSettings { Failure = new IOException("disk full") };
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.SelectedTrayUnreadStyle = viewModel.TrayUnreadStyleOptions.Single(o => o.Code == "blink");

        viewModel.SelectedTrayUnreadStyle.Code.Should().Be("dot");
        viewModel.ErrorText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies toggling the new mail notification saves the choice.
    /// </summary>
    [Fact]
    public void ShowNewMailNotifications_Toggle_SavesValue()
    {
        var settings = new FakeAppSettings();
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());
        viewModel.ShowNewMailNotifications.Should().BeTrue();

        viewModel.ShowNewMailNotifications = false;

        settings.ShowNewMailNotifications.Should().BeFalse();
    }

    /// <summary>
    /// Verifies toggling the hidden start saves the choice.
    /// </summary>
    [Fact]
    public void StartMinimized_Toggle_SavesValue()
    {
        var settings = new FakeAppSettings();
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());
        viewModel.StartMinimized.Should().BeFalse();

        viewModel.StartMinimized = true;

        settings.StartMinimized.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a failed save of the hidden start is reported and the checkbox returns to the stored value.
    /// </summary>
    [Fact]
    public void StartMinimized_SaveFails_ShowsErrorAndRevertsValue()
    {
        var settings = new FakeAppSettings { Failure = new IOException("disk full") };
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.StartMinimized = true;

        viewModel.StartMinimized.Should().BeFalse();
        viewModel.ErrorText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies a failed save of the notification choice is reported and the checkbox returns to the stored value.
    /// </summary>
    [Fact]
    public void ShowNewMailNotifications_SaveFails_ShowsErrorAndRevertsValue()
    {
        var settings = new FakeAppSettings { Failure = new IOException("disk full") };
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.ShowNewMailNotifications = false;

        viewModel.ShowNewMailNotifications.Should().BeTrue();
        viewModel.ErrorText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies a failed save is reported and the checkbox returns to the stored value.
    /// </summary>
    [Fact]
    public void DownloadFullHistory_SaveFails_ShowsErrorAndRevertsValue()
    {
        var settings = new FakeAppSettings { Failure = new IOException("disk full") };
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.DownloadFullHistory = false;

        viewModel.DownloadFullHistory.Should().BeTrue();
        viewModel.ErrorText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies the window says how well the local data is protected.
    /// </summary>
    /// <param name="status">The protection status.</param>
    /// <param name="expected">The expected text.</param>
    [Theory]
    [InlineData(DatabaseProtectionStatus.Encrypted, "Local mail data is encrypted.")]
    [InlineData(DatabaseProtectionStatus.NotEncryptedNoKeychain, "Local mail data is not encrypted: no system keychain is available.")]
    [InlineData(DatabaseProtectionStatus.PartlyEncrypted, "Part of the local mail data is not encrypted yet; the next start tries again.")]
    public void Constructor_ProtectionStatus_ShowsStatusText(DatabaseProtectionStatus status, string expected)
    {
        var protection = new FakeDatabaseProtection { Status = status };

        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), new FakeRemoteImageRules(), protection);

        viewModel.DatabaseStatusText.Should().Be(expected);
    }

    /// <summary>
    /// Verifies nothing is claimed when the protection is unknown.
    /// </summary>
    [Fact]
    public void Constructor_NoProtection_HasNoStatusText()
    {
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.DatabaseStatusText.Should().BeNull();
    }

    /// <summary>
    /// Verifies toggling the tray setting saves the choice and starts from the stored value.
    /// </summary>
    [Fact]
    public void CloseToTray_Toggle_SavesValue()
    {
        var settings = new FakeAppSettings();
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());
        viewModel.CloseToTray.Should().BeTrue();

        viewModel.CloseToTray = false;

        settings.CloseToTray.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failed save of the tray setting is reported and the checkbox returns to the stored value.
    /// </summary>
    [Fact]
    public void CloseToTray_SaveFails_ShowsErrorAndRevertsValue()
    {
        var settings = new FakeAppSettings { Failure = new IOException("disk full") };
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.CloseToTray = false;

        viewModel.CloseToTray.Should().BeTrue();
        viewModel.ErrorText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies a settings file without a theme follows the system.
    /// </summary>
    [Fact]
    public void AppSettings_ThemeMissing_IsSystem()
    {
        WithSettingsFile(null, path => new AppSettings(path).Theme.Should().Be(ThemePreference.System));
    }

    /// <summary>
    /// Verifies a saved theme survives a restart.
    /// </summary>
    [Fact]
    public void AppSettings_ThemeSaved_SurvivesReload()
    {
        WithSettingsFile(null, path =>
        {
            new AppSettings(path).SetTheme(ThemePreference.Dark);

            new AppSettings(path).Theme.Should().Be(ThemePreference.Dark);
        });
    }

    /// <summary>
    /// Verifies an unknown or wrongly typed theme value follows the system and keeps the other settings.
    /// </summary>
    /// <param name="json">The settings file content.</param>
    [Theory]
    [InlineData("{\"language\":\"en\",\"theme\":\"neon\"}")]
    [InlineData("{\"language\":\"en\",\"theme\":5}")]
    [InlineData("{\"language\":\"en\",\"theme\":{}}")]
    public void AppSettings_ThemeUnusable_IsSystemAndKeepsOtherSettings(string json)
    {
        WithSettingsFile(json, path =>
        {
            var settings = new AppSettings(path);

            settings.Theme.Should().Be(ThemePreference.System);
            settings.Load().Should().Be("en");
        });
    }

    /// <summary>
    /// Verifies saving another setting does not lose the theme.
    /// </summary>
    [Fact]
    public void AppSettings_SaveOtherSettings_KeepsTheme()
    {
        WithSettingsFile(null, path =>
        {
            var settings = new AppSettings(path);
            settings.SetTheme(ThemePreference.Dark);

            settings.Save("en");
            settings.SetDownloadFullHistory(false);
            settings.SetRemoveServerDeletedMessages(false);

            new AppSettings(path).Theme.Should().Be(ThemePreference.Dark);
        });
    }

    /// <summary>
    /// Verifies the view model starts from the stored theme and a new choice is saved and applied.
    /// </summary>
    [Fact]
    public void SelectedTheme_Changed_SavesAndApplies()
    {
        var settings = new FakeAppSettings { Theme = ThemePreference.Light };
        var applier = new FakeThemeApplier();
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules(), themeApplier: applier);
        viewModel.SelectedTheme.Code.Should().Be(ThemePreference.Light);
        applier.Applied.Should().BeEmpty();

        viewModel.SelectedTheme = viewModel.ThemeOptions.Single(o => o.Code == ThemePreference.Dark);

        settings.Theme.Should().Be(ThemePreference.Dark);
        applier.Applied.Should().Equal(ThemePreference.Dark);
    }

    /// <summary>
    /// Verifies a failed theme save is reported, the choice returns to the stored one and nothing is applied.
    /// </summary>
    [Fact]
    public void SelectedTheme_SaveFails_RevertsAndShowsError()
    {
        var settings = new FakeAppSettings { Failure = new IOException("disk full") };
        var applier = new FakeThemeApplier();
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules(), themeApplier: applier);

        viewModel.SelectedTheme = viewModel.ThemeOptions.Single(o => o.Code == ThemePreference.Dark);

        viewModel.SelectedTheme.Code.Should().Be(ThemePreference.System);
        viewModel.ErrorText.Should().Contain("disk full");
        applier.Applied.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the texts built by the view model follow a language switch made in the window itself.
    /// </summary>
    [Fact]
    public void LanguageSwitched_TextsOfTheWindow_AreRelabelled()
    {
        var localizer = TestLocalizer.English();
        var protection = new FakeDatabaseProtection { Status = DatabaseProtectionStatus.Encrypted };
        var viewModel = new SettingsViewModel(new FakeAppSettings(), localizer, new FakeRemoteImageRules(), protection);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        localizer.SetPreference("zh-Hans");

        viewModel.ThemeOptions.Select(o => o.DisplayName).Should().Equal("跟随系统", "浅色", "深色");
        viewModel.DatabaseStatusText.Should().Be("本地邮件数据已加密。");
        viewModel.RemoteImageRuleCountText.Should().Be("显示 0 / 共 0 条");
        changed.Should().Contain(nameof(SettingsViewModel.DatabaseStatusText));
    }

    /// <summary>
    /// Verifies a language switch also reaches the "About" page of the window.
    /// </summary>
    [Fact]
    public void LanguageSwitched_AboutPage_IsRelabelled()
    {
        var localizer = TestLocalizer.English();
        var about = new AboutViewModel(new FakeDialogService(), localizer, new Swiftlet.Desktop.Hosting.AppPaths("data"));
        var viewModel = new SettingsViewModel(new FakeAppSettings(), localizer, new FakeRemoteImageRules(), about: about);
        var changed = new List<string?>();
        about.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        localizer.SetPreference("zh-Hans");

        viewModel.About.Should().BeSameAs(about);
        changed.Should().Contain(nameof(AboutViewModel.VersionText));
    }

    /// <summary>
    /// Verifies a closed window's view model no longer listens to the long-lived localizer.
    /// </summary>
    [Fact]
    public void Detach_LanguageSwitchedAfterwards_ChangesNothing()
    {
        var localizer = TestLocalizer.English();
        var viewModel = new SettingsViewModel(new FakeAppSettings(), localizer, new FakeRemoteImageRules());

        viewModel.Detach();
        localizer.SetPreference("zh-Hans");

        viewModel.RemoteImageRuleCountText.Should().Be("Showing 0 of 0");
    }

    /// <summary>
    /// Verifies the applier changes the theme of the running application.
    /// </summary>
    [Fact]
    public async Task ThemeApplier_EachPreference_SetsRequestedVariant()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                var applier = new Swiftlet.Desktop.Services.AvaloniaThemeApplier();
                var application = Avalonia.Application.Current!;

                applier.Apply(ThemePreference.Dark);
                application.RequestedThemeVariant.Should().Be(Avalonia.Styling.ThemeVariant.Dark);
                applier.Apply(ThemePreference.Light);
                application.RequestedThemeVariant.Should().Be(Avalonia.Styling.ThemeVariant.Light);
                applier.Apply(ThemePreference.System);
                application.RequestedThemeVariant.Should().Be(Avalonia.Styling.ThemeVariant.Default);
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs a test against a settings file in its own temporary directory.
    /// </summary>
    /// <param name="content">The initial file content, or <see langword="null"/> for no file.</param>
    /// <param name="test">The test body, given the file path.</param>
    private static void WithSettingsFile(string? content, Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            if (content is not null)
            {
                File.WriteAllText(path, content);
            }

            test(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
