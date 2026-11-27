using AwesomeAssertions;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for leading the user to new mail when the tray icon is opened, and for stepping through that mail.
/// </summary>
public class NewMailNavigationTests
{
    /// <summary>
    /// Announces messages as new mail of the scenario's account.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="messages">The messages that arrived.</param>
    private static void Announce(MainWindowViewModelTests.Scenario scenario, params MailMessage[] messages)
    {
        scenario.SyncEvents.RaiseNewMail(new NewMailEventArgs(
            scenario.Account.Id,
            scenario.Account.EmailAddress,
            [.. messages.Select(m => new NewMailSummary("Alice", m.FromAddress, m.Subject, m.SentAt, m.Id, m.FolderId, string.Empty))]));
    }

    /// <summary>
    /// Creates a scenario with three unread inbox messages, the newest one being the last.
    /// </summary>
    /// <returns>The scenario.</returns>
    private static MainWindowViewModelTests.Scenario CreateScenario()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        scenario.Messages.Add(TestData.Message(scenario.Account, scenario.Inbox, 3, "Hello 3"));
        return scenario;
    }

    /// <summary>
    /// Verifies several new messages lead to the newest one and the set stays complete after the first is read.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ShowNewMailAsync_SeveralArrivals_SelectsNewestAndKeepsTheSet()
    {
        var scenario = CreateScenario();
        await scenario.StartInInboxAsync();
        Announce(scenario, scenario.Messages[0], scenario.Messages[1]);
        Announce(scenario, scenario.Messages[2]);

        await scenario.ViewModel.ShowNewMailAsync();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.SelectedNode.Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
        scenario.ViewModel.SelectedMessage!.Id.Should().Be(scenario.Messages[2].Id);
        scenario.ViewModel.SelectedMessage.IsRead.Should().BeTrue("selecting marks it read");
        scenario.ViewModel.NewMail.Should().NotBeNull();
        scenario.ViewModel.NewMail!.Count.Should().Be(3, "reading a message does not shrink the set");
        scenario.ViewModel.NewMail.PositionText.Should().Be("第 1 / 3 封");
    }

    /// <summary>
    /// Verifies next and previous walk through the set from the newest to the oldest and back.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task NextAndPrevious_SeveralArrivals_WalkTheSetInOrder()
    {
        var scenario = CreateScenario();
        await scenario.StartAsync();
        Announce(scenario, [.. scenario.Messages]);
        await scenario.ViewModel.ShowNewMailAsync();
        var navigator = scenario.ViewModel.NewMail!;

        navigator.PreviousCommand.CanExecute(null).Should().BeFalse("the newest message is shown");
        navigator.NextCommand.Execute(null);
        scenario.ViewModel.SelectedMessage!.Id.Should().Be(scenario.Messages[1].Id);
        navigator.PositionText.Should().Be("第 2 / 3 封");
        navigator.NextCommand.Execute(null);
        scenario.ViewModel.SelectedMessage.Id.Should().Be(scenario.Messages[0].Id);
        navigator.NextCommand.CanExecute(null).Should().BeFalse("the oldest message is shown");
        navigator.PreviousCommand.Execute(null);

        scenario.ViewModel.SelectedMessage.Id.Should().Be(scenario.Messages[1].Id);
        scenario.ViewModel.NewMail.Should().BeSameAs(navigator, "stepping stays inside the set");
    }

    /// <summary>
    /// Verifies a folder of one account does not keep the tray from showing the unified inbox.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ShowNewMailAsync_FolderSelected_SwitchesToTheUnifiedInbox()
    {
        var scenario = CreateScenario();
        await scenario.StartInInboxAsync();
        Announce(scenario, scenario.Messages[1], scenario.Messages[2]);

        await scenario.ViewModel.ShowNewMailAsync();

        scenario.ViewModel.SelectedNode.Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
        scenario.ViewModel.SelectedMessage!.Id.Should().Be(scenario.Messages[2].Id);
        scenario.ViewModel.NewMail.Should().NotBeNull("the node change must not drop the set that was just made");
    }

    /// <summary>
    /// Verifies a search in the unified inbox does not hide the new mail.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ShowNewMailAsync_UnifiedInboxSearched_ShowsTheWholeInboxAgain()
    {
        var scenario = CreateScenario();
        await scenario.StartAsync();
        scenario.ViewModel.SearchText = "Hello 1";
        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);
        scenario.ViewModel.Messages.Should().ContainSingle();
        Announce(scenario, scenario.Messages[1], scenario.Messages[2]);

        await scenario.ViewModel.ShowNewMailAsync();

        scenario.ViewModel.SearchText.Should().BeEmpty();
        scenario.ViewModel.Messages.Should().HaveCount(3);
        scenario.ViewModel.SelectedMessage!.Id.Should().Be(scenario.Messages[2].Id);
    }

    /// <summary>
    /// Verifies clicking the pop-up selects exactly the message it showed, from any node.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ShowMessageAsync_FolderSelected_SelectsThatMessageInTheUnifiedInbox()
    {
        var scenario = CreateScenario();
        await scenario.StartInInboxAsync();

        await scenario.ViewModel.ShowMessageAsync(scenario.Messages[0].Id);

        scenario.ViewModel.SelectedNode.Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
        scenario.ViewModel.SelectedMessage!.Id.Should().Be(scenario.Messages[0].Id);
        scenario.ViewModel.NewMail.Should().BeNull();
    }

    /// <summary>
    /// Verifies a single arrival is selected without a stepper.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ShowNewMailAsync_SingleArrival_SelectsItWithoutStepper()
    {
        var scenario = CreateScenario();
        await scenario.StartAsync();
        Announce(scenario, scenario.Messages[1]);

        await scenario.ViewModel.ShowNewMailAsync();

        scenario.ViewModel.SelectedMessage!.Id.Should().Be(scenario.Messages[1].Id);
        scenario.ViewModel.NewMail.Should().BeNull();
    }

    /// <summary>
    /// Verifies unread mail from before the start, which nobody announced, leads to the newest unread message.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ShowNewMailAsync_NothingAnnounced_SelectsNewestUnread()
    {
        var scenario = CreateScenario();
        scenario.Messages[2].SetRead(true);
        await scenario.StartAsync();

        await scenario.ViewModel.ShowNewMailAsync();

        scenario.ViewModel.SelectedMessage!.Id.Should().Be(scenario.Messages[1].Id);
        scenario.ViewModel.NewMail.Should().BeNull();
    }

    /// <summary>
    /// Verifies one opening uses up what was announced, so the next one only covers later arrivals.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ShowNewMailAsync_OpenedTwice_SecondTimeCoversOnlyLaterArrivals()
    {
        var scenario = CreateScenario();
        await scenario.StartAsync();
        Announce(scenario, scenario.Messages[0], scenario.Messages[1]);
        await scenario.ViewModel.ShowNewMailAsync();
        scenario.ViewModel.NewMail!.Count.Should().Be(2);

        await scenario.ViewModel.ShowNewMailAsync();

        scenario.ViewModel.NewMail.Should().BeNull("nothing new was announced in between");
    }

    /// <summary>
    /// Verifies choosing a message outside the set, or another node, ends the stepper, while one inside only moves it.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Selection_OutsideTheSet_EndsTheStepper()
    {
        var scenario = CreateScenario();
        await scenario.StartAsync();
        Announce(scenario, scenario.Messages[1], scenario.Messages[2]);
        await scenario.ViewModel.ShowNewMailAsync();
        var navigator = scenario.ViewModel.NewMail!;

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages.Single(m => m.Id == scenario.Messages[1].Id);
        scenario.ViewModel.NewMail.Should().BeSameAs(navigator);
        navigator.PositionText.Should().Be("第 2 / 2 封");

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages.Single(m => m.Id == scenario.Messages[0].Id);
        scenario.ViewModel.NewMail.Should().BeNull();
    }

    /// <summary>
    /// Verifies the stepper ends when fewer than two of the new messages are still listed.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Refresh_NewMessageRemoved_EndsTheStepperWhenOneIsLeft()
    {
        var scenario = CreateScenario();
        await scenario.StartAsync();
        Announce(scenario, scenario.Messages[1], scenario.Messages[2]);
        await scenario.ViewModel.ShowNewMailAsync();
        scenario.Messages.RemoveAt(2);

        await scenario.ViewModel.RefreshAsync();

        scenario.ViewModel.NewMail.Should().BeNull();
    }

    /// <summary>
    /// Verifies messages that vanished are skipped when stepping.
    /// </summary>
    [Fact]
    public void Step_MessageNoLongerListed_IsSkipped()
    {
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var available = ids.ToHashSet();
        Guid? shown = null;
        var navigator = new NewMailNavigator(ids, available.Contains, id => shown = id, TestLocalizer.English());
        navigator.ShowFirst().Should().BeTrue();
        available.Remove(ids[1]);
        navigator.Refresh();

        navigator.NextCommand.Execute(null);

        shown.Should().Be(ids[2]);
        navigator.Count.Should().Be(2);
        navigator.PositionText.Should().Be("2 of 2");
    }

    /// <summary>
    /// Verifies a set without any listed message has nothing to show.
    /// </summary>
    [Fact]
    public void ShowFirst_NothingAvailable_ReturnsFalse()
    {
        var navigator = new NewMailNavigator([Guid.NewGuid()], _ => false, _ => { }, TestLocalizer.English());

        navigator.ShowFirst().Should().BeFalse();
    }

    /// <summary>
    /// Verifies opening the tray while mail is unread shows the window and leads to the new mail.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task OpenCommand_UnreadMail_ShowsWindowAndLocatesNewMail()
    {
        var scenario = CreateScenario();
        await scenario.StartAsync();
        var shown = 0;
        using var tray = new TrayViewModel(
            scenario.ViewModel, scenario.Localizer, new FakeAppSettings(), () => shown++, () => { });
        Announce(scenario, scenario.Messages[1], scenario.Messages[2]);

        await tray.OpenCommand.ExecuteAsync(null);

        shown.Should().Be(1);
        scenario.ViewModel.SelectedMessage!.Id.Should().Be(scenario.Messages[2].Id);
        scenario.ViewModel.NewMail!.Count.Should().Be(2);
    }

    /// <summary>
    /// Verifies opening the tray without unread mail only shows the window and leaves the selection alone.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task OpenCommand_NothingUnread_OnlyShowsWindow()
    {
        var scenario = CreateScenario();
        foreach (var message in scenario.Messages)
        {
            message.SetRead(true);
        }

        await scenario.StartAsync();
        var shown = 0;
        using var tray = new TrayViewModel(
            scenario.ViewModel, scenario.Localizer, new FakeAppSettings(), () => shown++, () => { });
        tray.Indicator.Should().Be(TrayIndicator.None);

        await tray.OpenCommand.ExecuteAsync(null);

        shown.Should().Be(1);
        scenario.ViewModel.SelectedMessage.Should().BeNull();
        scenario.ViewModel.NewMail.Should().BeNull();
    }
}
