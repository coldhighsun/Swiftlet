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
            var viewModel = new SettingsViewModel(store, TestLocalizer.English());
            viewModel.RemoveServerDeletedMessages.Should().BeFalse();

            viewModel.RemoveServerDeletedMessages = true;

            store.RemoveServerDeletedMessages.Should().BeTrue();
            new AppSettings(path).RemoveServerDeletedMessages.Should().BeTrue();

            viewModel.RemoveServerDeletedMessages = false;
            store.RemoveServerDeletedMessages.Should().BeFalse();
            new AppSettings(path).RemoveServerDeletedMessages.Should().BeFalse();
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

        var viewModel = new SettingsViewModel(settings, TestLocalizer.English());

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
        var viewModel = new SettingsViewModel(settings, TestLocalizer.English());

        viewModel.RemoveServerDeletedMessages = true;

        viewModel.RemoveServerDeletedMessages.Should().BeFalse();
        viewModel.ErrorText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies the close command raises the close request.
    /// </summary>
    [Fact]
    public void Close_Executed_RaisesCloseRequested()
    {
        var viewModel = new SettingsViewModel(new FakeAppSettings(), TestLocalizer.English());
        var raised = 0;
        viewModel.CloseRequested += (_, _) => raised++;

        viewModel.CloseCommand.Execute(null);

        raised.Should().Be(1);
    }
}
