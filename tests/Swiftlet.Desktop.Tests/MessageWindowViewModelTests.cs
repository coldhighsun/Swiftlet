using AwesomeAssertions;
using Mediator;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Queries;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Xunit;
using Scenario = Swiftlet.Desktop.Tests.MainWindowViewModelTests.Scenario;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the window that shows one message, and for opening it from the main window.
/// </summary>
public class MessageWindowViewModelTests
{
    /// <summary>
    /// Starts a scenario, selects the newest message and opens its window.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The view model of the opened window.</returns>
    private static async Task<MessageWindowViewModel> OpenAsync(Scenario scenario)
    {
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.OpenMessageWindow(scenario.ViewModel.Messages[0]);
        return scenario.Dialogs.MessageWindows[^1];
    }

    /// <summary>
    /// Gives the account of a scenario a Trash and an archive folder.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    private static void AddTrashAndArchive(Scenario scenario)
    {
        var trash = TestData.Folder(scenario.Account, "Trash", FolderRole.Trash);
        var archive = TestData.Folder(scenario.Account, "Archive", FolderRole.Archive);
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Inbox, trash, archive });
    }

    /// <summary>
    /// Answers quick actions, taking the message away when they succeed.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="result">The answer.</param>
    private static void AnswerQuickActions(Scenario scenario, QuickMailActionResult result)
    {
        scenario.Dispatcher.On<QuickMailActionCommand>(c =>
        {
            if (result == QuickMailActionResult.Done)
            {
                scenario.Messages.RemoveAll(m => m.Id == c.MessageId);
            }

            return result;
        });
    }

    /// <summary>
    /// Verifies opening a message shows a window for exactly that item.
    /// </summary>
    [Fact]
    public async Task OpenMessageWindow_Message_ShowsWindowForIt()
    {
        var scenario = new Scenario();

        var window = await OpenAsync(scenario);

        window.Item.Should().BeSameAs(scenario.ViewModel.Messages[0]);
        window.Main.Should().BeSameAs(scenario.ViewModel);
    }

    /// <summary>
    /// Verifies a message that has a window already is shown in it again.
    /// </summary>
    [Fact]
    public async Task OpenMessageWindow_AlreadyOpen_ShowsTheSameViewModel()
    {
        var scenario = new Scenario();
        var first = await OpenAsync(scenario);

        scenario.ViewModel.OpenMessageWindow(scenario.ViewModel.Messages[0]);

        scenario.Dialogs.MessageWindows.Should().HaveCount(2);
        scenario.Dialogs.MessageWindows[1].Should().BeSameAs(first);
    }

    /// <summary>
    /// Verifies a closed window is forgotten.
    /// </summary>
    [Fact]
    public async Task OpenMessageWindow_AfterClose_CreatesNewViewModel()
    {
        var scenario = new Scenario();
        var first = await OpenAsync(scenario);
        first.Detach();

        scenario.ViewModel.OpenMessageWindow(scenario.ViewModel.Messages[0]);

        scenario.Dialogs.MessageWindows[1].Should().NotBeSameAs(first);
    }

    /// <summary>
    /// Verifies reply and forward start from the message of the window, whatever the main window shows.
    /// </summary>
    [Fact]
    public async Task ReplyAndForward_OtherMessageSelected_UseTheWindowMessage()
    {
        var scenario = new Scenario();
        var window = await OpenAsync(scenario);
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[1];

        await window.ReplyCommand.ExecuteAsync(null);
        await window.ForwardCommand.ExecuteAsync(null);

        scenario.Dialogs.ComposeRequests.Select(r => r.Mode).Should().Equal(ComposeMode.Reply, ComposeMode.Forward);
        scenario.Dialogs.ComposeRequests.Should().OnlyContain(r => r.Subject.Contains(window.Item.Subject));
    }

    /// <summary>
    /// Verifies the star is saved and shown.
    /// </summary>
    [Fact]
    public async Task ToggleFlag_Unflagged_FlagsTheMessage()
    {
        var scenario = new Scenario();
        var window = await OpenAsync(scenario);

        await window.ToggleFlagCommand.ExecuteAsync(null);

        window.Item.IsFlagged.Should().BeTrue();
        scenario.Dispatcher.Sent<SetMessageFlaggedCommand>().Should().ContainSingle()
            .Which.MessageId.Should().Be(window.Item.Id);
    }

    /// <summary>
    /// Verifies a failed star is rolled back and reported.
    /// </summary>
    [Fact]
    public async Task ToggleFlag_Fails_RollsBack()
    {
        var scenario = new Scenario();
        var window = await OpenAsync(scenario);
        scenario.Dispatcher.On<SetMessageFlaggedCommand>(_ => throw new IOException("disk full"));

        await window.ToggleFlagCommand.ExecuteAsync(null);

        window.Item.IsFlagged.Should().BeFalse();
        scenario.ViewModel.StatusText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies the list row follows the star when the list holds another object for the same message.
    /// </summary>
    [Fact]
    public async Task ToggleFlag_ListWasRebuilt_UpdatesTheListedRow()
    {
        var scenario = new Scenario();
        var window = await OpenAsync(scenario);
        scenario.ViewModel.SelectedNode = scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == scenario.Sent.Id);
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.SelectedNode = scenario.ViewModel.UnifiedInbox;
        await scenario.ViewModel.WhenIdleAsync();
        var listed = scenario.ViewModel.Messages.Single(m => m.Id == window.Item.Id);
        listed.Should().NotBeSameAs(window.Item);

        await window.ToggleFlagCommand.ExecuteAsync(null);

        listed.IsFlagged.Should().BeTrue();
        window.Item.IsFlagged.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a cancelled confirmation sends nothing and keeps the window.
    /// </summary>
    [Fact]
    public async Task Delete_Cancelled_SendsNothing()
    {
        var scenario = new Scenario();
        AddTrashAndArchive(scenario);
        var window = await OpenAsync(scenario);
        var closed = 0;
        window.CloseRequested += (_, _) => closed++;

        await window.DeleteCommand.ExecuteAsync(null);

        scenario.Dialogs.Confirmations.Should().ContainSingle();
        scenario.Dispatcher.Sent<QuickMailActionCommand>().Should().BeEmpty();
        scenario.Dispatcher.Sent<DeleteMessagesCommand>().Should().BeEmpty();
        closed.Should().Be(0);
    }

    /// <summary>
    /// Verifies a delete to Trash names the folder the message was seen in, closes the window and selects the next row.
    /// </summary>
    [Fact]
    public async Task Delete_ToTrash_QueuesClosesAndSelectsNext()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        AddTrashAndArchive(scenario);
        AnswerQuickActions(scenario, QuickMailActionResult.Done);
        var window = await OpenAsync(scenario);
        var next = scenario.ViewModel.Messages[1];
        var closed = 0;
        window.CloseRequested += (_, _) => closed++;

        await window.DeleteCommand.ExecuteAsync(null);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<QuickMailActionCommand>().Should().ContainSingle().Which.Should().Be(
            new QuickMailActionCommand(scenario.Account.Id, window.Item.Id, scenario.Inbox.Id, QuickMailAction.Delete));
        closed.Should().Be(1);
        scenario.ViewModel.Messages.Should().Equal(next);
        scenario.ViewModel.SelectedMessage.Should().BeSameAs(next);
    }

    /// <summary>
    /// Verifies removing the message of a window leaves another selection of the main window alone.
    /// </summary>
    [Fact]
    public async Task Delete_OtherMessageSelected_KeepsTheSelection()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        AddTrashAndArchive(scenario);
        AnswerQuickActions(scenario, QuickMailActionResult.Done);
        var window = await OpenAsync(scenario);
        var other = scenario.ViewModel.Messages[1];
        scenario.ViewModel.SelectedMessage = other;
        scenario.ViewModel.SelectedMessages.Clear();
        scenario.ViewModel.SelectedMessages.Add(other);

        await window.DeleteCommand.ExecuteAsync(null);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.SelectedMessage.Should().BeSameAs(other);
        scenario.ViewModel.SelectedMessages.Should().Equal(other);
        scenario.ViewModel.Messages.Should().Equal(other);
    }

    /// <summary>
    /// Verifies a permanent delete is confirmed as such and queued as a delete.
    /// </summary>
    [Fact]
    public async Task Delete_WithoutTrash_ConfirmsPermanentAndDeletes()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteMessagesCommand>(c =>
        {
            scenario.Messages.RemoveAll(m => c.Messages.Any(k => k.MessageId == m.Id));
            return c.Messages.Count;
        });
        var window = await OpenAsync(scenario);
        var closed = 0;
        window.CloseRequested += (_, _) => closed++;

        await window.DeleteCommand.ExecuteAsync(null);

        scenario.Dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().Contain("永久删除");
        scenario.Dispatcher.Sent<DeleteMessagesCommand>().Should().ContainSingle()
            .Which.Messages.Should().Equal(new MessageKey(scenario.Account.Id, window.Item.Id));
        closed.Should().Be(1);
    }

    /// <summary>
    /// Verifies a message that was moved or removed meanwhile is left alone and the window says so.
    /// </summary>
    /// <param name="result">The answer of the quick action.</param>
    [Theory]
    [InlineData(QuickMailActionResult.Gone)]
    [InlineData(QuickMailActionResult.Unavailable)]
    public async Task Delete_NotPossibleAnymore_KeepsWindowAndSaysSo(QuickMailActionResult result)
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        AddTrashAndArchive(scenario);
        AnswerQuickActions(scenario, result);
        var window = await OpenAsync(scenario);
        var closed = 0;
        window.CloseRequested += (_, _) => closed++;

        await window.DeleteCommand.ExecuteAsync(null);

        closed.Should().Be(0);
        scenario.ViewModel.StatusText.Should().Be(scenario.Localizer["Status_MessageGone"]);
        scenario.ViewModel.Messages.Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies a failed delete is reported and the window stays.
    /// </summary>
    [Fact]
    public async Task Delete_Fails_KeepsWindowAndShowsError()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        AddTrashAndArchive(scenario);
        scenario.Dispatcher.On<QuickMailActionCommand>(_ => throw new IOException("disk full"));
        var window = await OpenAsync(scenario);
        var closed = 0;
        window.CloseRequested += (_, _) => closed++;

        await window.DeleteCommand.ExecuteAsync(null);

        closed.Should().Be(0);
        scenario.ViewModel.StatusText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies archiving queues the move and closes the window.
    /// </summary>
    [Fact]
    public async Task Archive_WithArchiveFolder_QueuesAndCloses()
    {
        var scenario = new Scenario();
        AddTrashAndArchive(scenario);
        AnswerQuickActions(scenario, QuickMailActionResult.Done);
        var window = await OpenAsync(scenario);
        var closed = 0;
        window.CloseRequested += (_, _) => closed++;

        await window.ArchiveCommand.ExecuteAsync(null);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<QuickMailActionCommand>().Should().ContainSingle()
            .Which.Action.Should().Be(QuickMailAction.Archive);
        scenario.Dialogs.Confirmations.Should().BeEmpty();
        closed.Should().Be(1);
        scenario.ViewModel.Messages.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies archiving is off for an account without an archive folder.
    /// </summary>
    [Fact]
    public async Task Archive_WithoutArchiveFolder_IsDisabled()
    {
        var scenario = new Scenario();

        var window = await OpenAsync(scenario);

        window.ArchiveCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a message that is no longer listed still follows a language change while its window is open.
    /// </summary>
    [Fact]
    public async Task LanguageChange_MessageNotListed_RefreshesTheOpenWindow()
    {
        var scenario = new Scenario();
        var window = await OpenAsync(scenario);
        scenario.ViewModel.SelectedNode = scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == scenario.Sent.Id);
        await scenario.ViewModel.WhenIdleAsync();
        var changed = new List<string?>();
        window.Item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        scenario.Localizer.SetPreference("en");

        changed.Should().Contain(nameof(MessageItemViewModel.Subject));
    }
}
