using AwesomeAssertions;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Desktop.Localization;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Queries;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
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

            Dispatcher.On<GetAccountsQuery>(_ => HasAccount ? new List<MailAccount> { Account } : []);
            Dispatcher.On<GetFoldersQuery>(q => q.AccountId == Account.Id ? new List<MailFolder> { Sent, Inbox } : []);
            Dispatcher.On<GetMessagesQuery>(q => q.FolderId == Inbox.Id ? Messages.OrderByDescending(m => m.SentAt).ToList() : []);
            Dispatcher.On<SearchMessagesQuery>(q => Messages.Where(m => m.Subject.Contains(q.Text, StringComparison.Ordinal)).ToList());
            Dispatcher.On<SetMessageReadCommand>(_ => Unit.Value);
            Dispatcher.On<SetMessageFlaggedCommand>(_ => Unit.Value);
            Dispatcher.On<SyncAccountCommand>(_ => new SyncResult(2, 1, []));
            Localizer = TestLocalizer.Chinese();
            LanguageStore = new FakeLanguageStore();
            ViewModel = new MainWindowViewModel(
                Dispatcher,
                Dialogs,
                Localizer,
                new LanguageSelectorViewModel(Localizer, LanguageStore, NullLogger<LanguageSelectorViewModel>.Instance));
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
}
