using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Mediator;
using Swiftlet.Application.Ai;
using Swiftlet.Application.Labels;
using Swiftlet.Application.Messages;
using Swiftlet.Desktop.ViewModels.Ai;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests the classification dialog and the message menu of the main window.
/// </summary>
public sealed class ClassifyViewModelTests
{
    /// <summary>
    /// The dispatcher.
    /// </summary>
    private readonly FakeDispatcher _dispatcher = new();

    /// <summary>
    /// A label.
    /// </summary>
    private readonly Label _work = Label.Create("Work", "#E53935");

    /// <summary>
    /// The selected messages.
    /// </summary>
    private readonly List<MailMessage> _messages;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClassifyViewModelTests"/> class.
    /// </summary>
    public ClassifyViewModelTests()
    {
        var account = TestData.Account();
        var inbox = TestData.Folder(account, "INBOX", FolderRole.Inbox);
        _messages = [TestData.Message(account, inbox, 1, "one"), TestData.Message(account, inbox, 2, "two")];
        _dispatcher.On<SetMessageLabelCommand>(_ => Unit.Value);
        _dispatcher.On<SetMessagePriorityCommand>(_ => Unit.Value);
    }

    /// <summary>
    /// Suggestions arrive ticked, the others say there is nothing, and nothing is saved before applying.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Start_Results_ShowsSuggestions()
    {
        AnswerFirstOnly();
        var viewModel = Create();

        viewModel.Start();
        await viewModel.Running;

        viewModel.ProgressText.Should().Be("2 of 2 done");
        viewModel.Items[0].Suggestions.Select(s => (s.Text, s.IsChecked)).Should().Equal(
            ("Label: Work", true), ("Priority: High", true));
        viewModel.Items[0].StatusText.Should().BeNull();
        viewModel.Items[1].StatusText.Should().Be("No suggestion");
        viewModel.ApplyCommand.CanExecute(null).Should().BeTrue();
        _dispatcher.Sent<SetMessageLabelCommand>().Should().BeEmpty();
        _dispatcher.Sent<SetMessagePriorityCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// Applying saves only what is ticked and closes.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task ApplyCommand_Unticked_SavesOnlyTicked()
    {
        AnswerFirstOnly();
        var viewModel = Create();
        var closed = false;
        viewModel.CloseRequested += (_, _) => closed = true;
        viewModel.Start();
        await viewModel.Running;
        viewModel.Items[0].Suggestions[0].IsChecked = false;

        await viewModel.ApplyCommand.ExecuteAsync(null);

        _dispatcher.Sent<SetMessageLabelCommand>().Should().BeEmpty();
        _dispatcher.Sent<SetMessagePriorityCommand>().Should().ContainSingle().Which.Should().Be(
            new SetMessagePriorityCommand(_messages[0].AccountId, _messages[0].Id, MessagePriority.High));
        viewModel.HasChanges.Should().BeTrue();
        closed.Should().BeTrue();
    }

    /// <summary>
    /// A failed save keeps the window open, and what was saved before counts as a change.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task ApplyCommand_SecondSaveFails_ShowsItAndKeepsOpen()
    {
        AnswerFirstOnly();
        _dispatcher.On<SetMessagePriorityCommand>(_ => throw new InvalidOperationException("disk full"));
        var viewModel = Create();
        var closed = false;
        viewModel.CloseRequested += (_, _) => closed = true;
        viewModel.Start();
        await viewModel.Running;

        await viewModel.ApplyCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().Be("Could not apply the suggestions: disk full");
        viewModel.HasChanges.Should().BeTrue();
        closed.Should().BeFalse();
    }

    /// <summary>
    /// A failure of the whole request is shown and the unanswered messages say so.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Start_RequestFails_ShowsTheReason()
    {
        _dispatcher.OnStream<ClassifyMessagesQuery, MessageClassification>((_, _) => throw new AiException(AiFailure.Network));
        var viewModel = Create();

        viewModel.Start();
        await viewModel.Running;

        viewModel.ErrorText.Should().Be("The service could not be reached.");
        viewModel.Items.Should().OnlyContain(i => i.StatusText == "Not classified");
        viewModel.ApplyCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Only the first twenty messages are listed, with a note.
    /// </summary>
    [Fact]
    public void Constructor_ManyMessages_LimitsAndSaysSo()
    {
        var many = Enumerable.Repeat(_messages[0], 21).ToList();

        var viewModel = new ClassifyViewModel(_dispatcher, TestLocalizer.English(), many, [_work]);

        viewModel.Items.Should().HaveCount(ClassifyMessagesQuery.MaxMessages);
        viewModel.LimitText.Should().Contain("20");
    }

    /// <summary>
    /// The menu command passes the selection and the labels to the dialog and reloads after changes.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task ClassifySelectedCommand_Changes_ReloadsTheList()
    {
        var scenario = new MainWindowViewModelTests.Scenario(withAi: true);
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];
        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();
        scenario.Dialogs.OnClassify = () =>
        {
            scenario.Messages.Single(m => m.Id == item.Id).SetAiPriority(MessagePriority.High);
            return true;
        };

        await scenario.ViewModel.ClassifySelectedCommand.ExecuteAsync(null);

        var call = scenario.Dialogs.ClassifyCalls.Should().ContainSingle().Subject;
        call.Messages.Should().ContainSingle().Which.Id.Should().Be(item.Id);
        call.Labels.Should().ContainSingle().Which.Name.Should().Be("Work");
        scenario.ViewModel.Messages.Single(m => m.Id == item.Id).IsHighPriority.Should().BeTrue();
    }

    /// <summary>
    /// Without AI there is no classification, while a priority can still be cleared.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task ClearPriorityCommand_WithoutAi_ClearsIt()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        scenario.Messages[0].SetAiPriority(MessagePriority.Low);
        scenario.Dispatcher.On<SetMessagePriorityCommand>(_ => Unit.Value);
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages.Single(m => m.PriorityText is not null);
        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.RefreshPriorityMenu();
        scenario.ViewModel.CanClearPriority.Should().BeTrue();
        scenario.ViewModel.ClassifySelectedCommand.CanExecute(null).Should().BeFalse();

        await scenario.ViewModel.ClearPriorityCommand.ExecuteAsync(null);

        item.PriorityText.Should().BeNull();
        scenario.Dispatcher.Sent<SetMessagePriorityCommand>().Should().ContainSingle().Which.Priority.Should().BeNull();
        scenario.ViewModel.RefreshPriorityMenu();
        scenario.ViewModel.CanClearPriority.Should().BeFalse();
    }

    /// <summary>
    /// A freshly loaded copy of a message updates the mark of its list item.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Update_FreshCopy_TakesThePriority()
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];
        var fresh = item.Message.CopyToFolder(item.Message.FolderId, item.Message.Uid, true);
        fresh.SetAiPriority(MessagePriority.High);

        item.Update(fresh);

        item.PriorityText.Should().Be("高");
    }

    /// <summary>
    /// Streams suggestions for the first message and none for the second.
    /// </summary>
    private void AnswerFirstOnly()
    {
        _dispatcher.OnStream<ClassifyMessagesQuery, MessageClassification>((query, token) => Results(
            [
                new(query.Messages[0], ClassificationStatus.Suggested, [_work.Id], MessagePriority.High),
                new(query.Messages[1], ClassificationStatus.NoSuggestion, []),
            ],
            token));
    }

    /// <summary>
    /// Streams prepared results.
    /// </summary>
    /// <param name="results">The results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream.</returns>
    internal static async IAsyncEnumerable<MessageClassification> Results(
        MessageClassification[] results,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return result;
        }
    }

    /// <summary>
    /// Creates the dialog view model in English.
    /// </summary>
    /// <returns>The view model.</returns>
    private ClassifyViewModel Create()
    {
        return new ClassifyViewModel(_dispatcher, TestLocalizer.English(), _messages, [_work]);
    }
}
