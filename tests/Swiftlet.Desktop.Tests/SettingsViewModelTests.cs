using AwesomeAssertions;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels;

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
    /// Verifies toggling the All Mail download saves the choice and starts from the stored value.
    /// </summary>
    [Fact]
    public void DownloadAllMail_Toggle_SavesValue()
    {
        var settings = new FakeAppSettings();
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());
        viewModel.DownloadAllMail.Should().BeTrue();

        viewModel.DownloadAllMail = false;

        settings.AllMailCalls.Should().Equal(false);
        settings.DownloadAllMail.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failed save is reported and the checkbox returns to the stored value.
    /// </summary>
    [Fact]
    public void DownloadAllMail_SaveFails_ShowsErrorAndRevertsValue()
    {
        var settings = new FakeAppSettings { Failure = new IOException("disk full") };
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English(), new FakeRemoteImageRules());

        viewModel.DownloadAllMail = false;

        viewModel.DownloadAllMail.Should().BeTrue();
        viewModel.ErrorText.Should().Contain("disk full");
    }
}
