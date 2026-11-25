using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Queries;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for language resolution, resource completeness and language switching in the view models.
/// </summary>
public partial class LocalizationTests
{
    /// <summary>
    /// The resource manager over the app's string resources.
    /// </summary>
    private static readonly ResourceManager Resources = new("Swiftlet.Desktop.Resources.Strings", typeof(LocalizationManager).Assembly);

    /// <summary>
    /// Verifies every culture maps to a supported language.
    /// </summary>
    /// <param name="culture">The culture name.</param>
    /// <param name="expected">The expected language code.</param>
    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hans")]
    [InlineData("fr-FR", "en")]
    [InlineData("ja-JP", "en")]
    public void Resolve_Culture_MapsToSupportedLanguage(string culture, string expected)
    {
        LocalizationManager.Resolve(CultureInfo.GetCultureInfo(culture)).Name.Should().Be(expected);
    }

    /// <summary>
    /// Verifies the system preference follows the operating system culture.
    /// </summary>
    /// <param name="systemCulture">The system culture name.</param>
    /// <param name="expectedToolbarCompose">The expected text of the Compose button.</param>
    [Theory]
    [InlineData("zh-CN", "写邮件")]
    [InlineData("en-US", "Compose")]
    [InlineData("de-DE", "Compose")]
    public void SystemPreference_FollowsOperatingSystemLanguage(string systemCulture, string expectedToolbarCompose)
    {
        var localizer = new LocalizationManager(CultureInfo.GetCultureInfo(systemCulture));

        localizer["Toolbar_Compose"].Should().Be(expectedToolbarCompose);
        localizer.Preference.Should().Be("system");
    }

    /// <summary>
    /// Verifies an explicit preference wins over the system language, and unknown values fall back to system.
    /// </summary>
    [Fact]
    public void SetPreference_ExplicitAndUnknown_AppliesExpectedLanguage()
    {
        var localizer = new LocalizationManager(CultureInfo.GetCultureInfo("zh-CN"));

        localizer.SetPreference("en");
        localizer.Language.Should().Be("en");

        localizer.SetPreference("klingon");
        localizer.Preference.Should().Be("system");
        localizer.Language.Should().Be("zh-Hans");
    }

    /// <summary>
    /// Verifies switching raises both the language event and the indexer change that refreshes XAML bindings, only when the language changes.
    /// </summary>
    [Fact]
    public void SetPreference_LanguageChanges_RaisesNotifications()
    {
        var localizer = TestLocalizer.Chinese();
        var languageChanged = 0;
        var changedProperties = new List<string?>();
        localizer.LanguageChanged += (_, _) => languageChanged++;
        localizer.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        localizer.SetPreference("en");
        localizer.SetPreference("en");

        languageChanged.Should().Be(1);
        changedProperties.Should().Equal("Item[]");
        localizer["Toolbar_Sync"].Should().Be("Sync");
    }

    /// <summary>
    /// Verifies formatting uses the arguments and a missing key is visible instead of throwing.
    /// </summary>
    [Fact]
    public void FormatAndIndexer_Keys_ReturnFormattedTextOrMarker()
    {
        var english = TestLocalizer.English();

        english.Format("Status_SyncDone", 3).Should().Be("Sync complete, 3 new message(s)");
        TestLocalizer.Chinese().Format("Status_SyncDone", 3).Should().Be("同步完成，3 封新邮件");
        english["No_Such_Key"].Should().Be("!No_Such_Key!");
    }

    /// <summary>
    /// Verifies both languages define exactly the same keys, none is empty, and format placeholders match.
    /// </summary>
    [Fact]
    public void Resources_BothLanguages_AreComplete()
    {
        var english = ReadAll(CultureInfo.InvariantCulture);
        var chinese = ReadAll(CultureInfo.GetCultureInfo("zh-Hans"));

        chinese.Keys.Should().BeEquivalentTo(english.Keys);
        english.Values.Should().OnlyContain(v => v.Length > 0);
        chinese.Values.Should().OnlyContain(v => v.Length > 0);
        foreach (var (key, value) in english)
        {
            Placeholders(chinese[key]).Should().BeEquivalentTo(Placeholders(value), $"placeholders of '{key}' must match");
        }
    }

    /// <summary>
    /// Verifies every key referenced by the code or XAML exists, and every defined key is used somewhere.
    /// </summary>
    [Fact]
    public void Resources_KeysInSource_MatchDefinedKeys()
    {
        var defined = ReadAll(CultureInfo.InvariantCulture).Keys.ToHashSet();
        var used = new HashSet<string>();
        var desktopDirectory = Path.Combine(RepositoryRoot(), "src", "Swiftlet.Desktop");
        foreach (var file in Directory.EnumerateFiles(desktopDirectory, "*.*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                used.UnionWith(CodeKeys().Matches(text).Select(m => m.Groups[1].Value));
            }
            else if (file.EndsWith(".axaml", StringComparison.Ordinal))
            {
                used.UnionWith(XamlKeys().Matches(text).Select(m => m.Groups[1].Value));
            }
        }

        used.Should().NotBeEmpty();
        used.Except(defined).Should().BeEmpty("every referenced key must be defined");
        defined.Except(used).Should().BeEmpty("every defined key should be used");
    }

    /// <summary>
    /// Verifies the folder tree, placeholder subjects and status text follow a language switch at runtime.
    /// </summary>
    [Fact]
    public async Task SwitchingLanguage_UpdatesFolderNamesSubjectsAndStatusText()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        scenario.Messages[1] = Swiftlet.Desktop.Tests.TestData.Message(scenario.Account, scenario.Inbox, 2, subject: string.Empty);
        await scenario.StartAsync();
        var inbox = scenario.ViewModel.Accounts[0].Folders[0];
        var changed = new List<string?>();
        inbox.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        inbox.Name.Should().Be("收件箱");
        scenario.ViewModel.Messages[0].Subject.Should().Be("(无主题)");

        scenario.Localizer.SetPreference("en");

        inbox.Name.Should().Be("Inbox");
        changed.Should().Contain("Name");
        scenario.ViewModel.Messages[0].Subject.Should().Be("(No subject)");

        await scenario.ViewModel.SyncCommand.ExecuteAsync(null);
        scenario.ViewModel.StatusText.Should().Be("Sync complete, 1 new message(s)");
    }

    /// <summary>
    /// Verifies errors from the view models are reported in the current language.
    /// </summary>
    [Fact]
    public async Task ErrorMessages_EnglishLocalizer_AreEnglish()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        scenario.Dispatcher.On<SyncAccountCommand>(_ => new SyncResult(1, 0, ["Broken"]));
        scenario.Dispatcher.On<GetMessagesQuery>(_ => throw new IOException("db locked"));
        scenario.Localizer.SetPreference("en");

        await scenario.StartAsync();

        scenario.ViewModel.StatusText.Should().Be("Failed to load messages: db locked");

        scenario.Dispatcher.On<GetMessagesQuery>(_ => new List<Swiftlet.Domain.Messages.MailMessage>());
        await scenario.ViewModel.SyncCommand.ExecuteAsync(null);
        scenario.ViewModel.StatusText.Should().Contain("1 folder(s) failed to sync").And.Contain("problems:");
    }

    /// <summary>
    /// Verifies reply and forward templates are localized.
    /// </summary>
    [Fact]
    public void Templates_English_UseEnglishHeaders()
    {
        var account = Swiftlet.Desktop.Tests.TestData.Account();
        var message = Swiftlet.Desktop.Tests.TestData.Message(
            account,
            Swiftlet.Desktop.Tests.TestData.Folder(account, "INBOX", FolderRole.Inbox),
            1);
        var english = TestLocalizer.English();

        ComposeTemplates.Reply(message, english).Body.Should().Contain("wrote:").And.Contain("Alice <alice@example.com>");
        ComposeTemplates.Forward(message, english).Body
            .Should().Contain("Forwarded message").And.Contain("From: Alice <alice@example.com>").And.Contain("Subject: Hello");
    }

    /// <summary>
    /// Verifies dialog view models use the current language for hints and validation.
    /// </summary>
    [Fact]
    public async Task DialogViewModels_English_UseEnglishText()
    {
        var localizer = TestLocalizer.English();
        var addAccount = new AddAccountViewModel(new FakeDispatcher(), localizer) { Email = "me@gmail.com", Password = "x", ImapPort = "abc" };
        addAccount.ProviderHint.Should().Contain("app-specific password");
        await addAccount.AddCommand.ExecuteAsync(null);
        addAccount.ErrorMessage.Should().Be("Ports must be numbers");

        var account = Swiftlet.Desktop.Tests.TestData.Account();
        var compose = new ComposeViewModel(
            new FakeDispatcher(),
            new FakeDialogService(),
            localizer,
            [account],
            new ComposeRequest(ComposeMode.Reply, account.Id, string.Empty, "Re: x", string.Empty, null));
        compose.Title.Should().Be("Reply");
        await compose.SendCommand.ExecuteAsync(null);
        compose.ErrorMessage.Should().Be("Please enter at least one recipient");
    }

    /// <summary>
    /// Verifies the language picker applies and saves the selection and reflects the stored preference at startup.
    /// </summary>
    [Fact]
    public void LanguageSelector_Selection_AppliesAndSaves()
    {
        var localizer = TestLocalizer.Chinese();
        var store = new FakeLanguageStore { Preference = "zh-Hans" };
        var selector = new LanguageSelectorViewModel(localizer, store, NullLogger<LanguageSelectorViewModel>.Instance);
        selector.SelectedOption.Code.Should().Be("zh-Hans");
        store.Preference = "untouched";

        selector.SelectedOption = selector.Options.Single(o => o.Code == "en");

        localizer.Language.Should().Be("en");
        store.Preference.Should().Be("en");
        selector.Options.Single(o => o.Code == "system").DisplayName.Should().Be("Follow system");
    }

    /// <summary>
    /// Verifies a failing settings file does not prevent the language from changing.
    /// </summary>
    [Fact]
    public void LanguageSelector_SaveFails_StillAppliesLanguage()
    {
        var localizer = TestLocalizer.Chinese();
        var store = new FakeLanguageStore { SaveFailure = new IOException("read-only disk") };
        var selector = new LanguageSelectorViewModel(localizer, store, NullLogger<LanguageSelectorViewModel>.Instance);

        selector.SelectedOption = selector.Options.Single(o => o.Code == "en");

        localizer.Language.Should().Be("en");
    }

    /// <summary>
    /// Verifies the JSON store round-trips, defaults to system when missing and survives a corrupt file.
    /// </summary>
    [Fact]
    public void JsonLanguagePreferenceStore_MissingSavedAndCorrupt_BehavesSafely()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "nested", "settings.json");
        try
        {
            var store = new JsonLanguagePreferenceStore(path);
            store.Load().Should().Be("system");

            store.Save("zh-Hans");
            new JsonLanguagePreferenceStore(path).Load().Should().Be("zh-Hans");

            File.WriteAllText(path, "{ not json");
            store.Load().Should().Be("system");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Reads all strings of one language without falling back to other languages.
    /// </summary>
    /// <param name="culture">The culture.</param>
    /// <returns>The key/value pairs.</returns>
    private static Dictionary<string, string> ReadAll(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
        return set.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    /// <summary>
    /// Extracts the distinct <c>{n}</c> format placeholders of a string.
    /// </summary>
    /// <param name="value">The format string.</param>
    /// <returns>The placeholder indexes.</returns>
    private static string[] Placeholders(string value)
    {
        return [.. PlaceholderPattern().Matches(value).Select(m => m.Value).Distinct().Order()];
    }

    /// <summary>
    /// Finds the repository root from the location of this source file.
    /// </summary>
    /// <param name="sourceFile">The path of this file, supplied by the compiler.</param>
    /// <returns>The repository root directory.</returns>
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
    }

    /// <summary>
    /// Matches resource keys used from C# code.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"(?:_?localizer(?:\[|\.Format\()|GuardedAsync\()\s*""([A-Za-z]+_[A-Za-z]+)""")]
    private static partial Regex CodeKeys();

    /// <summary>
    /// Matches resource keys used from XAML.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"\{loc:Loc ([A-Za-z]+_[A-Za-z]+)\}")]
    private static partial Regex XamlKeys();

    /// <summary>
    /// Matches format placeholders such as <c>{0}</c>.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();
}
