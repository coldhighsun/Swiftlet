using AwesomeAssertions;
using Avalonia.Controls;
using Swiftlet.Application.Queries;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the tray tooltip, the tray menu and the close-to-tray rule.
/// </summary>
public class TrayTests
{
    /// <summary>
    /// Verifies the tooltip names the total first and then every account.
    /// </summary>
    [Fact]
    public void Format_SeveralAccounts_ShowsTotalAndEachAccount()
    {
        var text = TraySummary.Format([("a@example.com", 3), ("b@example.com", 0)], TestLocalizer.English());

        text.Should().Be("Swiftlet - Unread: 3\na@example.com: 3\nb@example.com: 0");
    }

    /// <summary>
    /// Verifies a mailbox without unread mail says so.
    /// </summary>
    [Fact]
    public void Format_NothingUnread_SaysSo()
    {
        var text = TraySummary.Format([("a@example.com", 0)], TestLocalizer.Chinese());

        text.Should().Be("Swiftlet - 没有未读邮件\na@example.com：0");
    }

    /// <summary>
    /// Verifies the tooltip without accounts.
    /// </summary>
    [Fact]
    public void Format_NoAccounts_SaysSo()
    {
        var text = TraySummary.Format([], TestLocalizer.English());

        text.Should().Be("Swiftlet - No accounts");
    }

    /// <summary>
    /// Verifies accounts that do not fit are counted in a last line and the system limit is kept.
    /// </summary>
    [Fact]
    public void Format_TooManyAccounts_CountsTheRestWithinTheLimit()
    {
        var accounts = Enumerable.Range(1, 10).Select(i => ($"someone-{i}@example.com", 1)).ToList();

        var text = TraySummary.Format(accounts, TestLocalizer.English());

        var lines = text.Split('\n');
        text.Length.Should().BeLessThanOrEqualTo(TraySummary.MaxLength);
        var listed = lines.Length - 2;
        listed.Should().BePositive();
        lines[^1].Should().Be($"More accounts: {10 - listed}");
    }

    /// <summary>
    /// Verifies an address longer than the tooltip is left out rather than cut in half.
    /// </summary>
    [Fact]
    public void Format_VeryLongAddress_IsOnlyCounted()
    {
        var text = TraySummary.Format([(new string('x', 200) + "@example.com", 2)], TestLocalizer.English());

        text.Should().Be("Swiftlet - Unread: 2\nMore accounts: 1");
    }

    /// <summary>
    /// Verifies only the user closing the window hides it, and only when the setting is on.
    /// </summary>
    /// <param name="enabled">The setting.</param>
    /// <param name="reason">Why the window closes.</param>
    /// <param name="isProgrammatic">Whether code closes it.</param>
    /// <param name="expected">Whether the window is hidden.</param>
    [Theory]
    [InlineData(true, WindowCloseReason.WindowClosing, false, true)]
    [InlineData(false, WindowCloseReason.WindowClosing, false, false)]
    [InlineData(true, WindowCloseReason.WindowClosing, true, false)]
    [InlineData(true, WindowCloseReason.ApplicationShutdown, false, false)]
    [InlineData(true, WindowCloseReason.OSShutdown, false, false)]
    public void ShouldHide_ByReason_HidesOnlyUserClose(bool enabled, WindowCloseReason reason, bool isProgrammatic, bool expected)
    {
        CloseToTray.ShouldHide(enabled, reason, isProgrammatic).Should().Be(expected);
    }

    /// <summary>
    /// Verifies the tooltip shows the unread inbox mail of loaded accounts.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ToolTipText_AccountsLoaded_ShowsUnreadCount()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        using var tray = Create(scenario);
        tray.ToolTipText.Should().Be("Swiftlet");

        await scenario.StartAsync();

        tray.ToolTipText.Should().Be($"Swiftlet - 2 封未读\n{scenario.Account.EmailAddress}：2");
    }

    /// <summary>
    /// Verifies the tooltip follows later changes of the unread counts.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ToolTipText_UnreadChanges_IsUpdated()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        await scenario.StartAsync();
        using var tray = Create(scenario);
        scenario.Dispatcher.On<GetUnreadCountsQuery>(_ => (IReadOnlyDictionary<Guid, int>)new Dictionary<Guid, int>
        {
            [scenario.Inbox.Id] = 7,
            [scenario.Sent.Id] = 5,
        });

        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        tray.ToolTipText.Should().StartWith("Swiftlet - 7 封未读", "only the inbox counts");
    }

    /// <summary>
    /// Verifies the tooltip and the menu texts follow the language.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Texts_LanguageChanged_AreRebuilt()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        await scenario.StartAsync();
        using var tray = Create(scenario);
        var changed = new List<string?>();
        tray.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        scenario.Localizer.SetPreference("en");

        tray.OpenText.Should().Be("Open Swiftlet");
        tray.ToolTipText.Should().StartWith("Swiftlet - Unread: 2");
        changed.Should().Contain([nameof(TrayViewModel.OpenText), nameof(TrayViewModel.ExitText), nameof(TrayViewModel.ToolTipText)]);
    }

    /// <summary>
    /// Verifies opening and quitting are passed on to the application.
    /// </summary>
    [Fact]
    public void OpenAndExit_Executed_CallTheApplication()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        var shown = 0;
        var quit = 0;
        using var tray = new TrayViewModel(scenario.ViewModel, scenario.Localizer, () => shown++, () => quit++);

        tray.OpenCommand.Execute(null);
        tray.ExitCommand.Execute(null);

        shown.Should().Be(1);
        quit.Should().Be(1);
    }

    /// <summary>
    /// Verifies writing a message does not bring up the main window.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Compose_Executed_OpensComposeOnly()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        await scenario.StartAsync();
        var shown = 0;
        using var tray = new TrayViewModel(scenario.ViewModel, scenario.Localizer, () => shown++, () => { });

        await tray.ComposeCommand.ExecuteAsync(null);

        scenario.Dialogs.ComposeRequests.Should().ContainSingle();
        shown.Should().Be(0);
    }

    /// <summary>
    /// Verifies opening the settings does not bring up the main window.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Settings_Executed_OpensSettingsOnly()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        var shown = 0;
        using var tray = new TrayViewModel(scenario.ViewModel, scenario.Localizer, () => shown++, () => { });

        await tray.SettingsCommand.ExecuteAsync(null);

        scenario.Dialogs.SettingsCalls.Should().Be(1);
        shown.Should().Be(0);
    }

    /// <summary>
    /// Verifies the tray synchronizes every account although a folder of one of them is selected.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncAll_FolderSelected_SynchronizesEveryAccount()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        var other = TestData.Account();
        scenario.Dispatcher.On<GetAccountsQuery>(_ => new List<MailAccount> { scenario.Account, other });
        await scenario.StartAsync();
        using var tray = Create(scenario);

        await tray.SyncAllCommand.ExecuteAsync(null);

        scenario.Dispatcher.Sent<SyncAccountCommand>().Select(c => c.AccountId)
            .Should().BeEquivalentTo([scenario.Account.Id, other.Id]);
    }

    /// <summary>
    /// Creates a tray view model that ignores opening and quitting.
    /// </summary>
    /// <param name="scenario">The main window scenario.</param>
    /// <returns>The view model.</returns>
    private static TrayViewModel Create(MainWindowViewModelTests.Scenario scenario)
    {
        return new TrayViewModel(scenario.ViewModel, scenario.Localizer, () => { }, () => { });
    }
}
