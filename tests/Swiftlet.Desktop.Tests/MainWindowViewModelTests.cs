using AwesomeAssertions;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Desktop.Localization;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Labels;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Queries;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="MainWindowViewModel"/> using a fake dispatcher and fake dialogs.
/// </summary>
public class MainWindowViewModelTests
{
    /// <summary>
    /// A fully wired view model with one account, an Inbox with two messages and a Sent folder.
    /// </summary>
    internal sealed class Scenario
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Scenario"/> class.
        /// </summary>
        /// <param name="hasAccount">Whether the account exists from the start.</param>
        public Scenario(bool hasAccount = true)
        {
            HasAccount = hasAccount;
            Account = TestData.Account();
            Inbox = TestData.Folder(Account, "INBOX", FolderRole.Inbox);
            Sent = TestData.Folder(Account, "Sent", FolderRole.Sent);
            Messages = [TestData.Message(Account, Inbox, 1, "Hello 1"), TestData.Message(Account, Inbox, 2, "Hello 2")];

            Work = Label.Create("Work", "#E53935");
            Dispatcher.On<GetLabelsQuery>(_ => (IReadOnlyList<Label>)new List<Label> { Work });
            Dispatcher.On<GetUnifiedInboxQuery>(_ => Messages.OrderByDescending(m => m.SentAt).ToList());
            Dispatcher.On<GetMessagesByLabelQuery>(q => Messages.Where(m => m.Labels.Any(l => l.Id == q.LabelId)).ToList());
            Dispatcher.On<GetAccountsQuery>(_ => HasAccount ? new List<MailAccount> { Account } : []);
            Dispatcher.On<GetFoldersQuery>(q => q.AccountId == Account.Id ? new List<MailFolder> { Sent, Inbox } : []);
            Dispatcher.On<GetMessagesQuery>(q => q.FolderId == Inbox.Id ? Messages.OrderByDescending(m => m.SentAt).ToList() : []);
            Dispatcher.On<SearchMessagesQuery>(q => Messages.Where(m => m.Subject.Contains(q.Text, StringComparison.Ordinal)).ToList());
            Dispatcher.On<SetMessageReadCommand>(_ => Unit.Value);
            Dispatcher.On<SetMessageFlaggedCommand>(_ => Unit.Value);
            Dispatcher.On<SetMessageLabelCommand>(_ => Unit.Value);
            Dispatcher.On<SyncAccountCommand>(_ => new SyncResult(2, 1, []));
            Localizer = TestLocalizer.Chinese();
            LanguageStore = new FakeLanguageStore();
            ViewModel = new MainWindowViewModel(
                Dispatcher,
                Dialogs,
                Localizer,
                new LanguageSelectorViewModel(Localizer, LanguageStore, NullLogger<LanguageSelectorViewModel>.Instance),
                SyncEvents,
                UiThread);
        }

        /// <summary>
        /// Gets the localizer, fixed to Simplified Chinese until a test switches it.
        /// </summary>
        public LocalizationManager Localizer { get; }

        /// <summary>
        /// Gets the in-memory language preference store.
        /// </summary>
        public FakeLanguageStore LanguageStore { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the account exists.
        /// </summary>
        public bool HasAccount { get; set; }

        /// <summary>
        /// Gets the dispatcher.
        /// </summary>
        public FakeDispatcher Dispatcher { get; } = new();

        /// <summary>
        /// Gets the sync notification source.
        /// </summary>
        public FakeSyncEvents SyncEvents { get; } = new();

        /// <summary>
        /// Gets the UI-thread marshaller.
        /// </summary>
        public FakeUiThread UiThread { get; } = new();

        /// <summary>
        /// Gets the dialogs.
        /// </summary>
        public FakeDialogService Dialogs { get; } = new();

        /// <summary>
        /// Gets the account.
        /// </summary>
        public MailAccount Account { get; }

        /// <summary>
        /// Gets the Inbox.
        /// </summary>
        public MailFolder Inbox { get; }

        /// <summary>
        /// Gets the Sent folder.
        /// </summary>
        public MailFolder Sent { get; }

        /// <summary>
        /// Gets the label returned by the labels query.
        /// </summary>
        public Label Work { get; }

        /// <summary>
        /// Gets the Inbox messages.
        /// </summary>
        public List<MailMessage> Messages { get; }

        /// <summary>
        /// Gets the view model under test.
        /// </summary>
        public MainWindowViewModel ViewModel { get; }

        /// <summary>
        /// Initializes the view model and waits until it is idle.
        /// </summary>
        /// <returns>A task that completes when initialization is done.</returns>
        public async Task StartAsync()
        {
            await ViewModel.InitializeAsync();
            await ViewModel.WhenIdleAsync();
        }
    }

    /// <summary>
    /// A sync raised on a background thread refreshes exactly once, through the UI marshaller.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AccountSynced_RaisedOnBackgroundThread_RefreshesOnceThroughMarshaller()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var before = scenario.Dispatcher.Sent<GetAccountsQuery>().Count;

        await Task.Run(() => scenario.SyncEvents.Raise(scenario.Account.Id), TestContext.Current.CancellationToken);
        await Task.WhenAll(scenario.UiThread.Invocations);

        scenario.UiThread.Invocations.Should().HaveCount(1);
        (scenario.Dispatcher.Sent<GetAccountsQuery>().Count - before).Should().Be(1);
    }

    /// <summary>
    /// A burst of syncs during a running refresh produces exactly one follow-up refresh.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AccountSynced_BurstWhileRefreshing_ProducesOneFollowUpRefresh()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var before = scenario.Dispatcher.Sent<GetAccountsQuery>().Count;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        scenario.Dispatcher.OnAsync<GetAccountsQuery>(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await gate.Task;
            }

            return new List<MailAccount> { scenario.Account };
        });

        scenario.SyncEvents.Raise(scenario.Account.Id);
        for (var i = 0; i < 5; i++)
        {
            scenario.SyncEvents.Raise(scenario.Account.Id);
        }

        gate.SetResult();
        await Task.WhenAll(scenario.UiThread.Invocations);

        scenario.UiThread.Invocations.Should().HaveCount(1);
        (scenario.Dispatcher.Sent<GetAccountsQuery>().Count - before).Should().Be(2);
    }

    /// <summary>
    /// After dispose a sync notification no longer refreshes and the subscription is gone.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AccountSynced_AfterDispose_DoesNothing()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var before = scenario.Dispatcher.Sent<GetAccountsQuery>().Count;

        scenario.ViewModel.Dispose();
        scenario.SyncEvents.Raise(scenario.Account.Id);

        scenario.SyncEvents.HasSubscribers.Should().BeFalse();
        scenario.UiThread.Invocations.Should().BeEmpty();
        scenario.Dispatcher.Sent<GetAccountsQuery>().Count.Should().Be(before);
    }

    /// <summary>
    /// Verifies the add-account dialog opens when no account exists.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_NoAccounts_ShowsAddAccountDialog()
    {
        var scenario = new Scenario(hasAccount: false);

        await scenario.StartAsync();

        scenario.Dialogs.AddAccountCalls.Should().Be(1);
        scenario.ViewModel.Accounts.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies folders are sorted with the Inbox first and its messages are shown newest first.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WithAccount_SelectsInboxAndLoadsMessages()
    {
        var scenario = new Scenario();

        await scenario.StartAsync();

        var viewModel = scenario.ViewModel;
        viewModel.Accounts.Should().ContainSingle();
        viewModel.Accounts[0].Folders.Select(f => f.Name).Should().Equal("收件箱", "已发送");
        viewModel.SelectedNode.Should().BeOfType<FolderNodeViewModel>().Which.Folder.Id.Should().Be(scenario.Inbox.Id);
        viewModel.Messages.Select(m => m.Subject).Should().Equal("Hello 2", "Hello 1");
        scenario.Dialogs.AddAccountCalls.Should().Be(0);
    }

    /// <summary>
    /// Verifies opening an unread message marks it read locally and persists the state.
    /// </summary>
    [Fact]
    public async Task SelectedMessage_Unread_MarksReadAndPersists()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];

        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();

        item.IsRead.Should().BeTrue();
        scenario.Dispatcher.Sent<SetMessageReadCommand>().Should().ContainSingle()
            .Which.Should().Be(new SetMessageReadCommand(item.Id, true));
    }

    /// <summary>
    /// Verifies a failed persistence rolls the read state back and reports the problem.
    /// </summary>
    [Fact]
    public async Task SelectedMessage_PersistFails_RollsBackAndReports()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<SetMessageReadCommand>(_ => throw new IOException("disk full"));
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];

        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();

        item.IsRead.Should().BeFalse();
        scenario.ViewModel.StatusText.Should().Contain("disk full");
    }

    /// <summary>
    /// Verifies already-read messages do not trigger a write.
    /// </summary>
    [Fact]
    public async Task SelectedMessage_AlreadyRead_SendsNothing()
    {
        var scenario = new Scenario();
        scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", isRead: true);
        await scenario.StartAsync();

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<SetMessageReadCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies search is scoped to the selected account and replaces the list.
    /// </summary>
    [Fact]
    public async Task SearchCommand_WithText_SearchesSelectedAccount()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.ViewModel.SearchText = " Hello 2 ";

        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);

        var query = scenario.Dispatcher.Sent<SearchMessagesQuery>().Should().ContainSingle().Subject;
        query.Text.Should().Be("Hello 2");
        query.AccountId.Should().Be(scenario.Account.Id);
        scenario.ViewModel.Messages.Select(m => m.Subject).Should().Equal("Hello 2");
        scenario.ViewModel.StatusText.Should().Contain("1");
    }

    /// <summary>
    /// Verifies a blank search restores the folder listing.
    /// </summary>
    [Fact]
    public async Task SearchCommand_Blank_RestoresFolderMessages()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.ViewModel.SearchText = "Hello 2";
        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);

        scenario.ViewModel.SearchText = "  ";
        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);

        scenario.ViewModel.Messages.Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies the sync command syncs the selected account and reports the result.
    /// </summary>
    [Fact]
    public async Task SyncCommand_Succeeds_SyncsSelectedAccountAndReports()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        await scenario.ViewModel.SyncCommand.ExecuteAsync(null);

        scenario.Dispatcher.Sent<SyncAccountCommand>().Should().ContainSingle().Which.AccountId.Should().Be(scenario.Account.Id);
        scenario.ViewModel.StatusText.Should().Contain("1 封新邮件");
        scenario.ViewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failing account is reported without throwing and busy state is cleared.
    /// </summary>
    [Fact]
    public async Task SyncCommand_Fails_ReportsErrorWithoutThrowing()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<SyncAccountCommand>(_ => throw new IOException("offline"));
        await scenario.StartAsync();

        await scenario.ViewModel.SyncCommand.ExecuteAsync(null);

        scenario.ViewModel.StatusText.Should().Contain("offline");
        scenario.ViewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// Verifies folder-level sync failures are surfaced.
    /// </summary>
    [Fact]
    public async Task SyncCommand_FolderFailures_AreReported()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<SyncAccountCommand>(_ => new SyncResult(1, 0, ["Broken"]));
        await scenario.StartAsync();

        await scenario.ViewModel.SyncCommand.ExecuteAsync(null);

        scenario.ViewModel.StatusText.Should().Contain("1 个文件夹同步失败");
    }

    /// <summary>
    /// Verifies a newly added account is loaded and synchronized.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_AccountAddedInDialog_ReloadsAndSyncsIt()
    {
        var scenario = new Scenario(hasAccount: false);
        scenario.Dialogs.AddAccountResult = true;
        scenario.Dialogs.OnAddAccount = () => scenario.HasAccount = true;

        await scenario.StartAsync();

        scenario.ViewModel.Accounts.Should().ContainSingle();
        scenario.Dispatcher.Sent<SyncAccountCommand>().Should().ContainSingle().Which.AccountId.Should().Be(scenario.Account.Id);
    }

    /// <summary>
    /// Verifies reply opens a compose request addressed to the sender with a quoted body.
    /// </summary>
    [Fact]
    public async Task ReplyCommand_SelectedMessage_OpensReplyCompose()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.ViewModel.ReplyCommand.CanExecute(null).Should().BeFalse();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];

        await scenario.ViewModel.ReplyCommand.ExecuteAsync(null);

        var request = scenario.Dialogs.ComposeRequests.Should().ContainSingle().Subject;
        request.Mode.Should().Be(ComposeMode.Reply);
        request.To.Should().Be("alice@example.com");
        request.Subject.Should().Be("Re: Hello 2");
        request.InReplyTo.Should().Be("<2@example.com>");
        request.Body.Should().Contain("> line one");
    }

    /// <summary>
    /// Verifies forward opens a compose request with a forwarded body and no recipients.
    /// </summary>
    [Fact]
    public async Task ForwardCommand_SelectedMessage_OpensForwardCompose()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];

        await scenario.ViewModel.ForwardCommand.ExecuteAsync(null);

        var request = scenario.Dialogs.ComposeRequests.Should().ContainSingle().Subject;
        request.Mode.Should().Be(ComposeMode.Forward);
        request.To.Should().BeEmpty();
        request.Subject.Should().Be("Fwd: Hello 2");
        request.Body.Should().Contain("转发的邮件").And.Contain("line one");
    }

    /// <summary>
    /// Verifies the compose command defaults to the account of the current selection.
    /// </summary>
    [Fact]
    public async Task ComposeCommand_Executed_OpensNewComposeForSelectedAccount()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        await scenario.ViewModel.ComposeCommand.ExecuteAsync(null);

        var request = scenario.Dialogs.ComposeRequests.Should().ContainSingle().Subject;
        request.Mode.Should().Be(ComposeMode.New);
        request.AccountId.Should().Be(scenario.Account.Id);
    }

    /// <summary>
    /// Verifies toggling the flag updates the item and persists it, and rolls back on failure.
    /// </summary>
    [Fact]
    public async Task ToggleFlagCommand_Toggles_AndRollsBackOnFailure()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];
        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();

        await scenario.ViewModel.ToggleFlagCommand.ExecuteAsync(null);
        item.IsFlagged.Should().BeTrue();
        item.FlagGlyph.Should().Be("★");
        scenario.Dispatcher.Sent<SetMessageFlaggedCommand>().Single().IsFlagged.Should().BeTrue();

        scenario.Dispatcher.On<SetMessageFlaggedCommand>(_ => throw new IOException("locked"));
        await scenario.ViewModel.ToggleFlagCommand.ExecuteAsync(null);
        item.IsFlagged.Should().BeTrue();
        scenario.ViewModel.StatusText.Should().Contain("locked");
    }

    /// <summary>
    /// Verifies the browser command is only available for messages with an HTML body and passes it on.
    /// </summary>
    [Fact]
    public async Task OpenHtmlCommand_MessageWithHtml_ShowsHtmlInBrowser()
    {
        var scenario = new Scenario();
        scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<p>rich</p>");
        await scenario.StartAsync();

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.OpenHtmlCommand.CanExecute(null).Should().BeTrue();
        scenario.ViewModel.OpenHtmlCommand.Execute(null);

        scenario.Dialogs.BrowserHtml.Should().Equal("<p>rich</p>");

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[1];
        scenario.ViewModel.OpenHtmlCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Verifies an attachment is downloaded on demand and written to the chosen path.
    /// </summary>
    [Fact]
    public async Task AttachmentSaveCommand_PathChosen_DownloadsAndWritesFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = new Scenario();
        scenario.Messages[1].AddAttachment("report.bin", "application/octet-stream", 3);
        var target = Path.Combine(Path.GetTempPath(), $"swiftlet-{Guid.NewGuid():N}.bin");
        scenario.Dialogs.SavePath = target;
        scenario.Dispatcher.On<DownloadAttachmentQuery>(_ => new DownloadedAttachment("report.bin", "application/octet-stream", new byte[] { 7, 8, 9 }));
        await scenario.StartAsync();
        var attachment = scenario.ViewModel.Messages[0].Attachments.Should().ContainSingle().Subject;

        try
        {
            await attachment.SaveCommand.ExecuteAsync(null);

            (await File.ReadAllBytesAsync(target, ct)).Should().Equal(7, 8, 9);
            scenario.ViewModel.StatusText.Should().Contain(target);
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>
    /// Verifies cancelling the save picker downloads nothing.
    /// </summary>
    [Fact]
    public async Task AttachmentSaveCommand_PickerCancelled_DownloadsNothing()
    {
        var scenario = new Scenario();
        scenario.Messages[1].AddAttachment("report.bin", "application/octet-stream", 3);
        scenario.Dialogs.SavePath = null;
        await scenario.StartAsync();

        await scenario.ViewModel.Messages[0].Attachments[0].SaveCommand.ExecuteAsync(null);

        scenario.Dispatcher.Sent<DownloadAttachmentQuery>().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a failing download is reported instead of crashing.
    /// </summary>
    [Fact]
    public async Task AttachmentSaveCommand_DownloadFails_ReportsError()
    {
        var scenario = new Scenario();
        scenario.Messages[1].AddAttachment("report.bin", "application/octet-stream", 3);
        scenario.Dialogs.SavePath = Path.Combine(Path.GetTempPath(), $"swiftlet-{Guid.NewGuid():N}.bin");
        scenario.Dispatcher.On<DownloadAttachmentQuery>(_ => throw new InvalidOperationException("gone"));
        await scenario.StartAsync();

        await scenario.ViewModel.Messages[0].Attachments[0].SaveCommand.ExecuteAsync(null);

        scenario.ViewModel.StatusText.Should().Contain("gone");
    }

    /// <summary>
    /// Verifies a refresh picks up folders that appeared after the first load without losing the selection.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_NewFolderAppears_AddsItAndKeepsSelection()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var drafts = TestData.Folder(scenario.Account, "Drafts", FolderRole.Drafts);
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Sent, scenario.Inbox, drafts });

        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Accounts[0].Folders.Select(f => f.Name).Should().Equal("收件箱", "草稿", "已发送");
        scenario.ViewModel.SelectedNode.Should().BeOfType<FolderNodeViewModel>().Which.Folder.Id.Should().Be(scenario.Inbox.Id);
    }

    /// <summary>
    /// Verifies the tree starts with the unified inbox, then accounts, then labels.
    /// </summary>
    [Fact]
    public async Task Initialize_AddsUnifiedInboxNodeFirst()
    {
        var scenario = new Scenario();

        await scenario.StartAsync();

        var roots = scenario.ViewModel.TreeRoots;
        roots.Should().HaveCount(3);
        roots[0].Should().BeOfType<UnifiedInboxNodeViewModel>().Which.Name.Should().Be("统一收件箱");
        roots[1].Should().BeOfType<AccountNodeViewModel>().Which.Account.Id.Should().Be(scenario.Account.Id);
        roots[2].Should().BeOfType<LabelNodeViewModel>().Which.Name.Should().Be("Work");
        scenario.ViewModel.Accounts.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies selecting the unified inbox loads the cross-account listing.
    /// </summary>
    [Fact]
    public async Task SelectUnifiedInbox_LoadsUnifiedInboxQuery()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots[0];
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<GetUnifiedInboxQuery>().Should().ContainSingle().Which.Skip.Should().Be(0);
        scenario.ViewModel.Messages.Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies selecting a label loads the messages carrying it.
    /// </summary>
    [Fact]
    public async Task SelectLabel_LoadsMessagesByLabelQuery()
    {
        var scenario = new Scenario();
        scenario.Messages[0].AddLabel(scenario.Work);
        await scenario.StartAsync();

        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots.OfType<LabelNodeViewModel>().Single();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<GetMessagesByLabelQuery>().Should().ContainSingle().Which.LabelId.Should().Be(scenario.Work.Id);
        scenario.ViewModel.Messages.Should().ContainSingle();
        scenario.ViewModel.Messages[0].Labels.Should().ContainSingle().Which.Name.Should().Be("Work");
    }

    /// <summary>
    /// Verifies searching from the unified inbox is not restricted to one account.
    /// </summary>
    [Fact]
    public async Task Search_FromUnifiedInbox_SearchesAllAccounts()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots[0];
        scenario.ViewModel.SearchText = "Hello";

        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);

        scenario.Dispatcher.Sent<SearchMessagesQuery>().Should().ContainSingle().Which.AccountId.Should().BeNull();
    }

    /// <summary>
    /// Verifies a cancelled confirmation sends nothing and keeps the list.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_Cancelled_SendsNothing()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = false;
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.Dialogs.Confirmations.Should().ContainSingle();
        scenario.Dispatcher.Sent<DeleteMessageCommand>().Should().BeEmpty();
        scenario.ViewModel.Messages.Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies deleting a message that sits in the Trash folder warns about permanent deletion and names the subject.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_MessageInTrashFolder_ConfirmsPermanentDeletionWithSubject()
    {
        var scenario = new Scenario();
        var trash = TestData.Folder(scenario.Account, "Trash", FolderRole.Trash);
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Inbox, trash });
        scenario.Messages.Add(TestData.Message(scenario.Account, trash, 3, "Old news"));
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages.Single(m => m.Subject == "Old news");

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        var confirmation = scenario.Dialogs.Confirmations.Should().ContainSingle().Which;
        confirmation.Message.Should().Contain("永久删除").And.Contain("Old news");
        confirmation.OkText.Should().Be("删除");
    }

    /// <summary>
    /// Verifies an account without a Trash folder gets the permanent-deletion warning.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_AccountWithoutTrash_ConfirmsPermanentDeletionWithSubject()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        var subject = scenario.ViewModel.SelectedMessage.Subject;

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        var message = scenario.Dialogs.Confirmations.Should().ContainSingle().Which.Message;
        message.Should().Contain("永久删除").And.Contain(subject);
    }

    /// <summary>
    /// Verifies a message outside Trash in an account that has a Trash folder is described as moved, not permanent.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_AccountWithTrash_ConfirmsMoveToTrashWithSubject()
    {
        var scenario = new Scenario();
        var trash = TestData.Folder(scenario.Account, "Trash", FolderRole.Trash);
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Inbox, trash });
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        var subject = scenario.ViewModel.SelectedMessage.Subject;

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        var message = scenario.Dialogs.Confirmations.Should().ContainSingle().Which.Message;
        message.Should().Contain("已删除文件夹").And.Contain(subject).And.NotContain("永久");
    }

    /// <summary>
    /// Verifies a confirmed delete sends the command and removes the message from the list.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_Confirmed_SendsCommandAndRemovesFromList()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteMessageCommand>(_ => DeleteOutcome.MovedToTrash);
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];
        scenario.ViewModel.SelectedMessage = item;

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.Dispatcher.Sent<DeleteMessageCommand>().Should().ContainSingle().Which.MessageId.Should().Be(item.Id);
        scenario.ViewModel.Messages.Should().NotContain(item).And.HaveCount(1);
        scenario.ViewModel.SelectedMessage.Should().BeNull();
    }

    /// <summary>
    /// Verifies a server failure keeps the message listed and reports the error.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_ServerFails_KeepsMessageAndShowsError()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteMessageCommand>(_ => throw new IOException("server down"));
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.ViewModel.Messages.Should().HaveCount(2);
        scenario.ViewModel.StatusText.Should().Contain("server down").And.Contain("删除邮件失败");
    }

    /// <summary>
    /// Verifies the status text differs between moved-to-trash and permanent deletion.
    /// </summary>
    /// <param name="outcome">The outcome reported by the server.</param>
    /// <param name="expected">The expected status text.</param>
    [Theory]
    [InlineData(DeleteOutcome.MovedToTrash, "已移至服务器的已删除文件夹")]
    [InlineData(DeleteOutcome.DeletedPermanently, "已从服务器永久删除")]
    public async Task DeleteSelected_Outcome_ShowsMatchingStatusText(DeleteOutcome outcome, string expected)
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteMessageCommand>(_ => outcome);
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.ViewModel.StatusText.Should().Be(expected);
    }

    /// <summary>
    /// Verifies delete is unavailable without a selected message.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_NoSelection_CannotExecute()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        scenario.ViewModel.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        scenario.ViewModel.DeleteSelectedCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// Verifies the settings command opens the settings dialog.
    /// </summary>
    [Fact]
    public async Task OpenSettings_Executed_ShowsSettingsDialog()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        await scenario.ViewModel.OpenSettingsCommand.ExecuteAsync(null);

        scenario.Dialogs.SettingsCalls.Should().Be(1);
    }

    /// <summary>
    /// Verifies the manage-labels command opens the manager and reloads the label nodes afterwards.
    /// </summary>
    [Fact]
    public async Task ManageLabels_ManagerChangesLabels_ReloadsLabelNodes()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.Dialogs.OnLabelManager = () => scenario.Dispatcher.On<GetLabelsQuery>(_ =>
            (IReadOnlyList<Label>)new List<Label> { scenario.Work, Label.Create("Travel", "#1E88E5") });

        await scenario.ViewModel.ManageLabelsCommand.ExecuteAsync(null);

        scenario.Dialogs.LabelManagerCalls.Should().Be(1);
        scenario.ViewModel.TreeRoots.OfType<LabelNodeViewModel>().Select(n => n.Name).Should().Equal("Work", "Travel");
    }

    /// <summary>
    /// Verifies deleting the selected label in the manager leaves the label view and falls back to a folder.
    /// </summary>
    [Fact]
    public async Task ManageLabels_SelectedLabelDeleted_FallsBackToFolder()
    {
        var scenario = new Scenario();
        scenario.Messages[0].AddLabel(scenario.Work);
        await scenario.StartAsync();
        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots.OfType<LabelNodeViewModel>().Single();
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.Messages.Should().ContainSingle();
        scenario.Dialogs.OnLabelManager = () => scenario.Dispatcher.On<GetLabelsQuery>(_ => (IReadOnlyList<Label>)new List<Label>());

        await scenario.ViewModel.ManageLabelsCommand.ExecuteAsync(null);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.TreeRoots.OfType<LabelNodeViewModel>().Should().BeEmpty();
        scenario.ViewModel.SelectedNode.Should().BeOfType<FolderNodeViewModel>();
        scenario.ViewModel.Messages.Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies the label choices list every label and mark the ones the selected message carries.
    /// </summary>
    [Fact]
    public async Task SelectedMessage_WithLabel_ShowsAssignedChoice()
    {
        var scenario = new Scenario();
        scenario.Messages[0].AddLabel(scenario.Work);
        await scenario.StartAsync();

        scenario.ViewModel.MessageLabelChoices.Should().BeEmpty();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages.Single(m => m.Id == scenario.Messages[0].Id);

        var choice = scenario.ViewModel.MessageLabelChoices.Should().ContainSingle().Which;
        choice.Name.Should().Be("Work");
        choice.IsAssigned.Should().BeTrue();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages.Single(m => m.Id == scenario.Messages[1].Id);
        scenario.ViewModel.MessageLabelChoices.Single().IsAssigned.Should().BeFalse();
    }

    /// <summary>
    /// Verifies ticking a label choice sends the set-label command and refreshes the chips.
    /// </summary>
    [Fact]
    public async Task ToggleMessageLabel_Assign_SendsSetMessageLabelCommandAndRefreshesChips()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];
        scenario.ViewModel.SelectedMessage = item;
        var choice = scenario.ViewModel.MessageLabelChoices.Single();
        choice.IsAssigned = true;

        await scenario.ViewModel.ToggleMessageLabelCommand.ExecuteAsync(choice);

        var sent = scenario.Dispatcher.Sent<SetMessageLabelCommand>().Should().ContainSingle().Which;
        sent.MessageId.Should().Be(item.Id);
        sent.LabelId.Should().Be(scenario.Work.Id);
        sent.Assigned.Should().BeTrue();
        item.Labels.Should().ContainSingle().Which.Name.Should().Be("Work");
        item.HasLabels.Should().BeTrue();
    }

    /// <summary>
    /// Verifies unticking a label choice removes the chip.
    /// </summary>
    [Fact]
    public async Task ToggleMessageLabel_Unassign_SendsCommandAndRemovesChip()
    {
        var scenario = new Scenario();
        scenario.Messages[0].AddLabel(scenario.Work);
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages.Single(m => m.Id == scenario.Messages[0].Id);
        scenario.ViewModel.SelectedMessage = item;
        var choice = scenario.ViewModel.MessageLabelChoices.Single();
        choice.IsAssigned = false;

        await scenario.ViewModel.ToggleMessageLabelCommand.ExecuteAsync(choice);

        scenario.Dispatcher.Sent<SetMessageLabelCommand>().Should().ContainSingle().Which.Assigned.Should().BeFalse();
        item.Labels.Should().BeEmpty();
        item.HasLabels.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failing toggle reverts the choice and reports the failure in the status line.
    /// </summary>
    [Fact]
    public async Task ToggleMessageLabel_Fails_RevertsChoiceAndReportsStatus()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<SetMessageLabelCommand>(_ => throw new InvalidOperationException("boom"));
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];
        scenario.ViewModel.SelectedMessage = item;
        var choice = scenario.ViewModel.MessageLabelChoices.Single();
        choice.IsAssigned = true;

        await scenario.ViewModel.ToggleMessageLabelCommand.ExecuteAsync(choice);

        choice.IsAssigned.Should().BeFalse();
        item.Labels.Should().BeEmpty();
        scenario.ViewModel.StatusText.Should().Contain("boom").And.Contain(scenario.Localizer["Error_SetLabel"]);
    }

    /// <summary>
    /// Verifies removing the label of the selected label node drops the message from the list.
    /// </summary>
    [Fact]
    public async Task ToggleMessageLabel_UnassignInLabelView_RemovesMessageFromList()
    {
        var scenario = new Scenario();
        scenario.Messages[0].AddLabel(scenario.Work);
        await scenario.StartAsync();
        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots.OfType<LabelNodeViewModel>().Single();
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages.Single();
        var choice = scenario.ViewModel.MessageLabelChoices.Single();
        choice.IsAssigned = false;

        await scenario.ViewModel.ToggleMessageLabelCommand.ExecuteAsync(choice);

        scenario.ViewModel.Messages.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a confirmed deletion sends the command, removes the account node and resets the list.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_Confirmed_SendsCommandAndRemovesNodeAndResetsList()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteAccountCommand>(_ =>
        {
            scenario.HasAccount = false;
            return Unit.Value;
        });
        await scenario.StartAsync();
        var node = scenario.ViewModel.Accounts.Single();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(node);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<DeleteAccountCommand>().Should().ContainSingle().Which.AccountId.Should().Be(scenario.Account.Id);
        scenario.ViewModel.Accounts.Should().BeEmpty();
        scenario.ViewModel.TreeRoots.OfType<AccountNodeViewModel>().Should().BeEmpty();
        scenario.ViewModel.Messages.Should().BeEmpty();
        scenario.ViewModel.SelectedMessage.Should().BeNull();
        scenario.ViewModel.SelectedNode.Should().BeNull();
        scenario.ViewModel.StatusText.Should().Contain(scenario.Account.EmailAddress);
    }

    /// <summary>
    /// Verifies cancelling the confirmation sends nothing.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_Cancelled_SendsNothing()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = false;
        await scenario.StartAsync();

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.Single());

        scenario.Dialogs.Confirmations.Should().ContainSingle();
        scenario.Dispatcher.Sent<DeleteAccountCommand>().Should().BeEmpty();
        scenario.ViewModel.Accounts.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies the confirmation names the address and says the server mailbox is not touched.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_ConfirmText_NamesAddressAndSaysServerUntouched()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.Single());

        var confirmation = scenario.Dialogs.Confirmations.Should().ContainSingle().Which;
        confirmation.Message.Should().Contain("me@example.com").And.Contain("服务器上的邮箱不受影响");
        confirmation.OkText.Should().Be("移除账号");
    }

    /// <summary>
    /// Verifies a failing deletion keeps the node and reports the error.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_CommandFails_KeepsNodeAndShowsError()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteAccountCommand>(_ => throw new IOException("locked"));
        await scenario.StartAsync();

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.Single());

        scenario.ViewModel.Accounts.Should().ContainSingle();
        scenario.ViewModel.Messages.Should().NotBeEmpty();
        scenario.ViewModel.StatusText.Should().Contain("locked").And.Contain(scenario.Localizer["Error_DeleteAccount"]);
    }

    /// <summary>
    /// Verifies a deletion that fails after the rows are already gone still removes the node and shows the error.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_CommandFailsAfterRowsGone_RemovesNodeAndShowsError()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteAccountCommand>(_ =>
        {
            scenario.HasAccount = false;
            throw new IOException("keychain locked");
        });
        await scenario.StartAsync();

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.Single());
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Accounts.Should().BeEmpty();
        scenario.ViewModel.TreeRoots.OfType<AccountNodeViewModel>().Should().BeEmpty();
        scenario.ViewModel.Messages.Should().BeEmpty();
        scenario.ViewModel.StatusText.Should().Contain("keychain locked").And.Contain(scenario.Localizer["Error_DeleteAccount"]);
    }

    /// <summary>
    /// Verifies deleting another account while a search is active re-runs the search instead of showing the folder list.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_SearchActive_RerunsSearchAndKeepsSearchText()
    {
        var scenario = new Scenario();
        var other = TestData.Account("other@example.com");
        var otherInbox = TestData.Folder(other, "INBOX", FolderRole.Inbox);
        var removed = false;
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<GetAccountsQuery>(_ => removed ? new List<MailAccount> { scenario.Account } : new List<MailAccount> { scenario.Account, other });
        scenario.Dispatcher.On<GetFoldersQuery>(q => q.AccountId == other.Id
            ? new List<MailFolder> { otherInbox }
            : q.AccountId == scenario.Account.Id ? new List<MailFolder> { scenario.Inbox } : []);
        scenario.Dispatcher.On<DeleteAccountCommand>(_ =>
        {
            removed = true;
            return Unit.Value;
        });
        await scenario.StartAsync();
        scenario.ViewModel.SearchText = "Hello 2";
        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.Single(a => a.Account.Id == other.Id));
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.SearchText.Should().Be("Hello 2");
        scenario.Dispatcher.Sent<SearchMessagesQuery>().Should().HaveCount(2);
        scenario.ViewModel.Messages.Select(m => m.Subject).Should().Equal("Hello 2");
    }

    /// <summary>
    /// Verifies a reload that was already running cannot re-insert the node of an account a newer reload removed.
    /// </summary>
    [Fact]
    public async Task Refresh_StaleReloadSeesRemovedAccount_DoesNotReinsertNode()
    {
        var scenario = new Scenario();
        var other = TestData.Account("other@example.com");
        var otherInbox = TestData.Folder(other, "INBOX", FolderRole.Inbox);
        await scenario.StartAsync();
        var accounts = new List<MailAccount> { scenario.Account, other };
        var gate = new TaskCompletionSource();
        var gated = false;
        scenario.Dispatcher.On<GetAccountsQuery>(_ => accounts.ToList());
        scenario.Dispatcher.OnAsync<GetFoldersQuery>(async (q, _) =>
        {
            if (q.AccountId == scenario.Account.Id && !gated)
            {
                // Only the first (stale) reload parks here, after it has read the account list but before it reaches the other account.
                gated = true;
                await gate.Task;
            }

            return q.AccountId == other.Id
                ? new List<MailFolder> { otherInbox }
                : q.AccountId == scenario.Account.Id ? new List<MailFolder> { scenario.Inbox, scenario.Sent } : [];
        });
        var stale = scenario.ViewModel.RefreshAsync();
        accounts.Remove(other);

        await scenario.ViewModel.RefreshAsync();
        gate.SetResult();
        await stale;
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Accounts.Select(a => a.Account.Id).Should().Equal(scenario.Account.Id);
        scenario.ViewModel.TreeRoots.OfType<AccountNodeViewModel>().Should().ContainSingle();
    }

    /// <summary>
    /// Verifies deleting one account leaves a selection in another account alone.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_OtherAccountSelected_KeepsSelection()
    {
        var scenario = new Scenario();
        var other = TestData.Account("other@example.com");
        var otherInbox = TestData.Folder(other, "INBOX", FolderRole.Inbox);
        var removed = false;
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<GetAccountsQuery>(_ => removed ? new List<MailAccount> { other } : new List<MailAccount> { scenario.Account, other });
        scenario.Dispatcher.On<GetFoldersQuery>(q => q.AccountId == other.Id
            ? new List<MailFolder> { otherInbox }
            : q.AccountId == scenario.Account.Id ? new List<MailFolder> { scenario.Inbox } : []);
        scenario.Dispatcher.On<GetMessagesQuery>(_ => new List<MailMessage>());
        scenario.Dispatcher.On<DeleteAccountCommand>(_ =>
        {
            removed = true;
            return Unit.Value;
        });
        await scenario.StartAsync();
        var otherFolder = scenario.ViewModel.Accounts.Single(a => a.Account.Id == other.Id).Folders.Single();
        scenario.ViewModel.SelectedNode = otherFolder;
        await scenario.ViewModel.WhenIdleAsync();

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.Single(a => a.Account.Id == scenario.Account.Id));
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Accounts.Should().ContainSingle().Which.Account.Id.Should().Be(other.Id);
        scenario.ViewModel.SelectedNode.Should().BeSameAs(otherFolder);
    }

    /// <summary>
    /// Verifies deleting the selected account moves the selection to a remaining account's folder.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_SelectedAccountRemoved_SelectsRemainingFolder()
    {
        var scenario = new Scenario();
        var other = TestData.Account("other@example.com");
        var otherInbox = TestData.Folder(other, "INBOX", FolderRole.Inbox);
        var removed = false;
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<GetAccountsQuery>(_ => removed ? new List<MailAccount> { other } : new List<MailAccount> { scenario.Account, other });
        scenario.Dispatcher.On<GetFoldersQuery>(q => q.AccountId == other.Id
            ? new List<MailFolder> { otherInbox }
            : q.AccountId == scenario.Account.Id ? new List<MailFolder> { scenario.Inbox } : []);
        scenario.Dispatcher.On<DeleteAccountCommand>(_ =>
        {
            removed = true;
            return Unit.Value;
        });
        await scenario.StartAsync();

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.First(a => a.Account.Id == scenario.Account.Id));
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.SelectedNode.Should().BeOfType<FolderNodeViewModel>().Which.Account.Id.Should().Be(other.Id);
    }

    /// <summary>
    /// Verifies re-sign-in sends the command, then synchronizes the account and reports success.
    /// </summary>
    [Fact]
    public async Task ReauthenticateAccount_Succeeds_SendsCommandThenSyncsAccount()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<ReauthenticateOAuthAccountCommand>(_ => Unit.Value);
        await scenario.StartAsync();
        var node = scenario.ViewModel.Accounts.Single();

        await scenario.ViewModel.ReauthenticateAccountCommand.ExecuteAsync(node);

        var relevant = scenario.Dispatcher.Requests.Where(r => r is ReauthenticateOAuthAccountCommand or SyncAccountCommand).ToList();
        relevant.Should().Equal(new ReauthenticateOAuthAccountCommand(scenario.Account.Id), new SyncAccountCommand(scenario.Account.Id));
        scenario.ViewModel.IsBusy.Should().BeFalse();
        scenario.ViewModel.StatusText.Should().NotBeEmpty();
    }

    /// <summary>
    /// Verifies a failed re-sign-in, such as an email mismatch, is reported with its message and nothing is synchronized.
    /// </summary>
    [Fact]
    public async Task ReauthenticateAccount_Fails_ShowsError()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<ReauthenticateOAuthAccountCommand>(_ =>
            throw new InvalidOperationException("Signed in as x@y.com but the account is me@example.com."));
        await scenario.StartAsync();

        await scenario.ViewModel.ReauthenticateAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.Single());

        scenario.ViewModel.StatusText.Should().Contain("Signed in as x@y.com but the account is me@example.com.")
            .And.Contain(scenario.Localizer["Error_Reauthenticate"]);
        scenario.Dispatcher.Sent<SyncAccountCommand>().Should().BeEmpty();
        scenario.ViewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a sync failing with the re-authentication exception shows a dedicated hint naming the account.
    /// </summary>
    [Fact]
    public async Task Sync_ReauthenticationRequired_ShowsDedicatedStatusNamingAccount()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<SyncAccountCommand>(_ => throw new OAuthReauthenticationRequiredException(scenario.Account.EmailAddress));
        await scenario.StartAsync();

        await scenario.ViewModel.SyncCommand.ExecuteAsync(null);

        scenario.ViewModel.StatusText.Should().Contain(scenario.Account.EmailAddress).And.Contain("重新登录");
    }

    /// <summary>
    /// Verifies only OAuth accounts offer re-sign-in.
    /// </summary>
    [Fact]
    public void AccountNode_CanReauthenticate_OnlyForOAuthAccounts()
    {
        new AccountNodeViewModel(TestData.Account()).CanReauthenticate.Should().BeFalse();
        new AccountNodeViewModel(TestData.OAuthAccount()).CanReauthenticate.Should().BeTrue();
    }
}
