using AwesomeAssertions;
using Mediator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Desktop.Localization;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Labels;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Queries;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Main;
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
            Dispatcher.On<GetUnifiedInboxQuery>(_ => HasAccount ? Messages.OrderByDescending(m => m.SentAt).ToList() : []);
            Dispatcher.On<GetMessagesByLabelQuery>(q => Messages.Where(m => m.Labels.Any(l => l.Id == q.LabelId)).ToList());
            Dispatcher.On<GetAccountsQuery>(_ => HasAccount ? new List<MailAccount> { Account } : []);
            Dispatcher.On<GetFoldersQuery>(q => q.AccountId == Account.Id ? new List<MailFolder> { Sent, Inbox } : []);
            Dispatcher.On<GetUnreadCountsQuery>(_ => (IReadOnlyDictionary<Guid, int>)Messages
                .Where(m => !m.IsRead)
                .GroupBy(m => m.FolderId)
                .ToDictionary(g => g.Key, g => g.Count()));
            Dispatcher.On<GetMessagesQuery>(q => q.FolderId == Inbox.Id ? Messages.OrderByDescending(m => m.SentAt).ToList() : []);
            Dispatcher.On<SearchMessagesQuery>(q => Messages.Where(m => m.Subject.Contains(q.Text, StringComparison.Ordinal)).ToList());
            Dispatcher.On<SetMessageReadCommand>(_ => Unit.Value);
            Dispatcher.On<SetMessageFlaggedCommand>(_ => Unit.Value);
            Dispatcher.On<SetMessageLabelCommand>(_ => Unit.Value);
            Dispatcher.On<SyncAccountCommand>(_ => new SyncResult(2, 1, []));
            Dispatcher.On<SyncFolderCommand>(_ => new FolderSyncResult(0, false));
            Localizer = TestLocalizer.Chinese();
            ViewModel = new MainWindowViewModel(
                Dispatcher,
                Dialogs,
                Localizer,
                SyncEvents,
                UiThread,
                RemoteImageRules,
                Logger,
                Clock);
        }

        /// <summary>
        /// Gets the clock of the view model.
        /// </summary>
        public FakeClock Clock { get; } = new();

        /// <summary>
        /// Gets the localizer, fixed to Simplified Chinese until a test switches it.
        /// </summary>
        public LocalizationManager Localizer { get; }

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
        /// Gets the logger of the view model.
        /// </summary>
        public ListLogger<MainWindowViewModel> Logger { get; } = new();

        /// <summary>
        /// Gets the saved remote image rules.
        /// </summary>
        public FakeRemoteImageRules RemoteImageRules { get; } = new();

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

        /// <summary>
        /// Initializes the view model, opens the Inbox folder and waits until it is idle.
        /// </summary>
        /// <returns>A task that completes when the Inbox is shown.</returns>
        public async Task StartInInboxAsync()
        {
            await StartAsync();
            ViewModel.SelectedNode = ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == Inbox.Id);
            await ViewModel.WhenIdleAsync();
        }
    }

    /// <summary>
    /// Only the first label node starts the label section of the tree.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Reload_SeveralLabels_OnlyTheFirstStartsTheSection()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<GetLabelsQuery>(_ => (IReadOnlyList<Label>)new List<Label>
        {
            Label.Create("Travel", "#1E88E5"),
            scenario.Work,
        });

        await scenario.StartAsync();

        scenario.ViewModel.TreeRoots.OfType<LabelNodeViewModel>().Select(n => n.StartsSection).Should().Equal(true, false);
    }

    /// <summary>
    /// Without accounts the list invites adding one.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ListEmptyState_NoAccounts_IsNoAccounts()
    {
        var scenario = new Scenario(hasAccount: false);

        await scenario.StartAsync();

        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.NoAccounts);
    }

    /// <summary>
    /// An opened folder without messages says so, and a folder with messages does not.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ListEmptyState_FolderWithoutMessages_IsEmptyView()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.None);

        scenario.ViewModel.SelectedNode = scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == scenario.Sent.Id);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.EmptyView);
    }

    /// <summary>
    /// A search without hits says so, and clearing it removes the notice.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ListEmptyState_SearchWithoutHits_IsNoSearchResults()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.ViewModel.SearchText = "nothing matches this";

        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);

        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.NoSearchResults);
        scenario.ViewModel.SearchText = string.Empty;
        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);
        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.None);
    }

    /// <summary>
    /// While a view is still loading the list does not claim to be empty.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ListEmptyState_WhileLoading_IsNone()
    {
        var scenario = new Scenario();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Dispatcher.OnAsync<GetUnifiedInboxQuery>(async (_, _) =>
        {
            await gate.Task;
            return new List<MailMessage>();
        });

        await scenario.ViewModel.InitializeAsync();

        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.None);
        gate.SetResult();
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.EmptyView);
    }

    /// <summary>
    /// A failed load does not claim the view is empty.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ListEmptyState_LoadFails_IsNone()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<GetUnifiedInboxQuery>(_ => throw new IOException("db locked"));

        await scenario.StartAsync();

        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.None);
    }

    /// <summary>
    /// Deleting the last message leaves the empty notice.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ListEmptyState_LastMessageDeleted_IsEmptyView()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteMessagesCommand>(c =>
        {
            scenario.Messages.Clear();
            return c.Messages.Count;
        });
        await scenario.StartAsync();
        foreach (var item in scenario.ViewModel.Messages)
        {
            scenario.ViewModel.SelectedMessages.Add(item);
        }

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.EmptyView);
    }

    /// <summary>
    /// A reload shows the unread count on each folder and their inbox sum on the unified inbox.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Reload_WithUnread_SetsFolderAndUnifiedCounts()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<GetUnreadCountsQuery>(_ => (IReadOnlyDictionary<Guid, int>)new Dictionary<Guid, int>
        {
            [scenario.Inbox.Id] = 12,
            [scenario.Sent.Id] = 3,
        });

        await scenario.StartAsync();

        var folders = scenario.ViewModel.Accounts[0].Folders;
        folders.Single(f => f.Folder.Id == scenario.Inbox.Id).UnreadCount.Should().Be(12);
        folders.Single(f => f.Folder.Id == scenario.Sent.Id).UnreadCount.Should().Be(3);
        scenario.ViewModel.UnifiedInbox.UnreadCount.Should().Be(12);
    }

    /// <summary>
    /// Opening an unread message lowers the counts without waiting for a reload.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectUnreadMessage_MarkedRead_CountsDropAtOnce()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<SetMessageReadCommand>(c =>
        {
            scenario.Messages.Single(m => m.Id == c.MessageId).SetRead(c.IsRead);
            return Unit.Value;
        });
        await scenario.StartAsync();
        scenario.ViewModel.UnifiedInbox.UnreadCount.Should().Be(2);

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.UnifiedInbox.UnreadCount.Should().Be(1);
        scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == scenario.Inbox.Id).UnreadCount.Should().Be(1);
    }

    /// <summary>
    /// Deleting unread messages lowers the counts.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task DeleteSelected_Unread_CountsDrop()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteMessagesCommand>(c =>
        {
            scenario.Messages.Clear();
            return c.Messages.Count;
        });
        await scenario.StartAsync();
        foreach (var item in scenario.ViewModel.Messages)
        {
            scenario.ViewModel.SelectedMessages.Add(item);
        }

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.UnifiedInbox.UnreadCount.Should().Be(0);
    }

    /// <summary>
    /// The first folder is listed while the unread counts are still being read.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Reload_SlowCountQuery_DoesNotDelayTheMessageList()
    {
        var scenario = new Scenario();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Dispatcher.OnAsync<GetUnreadCountsQuery>(async (_, _) =>
        {
            await gate.Task;
            return new Dictionary<Guid, int>();
        });

        await scenario.ViewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        scenario.ViewModel.Messages.Should().HaveCount(2);
        gate.SetResult();
        await scenario.ViewModel.WhenIdleAsync();
    }

    /// <summary>
    /// A count that arrives after a newer one was shown is discarded.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Reload_OlderCountArrivesLast_KeepsTheNewerCount()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        scenario.Dispatcher.OnAsync<GetUnreadCountsQuery>(async (_, _) =>
        {
            var first = Interlocked.Increment(ref calls) == 1;
            if (first)
            {
                entered.SetResult();
                await gate.Task;
            }

            return new Dictionary<Guid, int> { [scenario.Inbox.Id] = first ? 5 : 1 };
        });

        await scenario.ViewModel.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await scenario.ViewModel.RefreshAsync();
        await WaitForAsync(() => scenario.ViewModel.UnifiedInbox.UnreadCount == 1);
        gate.SetResult();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.UnifiedInbox.UnreadCount.Should().Be(1);
    }

    /// <summary>
    /// A failing count query only logs a warning; the tree and the status line are unaffected.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Reload_CountQueryFails_TreeStillLoads()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<GetUnreadCountsQuery>(_ => throw new IOException("db locked"));

        await scenario.StartAsync();

        scenario.ViewModel.Accounts.Single().Folders.Should().HaveCount(2);
        scenario.ViewModel.Messages.Should().HaveCount(2);
        scenario.ViewModel.StatusText.Should().BeEmpty();
        scenario.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The badge is empty at zero and capped for very large numbers.
    /// </summary>
    /// <param name="count">The number of unread messages.</param>
    /// <param name="expected">The badge text.</param>
    [Theory]
    [InlineData(0, "")]
    [InlineData(12, "12")]
    [InlineData(999, "999")]
    [InlineData(1000, "999+")]
    public void UnreadText_Values_AreShortened(int count, string expected)
    {
        var node = new UnifiedInboxNodeViewModel(TestLocalizer.English()) { UnreadCount = count };

        node.UnreadText.Should().Be(expected);
        node.HasUnread.Should().Be(count > 0);
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
    /// Verifies nothing is selected without accounts, and adding the first one opens the unified inbox.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AddAccount_FirstAccount_SelectsUnifiedInbox()
    {
        var scenario = new Scenario(hasAccount: false);
        await scenario.StartAsync();
        scenario.ViewModel.SelectedNode.Should().BeNull();
        scenario.Dialogs.AddAccountResult = true;
        scenario.Dialogs.OnAddAccount = () => scenario.HasAccount = true;

        await scenario.ViewModel.AddAccountCommand.ExecuteAsync(null);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.SelectedNode.Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
        scenario.ViewModel.Messages.Select(m => m.Subject).Should().Equal("Hello 2", "Hello 1");
    }

    /// <summary>
    /// Verifies folders are sorted with the Inbox first and the unified inbox is opened with the newest message first.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_WithAccount_SelectsUnifiedInboxAndLoadsMessages()
    {
        var scenario = new Scenario();

        await scenario.StartAsync();

        var viewModel = scenario.ViewModel;
        viewModel.Accounts.Should().ContainSingle();
        viewModel.Accounts[0].Folders.Select(f => f.Name).Should().Equal("收件箱", "已发送");
        viewModel.SelectedNode.Should().BeSameAs(viewModel.UnifiedInbox);
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
            .Which.Should().Be(new SetMessageReadCommand(item.Message.AccountId, item.Id, true));
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
        await scenario.StartInInboxAsync();
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
    /// Verifies a manual sync runs the accounts side by side, names those still running and keeps one failure apart from the others.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncCommand_SeveralAccounts_SyncsThemInParallel()
    {
        // Arrange
        var scenario = new Scenario();
        var second = TestData.Account("second@example.com");
        scenario.Dispatcher.On<GetAccountsQuery>(_ => new List<MailAccount> { scenario.Account, second });
        var first = new TaskCompletionSource<object?>();
        var other = new TaskCompletionSource<object?>();
        scenario.Dispatcher.OnAsync<SyncAccountCommand>((c, _) => c.AccountId == scenario.Account.Id ? first.Task : other.Task);
        await scenario.StartAsync();
        scenario.ViewModel.SelectedNode = null;

        // Act
        var sync = scenario.ViewModel.SyncCommand.ExecuteAsync(null);
        var startedTogether = scenario.Dispatcher.Sent<SyncAccountCommand>().Count;
        other.SetResult(new SyncResult(1, 3, []));
        await WaitForAsync(() => !scenario.ViewModel.StatusText.Contains(second.EmailAddress, StringComparison.Ordinal));
        var statusWhileFirstRuns = scenario.ViewModel.StatusText;
        first.SetException(new IOException("offline"));
        await sync;

        // Assert
        startedTogether.Should().Be(2);
        statusWhileFirstRuns.Should().Contain(scenario.Account.EmailAddress);
        scenario.ViewModel.StatusText.Should().Contain("3 封新邮件").And.Contain("offline");
        scenario.ViewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// Verifies another account can be added while the first sync of the previous one is still running.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AddAccountCommand_FirstSyncRunning_CanAddAnotherAccount()
    {
        // Arrange
        var scenario = new Scenario();
        var accounts = new List<MailAccount> { scenario.Account };
        scenario.Dispatcher.On<GetAccountsQuery>(_ => accounts.ToList());
        var sync = new TaskCompletionSource<object?>();
        scenario.Dispatcher.OnAsync<SyncAccountCommand>((_, _) => sync.Task);
        await scenario.StartAsync();
        scenario.Dialogs.AddAccountResult = true;
        scenario.Dialogs.OnAddAccount = () => accounts.Add(TestData.Account($"added{accounts.Count}@example.com"));

        // Act
        var first = scenario.ViewModel.AddAccountCommand.ExecuteAsync(null);
        var canAddAgain = scenario.ViewModel.AddAccountCommand.CanExecute(null);
        var second = scenario.ViewModel.AddAccountCommand.ExecuteAsync(null);
        sync.SetResult(new SyncResult(1, 0, []));
        await Task.WhenAll(first, second);

        // Assert
        canAddAgain.Should().BeTrue();
        scenario.Dialogs.AddAccountCalls.Should().Be(2);
        scenario.Dispatcher.Sent<SyncAccountCommand>().Select(c => c.AccountId)
            .Should().Equal(accounts[1].Id, accounts[2].Id);
    }

    /// <summary>
    /// Verifies the progress bar stays until the last of several overlapping first syncs has finished.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AddAccountCommand_OverlappingSyncs_StaysBusyUntilAllFinished()
    {
        // Arrange
        var scenario = new Scenario();
        var accounts = new List<MailAccount> { scenario.Account };
        scenario.Dispatcher.On<GetAccountsQuery>(_ => accounts.ToList());
        var firstSync = new TaskCompletionSource<object?>();
        var secondSync = new TaskCompletionSource<object?>();
        scenario.Dispatcher.OnAsync<SyncAccountCommand>((c, _) => c.AccountId == accounts[1].Id ? firstSync.Task : secondSync.Task);
        await scenario.StartAsync();
        scenario.Dialogs.AddAccountResult = true;
        scenario.Dialogs.OnAddAccount = () => accounts.Add(TestData.Account($"added{accounts.Count}@example.com"));
        var first = scenario.ViewModel.AddAccountCommand.ExecuteAsync(null);
        var second = scenario.ViewModel.AddAccountCommand.ExecuteAsync(null);

        // Act
        firstSync.SetResult(new SyncResult(1, 0, []));
        await first;
        var busyWhileSecondRuns = scenario.ViewModel.IsBusy;
        secondSync.SetResult(new SyncResult(1, 0, []));
        await second;

        // Assert
        busyWhileSecondRuns.Should().BeTrue();
        scenario.ViewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// Waits until a condition that another thread makes true holds.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
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
    /// Verifies the reading pane gets an HTML document only for messages that have an HTML body.
    /// </summary>
    [Fact]
    public async Task HtmlDocument_MessageWithAndWithoutHtml_IsBuiltOnlyWhenPresent()
    {
        var scenario = new Scenario();
        scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<p>rich</p>");
        await scenario.StartAsync();

        scenario.ViewModel.Messages[0].HtmlDocument.Should().Contain("<p>rich</p>").And.Contain("Content-Security-Policy");
        scenario.ViewModel.Messages[1].HtmlDocument.Should().BeNull();
    }

    /// <summary>
    /// Verifies selecting a message with embedded images downloads them once and shows them as data URIs.
    /// </summary>
    [Fact]
    public async Task SelectedMessage_HtmlWithCid_LoadsInlineImagesOnce()
    {
        var scenario = new Scenario();
        scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<img src=\"cid:Logo\">");
        scenario.Dispatcher.On<GetInlineImagesQuery>(_ => new Dictionary<string, InlineImage> { ["logo"] = new("image/png", new byte[] { 1, 2, 3 }) });
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];

        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[1];
        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();

        var queries = scenario.Dispatcher.Sent<GetInlineImagesQuery>();
        queries.Should().ContainSingle().Which.Should().Be(new GetInlineImagesQuery(scenario.Account.Id, item.Id));
        item.InlineImages.Should().ContainKey("logo");
        item.HtmlDocument.Should().Contain("data:image/png;base64,AQID").And.NotContain("cid:Logo");
    }

    /// <summary>
    /// Verifies messages without embedded-image references never contact the server for images.
    /// </summary>
    [Fact]
    public async Task SelectedMessage_NoCidReferences_SendsNoInlineImageQuery()
    {
        var scenario = new Scenario();
        scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<p>plain html</p>");
        await scenario.StartAsync();

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[1];
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<GetInlineImagesQuery>().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a failed image download is reported in the status bar, keeps the message readable and is retried on the next selection.
    /// </summary>
    [Fact]
    public async Task SelectedMessage_InlineImageQueryFails_ReportsErrorAndRetriesLater()
    {
        var scenario = new Scenario();
        scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<img src=\"cid:logo\">");
        var attempts = 0;
        scenario.Dispatcher.On<GetInlineImagesQuery>(_ =>
        {
            attempts++;
            return attempts == 1
                ? throw new IOException("offline")
                : new Dictionary<string, InlineImage> { ["logo"] = new("image/gif", new byte[] { 7 }) };
        });
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];

        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.StatusText.Should().Contain(scenario.Localizer["Error_InlineImages"]).And.Contain("offline");
        item.InlineImages.Should().BeEmpty();
        item.HtmlDocument.Should().Contain("cid:logo");

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[1];
        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();

        attempts.Should().Be(2);
        item.HtmlDocument.Should().Contain("data:image/gif;base64,Bw==");
    }

    /// <summary>
    /// Verifies remote images are reported as blocked until the user allows them for the message.
    /// </summary>
    [Fact]
    public async Task LoadRemoteImagesCommand_BlockedRemoteImages_AllowsThemForThatMessageOnly()
    {
        var scenario = new Scenario();
        scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: "<img src=\"https://x.example/a.png\">");
        await scenario.StartAsync();
        var remote = scenario.ViewModel.Messages[0];
        var plain = scenario.ViewModel.Messages[1];

        remote.HasBlockedRemoteImages.Should().BeTrue();
        remote.HtmlDocument.Should().NotContain("img-src data: https: http:");
        plain.HasBlockedRemoteImages.Should().BeFalse();

        var raised = new List<string?>();
        remote.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        remote.LoadRemoteImagesCommand.Execute(null);

        remote.AllowRemoteImages.Should().BeTrue();
        remote.HasBlockedRemoteImages.Should().BeFalse();
        remote.HtmlDocument.Should().Contain("img-src data: https: http:");
        raised.Should().Contain(["HtmlDocument", "HasBlockedRemoteImages"]);
        plain.AllowRemoteImages.Should().BeFalse();
    }

    /// <summary>
    /// Creates a scenario whose two messages come from the given senders and both load remote images.
    /// </summary>
    /// <param name="newestSender">The sender of the newest message (listed first).</param>
    /// <param name="olderSender">The sender of the older message.</param>
    /// <returns>The started scenario.</returns>
    private static async Task<Scenario> StartRemoteImageScenarioAsync(string newestSender, string olderSender)
    {
        const string Html = "<img src=\"https://x.example/a.png\">";
        var scenario = new Scenario();
        scenario.Messages[0] = TestData.Message(scenario.Account, scenario.Inbox, 1, "Hello 1", html: Html, fromAddress: olderSender);
        scenario.Messages[1] = TestData.Message(scenario.Account, scenario.Inbox, 2, "Hello 2", html: Html, fromAddress: newestSender);
        await scenario.StartAsync();
        return scenario;
    }

    /// <summary>
    /// Verifies a message from a sender with a saved address rule shows its images at once, without the banner.
    /// </summary>
    /// <param name="rule">The saved rule.</param>
    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("example.com")]
    public async Task SelectedMessage_SenderMatchesSavedRule_LoadsImagesWithoutBanner(string rule)
    {
        var scenario = await StartRemoteImageScenarioAsync("alice@example.com", "bob@other.org");
        await scenario.RemoteImageRules.AddAsync(rule, TestContext.Current.CancellationToken);

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        await scenario.ViewModel.WhenIdleAsync();

        var selected = scenario.ViewModel.SelectedMessage!;
        selected.AllowRemoteImages.Should().BeTrue();
        selected.HasBlockedRemoteImages.Should().BeFalse();
        selected.HtmlDocument.Should().Contain("img-src data: https: http:");
    }

    /// <summary>
    /// Verifies a sender without a rule keeps the images blocked.
    /// </summary>
    [Fact]
    public async Task SelectedMessage_NoMatchingRule_StaysBlocked()
    {
        var scenario = await StartRemoteImageScenarioAsync("alice@example.com", "bob@other.org");
        await scenario.RemoteImageRules.AddAsync("other.org", TestContext.Current.CancellationToken);

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.SelectedMessage!.HasBlockedRemoteImages.Should().BeTrue();
        scenario.ViewModel.SelectedMessage.AllowRemoteImages.Should().BeFalse();
    }

    /// <summary>
    /// Verifies "always allow this sender" saves the address, allows the message now and auto-allows the sender's other mail.
    /// </summary>
    [Fact]
    public async Task AlwaysAllowSenderCommand_Executed_SavesAddressAndAllowsSenderMessages()
    {
        var scenario = await StartRemoteImageScenarioAsync("alice@example.com", "alice@example.com");
        var first = scenario.ViewModel.Messages[0];

        first.CanRememberSender.Should().BeTrue();
        first.AlwaysAllowSenderText.Should().Be("总是加载 alice@example.com 的图片");
        await first.AlwaysAllowSenderCommand.ExecuteAsync(null);

        scenario.RemoteImageRules.AddCalls.Should().Equal("alice@example.com");
        first.AllowRemoteImages.Should().BeTrue();

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[1];
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.SelectedMessage!.AllowRemoteImages.Should().BeTrue();
    }

    /// <summary>
    /// Verifies "always allow this domain" saves the domain, which then covers other senders of that domain.
    /// </summary>
    [Fact]
    public async Task AlwaysAllowDomainCommand_Executed_SavesDomainAndAllowsOtherSendersThere()
    {
        var scenario = await StartRemoteImageScenarioAsync("alice@example.com", "carol@mail.example.com");
        var first = scenario.ViewModel.Messages[0];

        first.AlwaysAllowDomainText.Should().Be("总是加载 example.com 域发件人的图片");
        await first.AlwaysAllowDomainCommand.ExecuteAsync(null);

        scenario.RemoteImageRules.AddCalls.Should().Equal("example.com");
        first.AllowRemoteImages.Should().BeTrue();

        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[1];
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.SelectedMessage!.AllowRemoteImages.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a failed rule save still shows the images of the message and reports the failure.
    /// </summary>
    [Fact]
    public async Task AlwaysAllowSenderCommand_SaveFails_StillAllowsMessageAndReportsError()
    {
        var scenario = await StartRemoteImageScenarioAsync("alice@example.com", "bob@other.org");
        scenario.RemoteImageRules.AddFailure = new IOException("disk full");
        var first = scenario.ViewModel.Messages[0];

        await first.AlwaysAllowSenderCommand.ExecuteAsync(null);

        first.AllowRemoteImages.Should().BeTrue();
        scenario.ViewModel.StatusText.Should().Contain("保存图片规则失败").And.Contain("disk full");
        scenario.RemoteImageRules.Rules.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a sender address that cannot be turned into a rule offers no "always allow" choice.
    /// </summary>
    [Fact]
    public async Task CanRememberSender_UnknownSender_IsFalseAndSavesNothing()
    {
        var scenario = await StartRemoteImageScenarioAsync("unknown@invalid", "bob@other.org");
        var first = scenario.ViewModel.Messages[0];

        first.CanRememberSender.Should().BeFalse();
        first.SenderRule.Should().BeNull();
        first.DomainRule.Should().BeNull();
        await first.AlwaysAllowSenderCommand.ExecuteAsync(null);

        scenario.RemoteImageRules.AddCalls.Should().BeEmpty();
        first.AllowRemoteImages.Should().BeTrue();
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
        await scenario.StartInInboxAsync();
        var drafts = TestData.Folder(scenario.Account, "Drafts", FolderRole.Drafts);
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Sent, scenario.Inbox, drafts });

        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Accounts[0].Folders.Select(f => f.Name).Should().Equal("收件箱", "草稿", "已发送");
        scenario.ViewModel.SelectedNode.Should().BeOfType<FolderNodeViewModel>().Which.Folder.Id.Should().Be(scenario.Inbox.Id);
    }

    /// <summary>
    /// Verifies a refresh keeps the selected item object (and never clears the selection), so the open page is not reloaded.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RefreshAsync_SelectedMessageStillListed_KeepsSameItemAndNeverClearsSelection()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];
        scenario.ViewModel.SelectedMessage = item;
        await scenario.ViewModel.WhenIdleAsync();
        var readCommands = scenario.Dispatcher.Sent<SetMessageReadCommand>().Count;
        var selections = new List<MessageItemViewModel?>();
        scenario.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedMessage))
            {
                selections.Add(scenario.ViewModel.SelectedMessage);
            }
        };

        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.SelectedMessage.Should().BeSameAs(item);
        scenario.ViewModel.Messages.Should().Contain(item);
        selections.Should().BeEmpty();
        scenario.Dispatcher.Sent<SetMessageReadCommand>().Count.Should().Be(readCommands);
    }

    /// <summary>
    /// Verifies a refresh keeps per-message state and applies changed read state, flags and labels from the fresh data.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RefreshAsync_FreshCopyChanged_UpdatesKeptItem()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages.Single(m => m.Id == scenario.Messages[0].Id);
        item.AllowRemoteImages = true;
        var fresh = TestData.Message(scenario.Account, scenario.Inbox, 1, "Hello 1");
        typeof(MailMessage).GetProperty(nameof(MailMessage.Id))!.SetValue(fresh, scenario.Messages[0].Id);
        fresh.AddLabel(scenario.Work);
        fresh.SetFlagged(true);
        scenario.Messages[0] = fresh;

        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Messages.Should().Contain(item);
        item.AllowRemoteImages.Should().BeTrue();
        item.IsFlagged.Should().BeTrue();
        item.Labels.Should().ContainSingle().Which.Name.Should().Be("Work");
    }

    /// <summary>
    /// Verifies a refresh adds new messages in order, drops vanished ones and clears the selection only when its message is gone.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RefreshAsync_ListChanged_ReordersAndClearsSelectionOnlyWhenGone()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var kept = scenario.ViewModel.Messages.Single(m => m.Subject == "Hello 2");
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages.Single(m => m.Subject == "Hello 1");
        var newest = TestData.Message(scenario.Account, scenario.Inbox, 3, "Hello 3");
        scenario.Messages.RemoveAt(0);
        scenario.Messages.Add(newest);

        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Messages.Select(m => m.Subject).Should().Equal(
            scenario.Messages.OrderByDescending(m => m.SentAt).Select(m => m.Subject));
        scenario.ViewModel.Messages.Should().Contain(kept);
        scenario.ViewModel.SelectedMessage.Should().BeNull();
    }

    /// <summary>
    /// Verifies a failure that is only shown in the status line is also written to the log as an error.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RefreshAsync_QueryFails_LogsErrorAndShowsStatus()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        scenario.Dispatcher.On<GetFoldersQuery>(_ => throw new InvalidOperationException("db is gone"));

        await scenario.ViewModel.RefreshAsync();

        scenario.ViewModel.StatusText.Should().Contain("db is gone");
        var entry = scenario.Logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error).Which;
        entry.Exception.Should().BeOfType<InvalidOperationException>();
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
        scenario.Dispatcher.Sent<DeleteMessagesCommand>().Should().BeEmpty();
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
    /// Verifies a confirmed delete queues the message, removes it at once and selects the next one.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_Confirmed_SendsCommandRemovesAndSelectsNext()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteMessagesCommand>(c =>
        {
            scenario.Messages.RemoveAll(m => c.Messages.Any(k => k.MessageId == m.Id));
            return c.Messages.Count;
        });
        await scenario.StartAsync();
        var item = scenario.ViewModel.Messages[0];
        var next = scenario.ViewModel.Messages[1];
        scenario.ViewModel.SelectedMessage = item;

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        var sent = scenario.Dispatcher.Sent<DeleteMessagesCommand>().Should().ContainSingle().Which;
        sent.Messages.Should().Equal(new MessageKey(scenario.Account.Id, item.Id));
        scenario.ViewModel.Messages.Should().Equal(next);
        scenario.ViewModel.SelectedMessage.Should().BeSameAs(next);
        scenario.ViewModel.StatusText.Should().Be("正在后台删除 1 封邮件");
    }

    /// <summary>
    /// Verifies several selected messages are confirmed once, with their number, and queued together.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_MultipleMessages_ConfirmsOnceAndQueuesAll()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        var trash = TestData.Folder(scenario.Account, "Trash", FolderRole.Trash);
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Inbox, trash });
        scenario.Dispatcher.On<DeleteMessagesCommand>(c =>
        {
            scenario.Messages.Clear();
            return c.Messages.Count;
        });
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];
        scenario.ViewModel.SelectedMessages.Clear();
        foreach (var item in scenario.ViewModel.Messages)
        {
            scenario.ViewModel.SelectedMessages.Add(item);
        }

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.Dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().StartWith("2 封邮件").And.Contain("已删除文件夹");
        scenario.Dispatcher.Sent<DeleteMessagesCommand>().Should().ContainSingle().Which.Messages.Should().HaveCount(2);
        scenario.ViewModel.Messages.Should().BeEmpty();
        scenario.ViewModel.SelectedMessage.Should().BeNull();
        scenario.ViewModel.SelectedMessages.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a selection mixing Trash and other folders says how many are lost for good.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_MixedSelection_ConfirmsBothCounts()
    {
        var scenario = new Scenario();
        var trash = TestData.Folder(scenario.Account, "Trash", FolderRole.Trash);
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Inbox, trash });
        scenario.Messages.Add(TestData.Message(scenario.Account, trash, 3, "Old news"));
        await scenario.StartAsync();
        foreach (var item in scenario.ViewModel.Messages)
        {
            scenario.ViewModel.SelectedMessages.Add(item);
        }

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.Dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().StartWith("2 封邮件").And.Contain("另有 1 封将永久删除");
    }

    /// <summary>
    /// Verifies a further delete can start while an earlier one has not finished.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_EarlierDeleteStillRunning_CanDeleteTheNext()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Dispatcher.OnAsync<DeleteMessagesCommand>((_, _) => release.Task);
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];

        var first = scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
        scenario.ViewModel.DeleteSelectedCommand.CanExecute(null).Should().BeTrue();
        var second = scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.Dispatcher.Sent<DeleteMessagesCommand>().Should().HaveCount(2);
        scenario.Messages.Clear();
        release.SetResult(1);
        await Task.WhenAll(first, second);
        scenario.ViewModel.Messages.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies that when the deletion cannot be queued the message comes back and the error is shown.
    /// </summary>
    [Fact]
    public async Task DeleteSelected_QueueingFails_RestoresMessageAndShowsError()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteMessagesCommand>(_ => throw new IOException("disk full"));
        await scenario.StartAsync();
        scenario.ViewModel.SelectedMessage = scenario.ViewModel.Messages[0];

        await scenario.ViewModel.DeleteSelectedCommand.ExecuteAsync(null);

        scenario.ViewModel.Messages.Should().HaveCount(2);
        scenario.ViewModel.StatusText.Should().Contain("disk full").And.Contain("删除邮件失败");
    }

    /// <summary>
    /// Verifies emptying a folder confirms with the folder and the kind of deletion, then queues it.
    /// </summary>
    /// <param name="hasTrash">Whether the account has a Trash folder.</param>
    /// <param name="expected">The text the confirmation must contain.</param>
    [Theory]
    [InlineData(true, "已删除文件夹")]
    [InlineData(false, "永久删除")]
    public async Task EmptyFolder_Confirmed_QueuesAndClearsList(bool hasTrash, string expected)
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        if (hasTrash)
        {
            var trash = TestData.Folder(scenario.Account, "Trash", FolderRole.Trash);
            scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Inbox, trash });
        }

        scenario.Dispatcher.On<EmptyFolderCommand>(_ =>
        {
            scenario.Messages.Clear();
            return true;
        });
        await scenario.StartAsync();
        var node = scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == scenario.Inbox.Id);

        await scenario.ViewModel.EmptyFolderCommand.ExecuteAsync(node);

        scenario.Dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().Contain(expected).And.Contain("尚未下载");
        scenario.Dispatcher.Sent<EmptyFolderCommand>().Should().ContainSingle().Which.FolderId.Should().Be(scenario.Inbox.Id);
        scenario.ViewModel.Messages.Should().BeEmpty();
        scenario.ViewModel.StatusText.Should().StartWith("正在后台删除");
    }

    /// <summary>
    /// Verifies a cancelled confirmation leaves the folder alone.
    /// </summary>
    [Fact]
    public async Task EmptyFolder_Cancelled_SendsNothing()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        await scenario.ViewModel.EmptyFolderCommand.ExecuteAsync(scenario.ViewModel.Accounts[0].Folders[0]);

        scenario.Dialogs.Confirmations.Should().ContainSingle();
        scenario.Dispatcher.Sent<EmptyFolderCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies All Mail is never emptied.
    /// </summary>
    [Fact]
    public async Task EmptyFolder_AllMail_DoesNothing()
    {
        var scenario = new Scenario();
        scenario.Dialogs.ConfirmResult = true;
        var allMail = TestData.Folder(scenario.Account, "All", FolderRole.AllMail);
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Inbox, allMail });
        await scenario.StartAsync();
        var node = scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == allMail.Id);

        await scenario.ViewModel.EmptyFolderCommand.ExecuteAsync(node);

        node.CanEmpty.Should().BeFalse();
        scenario.Dialogs.Confirmations.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the status line follows the background deletion, and the list is reloaded when a run ends.
    /// </summary>
    /// <param name="finished">Whether the run is over.</param>
    /// <param name="error">The reported error.</param>
    /// <param name="willRetry">Whether the deletion is retried.</param>
    /// <param name="expected">The expected start of the status text.</param>
    /// <param name="reloads">Whether the list is reloaded.</param>
    [Theory]
    [InlineData(false, null, false, "正在从服务器删除：已完成 3 封，剩余 4 封", false)]
    [InlineData(true, null, false, "已在服务器上删除 3 封邮件", true)]
    [InlineData(false, "offline", true, "服务器删除失败，稍后自动重试", false)]
    [InlineData(true, "read only", false, "服务器拒绝删除", true)]
    public async Task DeletionProgressed_Report_UpdatesStatusAndReloads(
        bool finished,
        string? error,
        bool willRetry,
        string expected,
        bool reloads)
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var loads = scenario.Dispatcher.Sent<GetUnifiedInboxQuery>().Count;

        scenario.SyncEvents.RaiseDeletion(new DeletionProgressEventArgs(scenario.Account.Id, 3, 4, finished, error, willRetry));
        await Task.WhenAll(scenario.UiThread.Invocations);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.StatusText.Should().StartWith(expected);
        (scenario.Dispatcher.Sent<GetUnifiedInboxQuery>().Count > loads).Should().Be(reloads);
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
    /// Verifies deleting the selected label in the manager leaves the label view and falls back to the unified inbox.
    /// </summary>
    [Fact]
    public async Task ManageLabels_SelectedLabelDeleted_FallsBackToUnifiedInbox()
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
        scenario.ViewModel.SelectedNode.Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
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
        scenario.ViewModel.SelectedNode.Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
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
    /// Verifies deleting the account that owns the selected folder moves the selection to the unified inbox.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_SelectedAccountRemoved_SelectsUnifiedInbox()
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
        await scenario.StartInInboxAsync();

        await scenario.ViewModel.DeleteAccountCommand.ExecuteAsync(scenario.ViewModel.Accounts.First(a => a.Account.Id == scenario.Account.Id));
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.SelectedNode.Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
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

        var relevant = scenario.Dispatcher.Sent<object>().Where(r => r is ReauthenticateOAuthAccountCommand or SyncAccountCommand).ToList();
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

    /// <summary>
    /// Background history progress is shown in the status line, and the final report replaces it.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillProgressed_RaisedOnBackgroundThread_ShowsCountInStatusLine()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        await Task.Run(() => scenario.SyncEvents.RaiseProgress(scenario.Account.Id, 250, finished: false), TestContext.Current.CancellationToken);
        await Task.WhenAll(scenario.UiThread.Invocations);
        var during = scenario.ViewModel.StatusText;
        await Task.Run(() => scenario.SyncEvents.RaiseProgress(scenario.Account.Id, 900, finished: true), TestContext.Current.CancellationToken);
        await Task.WhenAll(scenario.UiThread.Invocations);

        during.Should().Contain("250");
        scenario.ViewModel.StatusText.Should().Contain("900").And.NotBe(during);
    }

    /// <summary>
    /// A progress report that names a folder shows the account, the folder, the count so far and how many older messages are left.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillProgressed_WithFolder_ShowsAccountFolderAndRemaining()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        await Task.Run(() => scenario.SyncEvents.RaiseProgress(scenario.Account.Id, 37, finished: false, "[Gmail]/Sent", 1234), TestContext.Current.CancellationToken);
        await Task.WhenAll(scenario.UiThread.Invocations);

        scenario.ViewModel.StatusText.Should()
            .Contain("37")
            .And.Contain(scenario.Account.EmailAddress)
            .And.Contain("[Gmail]/Sent")
            .And.Contain("1234");
    }

    /// <summary>
    /// After dispose a progress notification no longer touches the status line.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillProgressed_AfterDispose_DoesNothing()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var before = scenario.ViewModel.StatusText;

        scenario.ViewModel.Dispose();
        scenario.SyncEvents.RaiseProgress(scenario.Account.Id, 5, finished: false);

        scenario.ViewModel.StatusText.Should().Be(before);
        scenario.SyncEvents.HasSubscribers.Should().BeFalse();
    }

    /// <summary>
    /// Creates a scenario whose Inbox holds many messages and whose folder and search queries honour skip and take.
    /// </summary>
    /// <param name="count">The number of Inbox messages.</param>
    /// <returns>The scenario, not yet started.</returns>
    private static Scenario PagedScenario(int count)
    {
        var scenario = new Scenario();
        scenario.Messages.Clear();
        for (var i = 1; i <= count; i++)
        {
            scenario.Messages.Add(TestData.Message(scenario.Account, scenario.Inbox, (uint)i, $"Paged {i}"));
        }

        scenario.Dispatcher.On<GetMessagesQuery>(q => q.FolderId == scenario.Inbox.Id
            ? scenario.Messages.OrderByDescending(m => m.SentAt).Skip(q.Skip).Take(q.Take).ToList()
            : []);
        scenario.Dispatcher.On<GetUnifiedInboxQuery>(q => scenario.Messages.OrderByDescending(m => m.SentAt).Skip(q.Skip).Take(q.Take).ToList());
        scenario.Dispatcher.On<SearchMessagesQuery>(q => scenario.Messages
            .Where(m => m.Subject.Contains(q.Text, StringComparison.Ordinal))
            .OrderByDescending(m => m.SentAt).Skip(q.Skip).Take(q.Take).ToList());
        return scenario;
    }

    /// <summary>
    /// Verifies scrolling past a full page appends the next pages until everything is listed, then stops asking.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task LoadMoreAsync_FullPages_AppendsUntilEverythingIsListed()
    {
        var scenario = PagedScenario(120);
        await scenario.StartInInboxAsync();
        scenario.ViewModel.Messages.Should().HaveCount(50);

        await scenario.ViewModel.LoadMoreAsync();
        scenario.ViewModel.Messages.Should().HaveCount(100);
        await scenario.ViewModel.LoadMoreAsync();
        scenario.ViewModel.Messages.Should().HaveCount(120);
        var queries = scenario.Dispatcher.Sent<GetMessagesQuery>().Count;
        await scenario.ViewModel.LoadMoreAsync();

        scenario.Dispatcher.Sent<GetMessagesQuery>().Should().HaveCount(queries);
        scenario.Dispatcher.Sent<GetMessagesQuery>().Select(q => q.Skip).Should().Equal(0, 50, 100);
        scenario.ViewModel.Messages.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        scenario.ViewModel.Messages[^1].Subject.Should().Be("Paged 1");
    }

    /// <summary>
    /// Verifies a first page shorter than the page size never triggers another request.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task LoadMoreAsync_ShortFirstPage_SendsNoQuery()
    {
        var scenario = PagedScenario(5);
        await scenario.StartInInboxAsync();
        var queries = scenario.Dispatcher.Sent<GetMessagesQuery>().Count;

        await scenario.ViewModel.LoadMoreAsync();

        scenario.Dispatcher.Sent<GetMessagesQuery>().Should().HaveCount(queries);
        scenario.ViewModel.Messages.Should().HaveCount(5);
    }

    /// <summary>
    /// Verifies a refresh re-fetches every page loaded so far instead of cutting the list back to the first page.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RefreshAsync_AfterLoadMore_KeepsLoadedPages()
    {
        var scenario = PagedScenario(120);
        await scenario.StartInInboxAsync();
        await scenario.ViewModel.LoadMoreAsync();

        await scenario.ViewModel.RefreshAsync();

        scenario.Dispatcher.Sent<GetMessagesQuery>().Last().Take.Should().Be(100);
        scenario.ViewModel.Messages.Should().HaveCount(100);
        await scenario.ViewModel.LoadMoreAsync();
        scenario.ViewModel.Messages.Should().HaveCount(120);
    }

    /// <summary>
    /// Verifies the next page of search results is requested with the search text and a skip.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task LoadMoreAsync_ShowingSearchResults_PagesTheSearch()
    {
        var scenario = PagedScenario(120);
        await scenario.StartInInboxAsync();
        scenario.ViewModel.SearchText = "Paged";
        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);

        await scenario.ViewModel.LoadMoreAsync();

        var last = scenario.Dispatcher.Sent<SearchMessagesQuery>().Last();
        last.Text.Should().Be("Paged");
        last.Skip.Should().Be(50);
        scenario.ViewModel.Messages.Should().HaveCount(100);
    }

    /// <summary>
    /// Verifies selecting another node starts again from the first page.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_ChangedAfterLoadMore_ReloadsFirstPageOnly()
    {
        var scenario = PagedScenario(120);
        await scenario.StartInInboxAsync();
        await scenario.ViewModel.LoadMoreAsync();

        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots[0];
        await scenario.ViewModel.WhenIdleAsync();

        var query = scenario.Dispatcher.Sent<GetUnifiedInboxQuery>().Last();
        query.Skip.Should().Be(0);
        query.Take.Should().Be(50);
        scenario.ViewModel.Messages.Should().HaveCount(50);
    }

    /// <summary>
    /// Verifies a failed page load keeps the listed messages, reports the error and can be retried.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task LoadMoreAsync_QueryFails_KeepsListAndAllowsRetry()
    {
        var scenario = PagedScenario(120);
        await scenario.StartInInboxAsync();
        var fail = true;
        scenario.Dispatcher.On<GetMessagesQuery>(q => fail
            ? throw new InvalidOperationException("db locked")
            : scenario.Messages.OrderByDescending(m => m.SentAt).Skip(q.Skip).Take(q.Take).ToList());

        await scenario.ViewModel.LoadMoreAsync();

        scenario.ViewModel.Messages.Should().HaveCount(50);
        scenario.ViewModel.StatusText.Should().Contain("db locked");
        fail = false;
        await scenario.ViewModel.LoadMoreAsync();
        scenario.ViewModel.Messages.Should().HaveCount(100);
    }

    /// <summary>
    /// Verifies repeated scroll events while a page is loading send only one request.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task LoadMoreAsync_WhileLoading_SendsOnlyOneQuery()
    {
        var scenario = PagedScenario(120);
        await scenario.StartInInboxAsync();
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Dispatcher.OnAsync<GetMessagesQuery>(async (q, _) =>
        {
            await gate.Task;
            return scenario.Messages.OrderByDescending(m => m.SentAt).Skip(q.Skip).Take(q.Take).ToList();
        });

        var first = scenario.ViewModel.LoadMoreAsync();
        var second = scenario.ViewModel.LoadMoreAsync();
        gate.SetResult(null);
        await Task.WhenAll(first, second);

        scenario.Dispatcher.Sent<GetMessagesQuery>().Count(q => q.Skip == 50).Should().Be(1);
        scenario.ViewModel.Messages.Should().HaveCount(100);
    }

    /// <summary>
    /// Verifies opening a folder checks it against the server at once.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_Folder_SynchronizesThatFolder()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();

        await SelectAsync(scenario, scenario.Sent);

        scenario.Dispatcher.Sent<SyncFolderCommand>().Should().ContainSingle(c => c.FolderId == scenario.Sent.Id)
            .Which.AccountId.Should().Be(scenario.Account.Id);
    }

    /// <summary>
    /// Verifies reopening a folder does not log in to the server again until a while has passed.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_FolderReopened_ChecksAgainOnlyAfterTheInterval()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        await SelectAsync(scenario, scenario.Sent);
        await SelectAsync(scenario, scenario.Inbox);
        scenario.Clock.Advance(TimeSpan.FromSeconds(29));
        await SelectAsync(scenario, scenario.Sent);
        var withinInterval = scenario.Dispatcher.Sent<SyncFolderCommand>().Count(c => c.FolderId == scenario.Sent.Id);

        await SelectAsync(scenario, scenario.Inbox);
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await SelectAsync(scenario, scenario.Sent);

        withinInterval.Should().Be(1);
        scenario.Dispatcher.Sent<SyncFolderCommand>().Count(c => c.FolderId == scenario.Sent.Id).Should().Be(2);
    }

    /// <summary>
    /// Verifies views spanning several folders are left to the regular synchronization.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_UnifiedInboxOrLabel_SynchronizesNoFolder()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var before = scenario.Dispatcher.Sent<SyncFolderCommand>().Count;

        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots[0];
        await scenario.ViewModel.WhenIdleAsync();
        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots.OfType<LabelNodeViewModel>().Single();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<SyncFolderCommand>().Should().HaveCount(before);
    }

    /// <summary>
    /// Verifies an unreachable server leaves the cached messages listed and is only logged.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_FolderCheckFails_KeepsCachedListAndLogsWarning()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        await SelectAsync(scenario, scenario.Sent);
        scenario.Clock.Advance(TimeSpan.FromMinutes(1));
        scenario.Dispatcher.On<SyncFolderCommand>(_ => throw new IOException("offline"));
        var status = scenario.ViewModel.StatusText;

        await SelectAsync(scenario, scenario.Inbox);

        scenario.ViewModel.Messages.Should().HaveCount(2);
        scenario.ViewModel.StatusText.Should().Be(status);
        scenario.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Verifies a sent message synchronizes the Sent folder at once, even right after it was checked.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task MessageSent_FolderCheckedAMomentAgo_SynchronizesSentFolderAgain()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        await SelectAsync(scenario, scenario.Sent);

        await Task.Run(() => scenario.SyncEvents.RaiseSent(scenario.Account.Id), TestContext.Current.CancellationToken);
        await Task.WhenAll(scenario.UiThread.Invocations);
        await scenario.ViewModel.WhenIdleAsync();

        var checks = scenario.Dispatcher.Sent<SyncFolderCommand>().Where(c => c.FolderId == scenario.Sent.Id).ToList();
        checks.Should().HaveCount(2);
        checks.Should().OnlyContain(c => c.AccountId == scenario.Account.Id);
    }

    /// <summary>
    /// Verifies nothing is synchronized when the sending account has no cached Sent folder.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task MessageSent_NoSentFolder_SynchronizesNothing()
    {
        var scenario = new Scenario();
        scenario.Dispatcher.On<GetFoldersQuery>(_ => new List<MailFolder> { scenario.Inbox });
        await scenario.StartAsync();
        var before = scenario.Dispatcher.Sent<SyncFolderCommand>().Count;

        scenario.SyncEvents.RaiseSent(scenario.Account.Id);
        await Task.WhenAll(scenario.UiThread.Invocations);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.Dispatcher.Sent<SyncFolderCommand>().Should().HaveCount(before);
    }

    /// <summary>
    /// After dispose a sent notification no longer synchronizes anything.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task MessageSent_AfterDispose_DoesNothing()
    {
        var scenario = new Scenario();
        await scenario.StartAsync();
        var before = scenario.Dispatcher.Sent<SyncFolderCommand>().Count;

        scenario.ViewModel.Dispose();
        scenario.SyncEvents.RaiseSent(scenario.Account.Id);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.SyncEvents.HasSubscribers.Should().BeFalse();
        scenario.Dispatcher.Sent<SyncFolderCommand>().Should().HaveCount(before);
    }

    /// <summary>
    /// Selects the tree node of a folder and waits until the view model is idle.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="folder">The folder to open.</param>
    /// <returns>A task that completes when the list is loaded and the folder is checked.</returns>
    private static async Task SelectAsync(Scenario scenario, MailFolder folder)
    {
        scenario.ViewModel.SelectedNode = scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == folder.Id);
        await scenario.ViewModel.WhenIdleAsync();
    }
}
