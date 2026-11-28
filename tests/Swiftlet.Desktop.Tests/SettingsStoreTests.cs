using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Application;
using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="AppSettings"/>.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    /// <summary>
    /// The temporary directory holding the settings file.
    /// </summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");

    /// <summary>
    /// Gets the settings file path, in a directory that does not exist yet.
    /// </summary>
    private string StorePath => Path.Combine(_directory, "nested", "settings.json");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Verifies a missing file yields the defaults.
    /// </summary>
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new AppSettings(StorePath);

        store.Load().Should().Be("system");
        store.RemoveServerDeletedMessages.Should().BeTrue();
    }

    /// <summary>
    /// Verifies update checks are on at first and the choice and their memory are kept.
    /// </summary>
    [Fact]
    public void UpdateSettings_Changed_ArePersisted()
    {
        var store = new AppSettings(StorePath);
        store.CheckForUpdates.Should().BeTrue();
        store.UpdateCheckState.Should().Be(new UpdateCheckState());
        var state = new UpdateCheckState(new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero), "2.0.0", true);

        store.SetCheckForUpdates(false);
        store.ChangeUpdateCheckState(_ => state);
        store.Save("zh-Hans");

        var reloaded = new AppSettings(StorePath);
        reloaded.CheckForUpdates.Should().BeFalse();
        reloaded.UpdateCheckState.Should().Be(state);
    }

    /// <summary>
    /// Verifies a damaged update memory is dropped without losing the other settings.
    /// </summary>
    [Fact]
    public void UpdateCheckState_DamagedValue_KeepsOtherSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, """{ "Language": "en", "UpdateCheck": { "lastCheckedAt": "soon" } }""");

        var store = new AppSettings(StorePath);

        store.Load().Should().Be("en");
        store.UpdateCheckState.Should().Be(new UpdateCheckState());
    }

    /// <summary>
    /// Verifies a corrupt file yields the defaults without throwing.
    /// </summary>
    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "{ not json");

        var store = new AppSettings(StorePath);

        store.Load().Should().Be("system");
        store.RemoveServerDeletedMessages.Should().BeTrue();
    }

    /// <summary>
    /// Verifies saving the setting keeps the saved language.
    /// </summary>
    [Fact]
    public void SaveSetting_KeepsLanguage()
    {
        var store = new AppSettings(StorePath);
        store.Save("zh-Hans");

        store.SetRemoveServerDeletedMessages(false);

        var reloaded = new AppSettings(StorePath);
        reloaded.Load().Should().Be("zh-Hans");
        reloaded.RemoveServerDeletedMessages.Should().BeFalse();
    }

    /// <summary>
    /// Verifies toggling the setting after the file became corrupt keeps the language saved earlier in the process.
    /// </summary>
    [Fact]
    public void SaveSetting_CorruptFileAfterSavingLanguage_KeepsLanguage()
    {
        var store = new AppSettings(StorePath);
        store.Save("zh-Hans");
        File.WriteAllText(StorePath, "{ not json");

        store.SetRemoveServerDeletedMessages(false);

        store.Load().Should().Be("zh-Hans");
        var reloaded = new AppSettings(StorePath);
        reloaded.Load().Should().Be("zh-Hans");
        reloaded.RemoveServerDeletedMessages.Should().BeFalse();
    }

    /// <summary>
    /// Verifies saving the language keeps the setting.
    /// </summary>
    [Fact]
    public void SaveLanguage_KeepsSetting()
    {
        var store = new AppSettings(StorePath);
        store.SetRemoveServerDeletedMessages(false);

        store.Save("en");

        var reloaded = new AppSettings(StorePath);
        reloaded.Load().Should().Be("en");
        reloaded.RemoveServerDeletedMessages.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a file written before the setting existed loads with the setting on.
    /// </summary>
    [Fact]
    public void OldFileWithOnlyLanguage_LoadsWithSettingOn()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, """{"language":"zh-Hans"}""");

        var store = new AppSettings(StorePath);

        store.Load().Should().Be("zh-Hans");
        store.RemoveServerDeletedMessages.Should().BeTrue();
    }

    /// <summary>
    /// Verifies new mail notifications are on unless the file says otherwise, and the choice survives a restart.
    /// </summary>
    [Fact]
    public void ShowNewMailNotifications_DefaultsOnAndIsSaved()
    {
        var store = new AppSettings(StorePath);
        store.ShowNewMailNotifications.Should().BeTrue();

        store.SetShowNewMailNotifications(false);
        store.Save("en");

        new AppSettings(StorePath).ShowNewMailNotifications.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a hidden start is off unless chosen, and the choice survives a restart without touching other settings.
    /// </summary>
    [Fact]
    public void StartMinimized_DefaultsOffAndIsSaved()
    {
        var store = new AppSettings(StorePath);
        store.StartMinimized.Should().BeFalse();

        store.SetStartMinimized(true);
        store.SetCloseToTray(false);

        var reloaded = new AppSettings(StorePath);
        reloaded.StartMinimized.Should().BeTrue();
        reloaded.CloseToTray.Should().BeFalse();
    }

    /// <summary>
    /// Verifies hardware rendering is on unless switched off, and the choice survives a restart and later saves.
    /// </summary>
    [Fact]
    public void UseHardwareRendering_DefaultsOnAndIsSaved()
    {
        var store = new AppSettings(StorePath);
        store.UseHardwareRendering.Should().BeTrue();

        store.SetUseHardwareRendering(false);
        store.SetCloseToTray(false);

        var reloaded = new AppSettings(StorePath);
        reloaded.UseHardwareRendering.Should().BeFalse();
        reloaded.CloseToTray.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the tray style defaults to the dot, is saved and announced.
    /// </summary>
    [Fact]
    public void TrayUnreadStyle_DefaultsToDotAndIsSavedAndAnnounced()
    {
        var store = new AppSettings(StorePath);
        store.TrayUnreadStyle.Should().Be("dot");
        var announced = 0;
        store.TrayUnreadStyleChanged += (_, _) => announced++;

        store.SetTrayUnreadStyle("blink");
        store.Save("en");

        announced.Should().Be(1);
        new AppSettings(StorePath).TrayUnreadStyle.Should().Be("blink");
    }

    /// <summary>
    /// Verifies an unusable tray style falls back to the dot without losing other settings.
    /// </summary>
    /// <param name="stored">The stored JSON value.</param>
    [Theory]
    [InlineData("\"sparkle\"")]
    [InlineData("5")]
    public void TrayUnreadStyle_UnusableValue_FallsBackAndKeepsOtherSettings(string stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, $$"""{"language":"zh-Hans","trayUnreadStyle":{{stored}}}""");

        var store = new AppSettings(StorePath);

        store.TrayUnreadStyle.Should().Be("dot");
        store.Load().Should().Be("zh-Hans");
    }

    /// <summary>
    /// Verifies the contact name order defaults to automatic, is saved and announced.
    /// </summary>
    [Fact]
    public void ContactNameOrder_DefaultsToAutoAndIsSavedAndAnnounced()
    {
        var store = new AppSettings(StorePath);
        store.ContactNameOrder.Should().Be("auto");
        var announced = 0;
        store.ContactNameOrderChanged += (_, _) => announced++;

        store.SetContactNameOrder("givenFirst");
        store.Save("en");

        announced.Should().Be(1);
        new AppSettings(StorePath).ContactNameOrder.Should().Be("givenFirst");
    }

    /// <summary>
    /// Verifies an unusable name order falls back to automatic without losing other settings.
    /// </summary>
    /// <param name="stored">The stored JSON value.</param>
    [Theory]
    [InlineData("\"backwards\"")]
    [InlineData("5")]
    public void ContactNameOrder_UnusableValue_FallsBackAndKeepsOtherSettings(string stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, $$"""{"language":"zh-Hans","contactNameOrder":{{stored}}}""");

        var store = new AppSettings(StorePath);

        store.ContactNameOrder.Should().Be("auto");
        store.Load().Should().Be("zh-Hans");
    }

    /// <summary>
    /// Verifies the window placement is empty at first, and survives a restart and later saves.
    /// </summary>
    [Fact]
    public void SetMainWindowPlacement_Saved_PersistsAcrossLaterSaves()
    {
        var store = new AppSettings(StorePath);
        store.MainWindowPlacement.Should().BeNull();
        var placement = new WindowPlacement(-1200, 40, 900.5, 600, true);

        store.SetMainWindowPlacement(placement);
        store.Save("en");
        new AppSettings(StorePath).SetDownloadFullHistory(false);

        new AppSettings(StorePath).MainWindowPlacement.Should().Be(placement);
    }

    /// <summary>
    /// Verifies an unusable window placement is ignored without losing other settings.
    /// </summary>
    /// <param name="stored">The stored JSON value.</param>
    [Theory]
    [InlineData("5")]
    [InlineData("{}")]
    [InlineData("""{"x":"left","y":0,"width":900,"height":600}""")]
    [InlineData("""{"x":0,"y":0,"width":-1,"height":600}""")]
    public void MainWindowPlacement_UnusableValue_IsIgnoredAndKeepsOtherSettings(string stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, $$"""{"language":"zh-Hans","mainWindowPlacement":{{stored}}}""");

        var store = new AppSettings(StorePath);

        store.MainWindowPlacement.Should().BeNull();
        store.Load().Should().Be("zh-Hans");
    }

    /// <summary>
    /// Verifies that, registered after AddApplication, the store is what the sync settings and language store resolve to.
    /// </summary>
    [Fact]
    public void AddAppSettings_AfterAddApplication_ResolvesStoreForAllRoles()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        var store = services.AddAppSettings(StorePath);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISyncSettings>().Should().BeSameAs(store);
        provider.GetRequiredService<ILanguagePreferenceStore>().Should().BeSameAs(store);
        provider.GetRequiredService<IAppSettings>().Should().BeSameAs(store);
        store.SetRemoveServerDeletedMessages(false);
        provider.GetRequiredService<ISyncSettings>().RemoveServerDeletedMessages.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the full-history download is on by default, including for files written before the setting existed.
    /// </summary>
    [Fact]
    public void DownloadFullHistory_NoValueStored_DefaultsToOn()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "{\"language\":\"en\",\"syncServerDeletions\":false}");

        var store = new AppSettings(StorePath);

        store.DownloadFullHistory.Should().BeTrue();
        store.RemoveServerDeletedMessages.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the choice survives a restart and does not disturb the other stored values.
    /// </summary>
    [Fact]
    public void SetDownloadFullHistory_Off_PersistsAndKeepsOtherSettings()
    {
        var store = new AppSettings(StorePath);
        store.Save("zh-Hans");
        store.SetRemoveServerDeletedMessages(false);

        store.SetDownloadFullHistory(false);

        var reloaded = new AppSettings(StorePath);
        reloaded.DownloadFullHistory.Should().BeFalse();
        reloaded.RemoveServerDeletedMessages.Should().BeFalse();
        reloaded.Load().Should().Be("zh-Hans");
    }

    /// <summary>
    /// Verifies changing another setting keeps the stored history choice.
    /// </summary>
    [Fact]
    public void Save_AfterHistoryTurnedOff_KeepsHistoryChoice()
    {
        var store = new AppSettings(StorePath);
        store.SetDownloadFullHistory(false);

        store.Save("en");
        store.SetRemoveServerDeletedMessages(false);

        new AppSettings(StorePath).DownloadFullHistory.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the removed "All Mail" key written by earlier versions is ignored without losing the other settings.
    /// </summary>
    [Fact]
    public void Load_RemovedDownloadAllMailKey_IsIgnoredAndKeepsOtherSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, """{"language":"zh-Hans","downloadAllMail":false,"downloadFullHistory":false}""");

        var store = new AppSettings(StorePath);

        store.Load().Should().Be("zh-Hans");
        store.DownloadFullHistory.Should().BeFalse();
    }

    /// <summary>
    /// Verifies staying in the tray is on by default and the choice survives a restart and later saves.
    /// </summary>
    [Fact]
    public void SetCloseToTray_Off_DefaultsOnAndPersists()
    {
        var store = new AppSettings(StorePath);
        store.CloseToTray.Should().BeTrue();

        store.SetCloseToTray(false);
        new AppSettings(StorePath).SetDownloadFullHistory(false);

        new AppSettings(StorePath).CloseToTray.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the "off" that earlier versions wrote while saving any other setting does not keep the new default off.
    /// </summary>
    [Fact]
    public void Load_LegacyRemoveServerDeletedFalse_IsIgnored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, """{"language":"en","removeServerDeletedMessages":false}""");

        var store = new AppSettings(StorePath);

        store.RemoveServerDeletedMessages.Should().BeTrue();
    }

    /// <summary>
    /// Verifies the fallback used without a settings store follows server deletions too.
    /// </summary>
    [Fact]
    public void AddApplication_WithoutSettingsStore_FollowsServerDeletions()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISyncSettings>().RemoveServerDeletedMessages.Should().BeTrue();
    }
}
