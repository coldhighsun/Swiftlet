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
    /// Verifies the All Mail download is on by default and the choice survives a restart without disturbing the other values.
    /// </summary>
    [Fact]
    public void SetDownloadAllMail_Off_DefaultsOnAndPersistsWithOtherSettings()
    {
        var store = new AppSettings(StorePath);
        store.DownloadAllMail.Should().BeTrue();
        store.SetDownloadFullHistory(false);

        store.SetDownloadAllMail(false);

        var reloaded = new AppSettings(StorePath);
        reloaded.DownloadAllMail.Should().BeFalse();
        reloaded.DownloadFullHistory.Should().BeFalse();
        reloaded.RemoveServerDeletedMessages.Should().BeTrue();
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
