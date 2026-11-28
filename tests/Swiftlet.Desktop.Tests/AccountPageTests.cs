using AwesomeAssertions;
using CommunityToolkit.Mvvm.Input;
using Mediator;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Queries;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the account page of the main window.
/// </summary>
public class AccountPageTests
{
    /// <summary>
    /// Starts a scenario and selects its account node.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The shown account page.</returns>
    private static async Task<AccountPageViewModel> OpenAsync(MainWindowViewModelTests.Scenario scenario)
    {
        await scenario.StartAsync();
        scenario.ViewModel.SelectedNode = scenario.ViewModel.Accounts[0];
        await scenario.ViewModel.WhenIdleAsync();
        return scenario.ViewModel.AccountPage!;
    }

    /// <summary>
    /// Selecting an account shows its settings, servers and message counts.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_Account_ShowsAccountPage()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario();
        var account = scenario.Account;

        // Act
        var page = await OpenAsync(scenario);

        // Assert
        scenario.ViewModel.IsAccountPageShown.Should().BeTrue();
        scenario.ViewModel.Messages.Should().BeEmpty();
        page.Node.Account.Should().BeSameAs(account);
        page.MessageCountText.Should().Be("2");
        page.InboxUnreadText.Should().Be(scenario.Messages.Count(m => !m.IsRead).ToString(System.Globalization.CultureInfo.InvariantCulture));
        page.FolderCountText.Should().Be("2");
        page.FolderRows.Select(r => (r.Name, r.Total)).Should().Equal(("收件箱", "2"), ("已发送", "0"));
        page.ImapAddress.Should().Be($"{account.Imap.Host}:{account.Imap.Port}");
        page.SmtpAddress.Should().Be($"{account.Smtp.Host}:{account.Smtp.Port}");
        page.SignInMethodText.Should().Be("密码");
        page.SenderName.Should().Be(account.DisplayName);
        page.ArchiveFolderText.Should().Be("自动（未找到归档文件夹）");
        page.TrashRetentionText.Should().Be("从不");
        page.SyncSummary.Should().Be("本次启动后尚未同步");
        page.HasSyncError.Should().BeFalse();
    }

    /// <summary>
    /// Selecting a folder again replaces the page with the message list.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_FolderAfterAccount_HidesAccountPage()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario();
        await OpenAsync(scenario);

        // Act
        scenario.ViewModel.SelectedNode = scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == scenario.Inbox.Id);
        await scenario.ViewModel.WhenIdleAsync();

        // Assert
        scenario.ViewModel.AccountPage.Should().BeNull();
        scenario.ViewModel.IsAccountPageShown.Should().BeFalse();
        scenario.ViewModel.Messages.Should().HaveCount(2);
    }

    /// <summary>
    /// A refresh keeps the page and reads the counts again.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Refresh_AccountSelected_KeepsPageAndUpdatesCounts()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario();
        var page = await OpenAsync(scenario);
        scenario.Dispatcher.On<GetFolderMessageCountsQuery>(
            _ => (IReadOnlyList<FolderMessageCount>)[new FolderMessageCount(scenario.Sent.Id, 7, 0)]);

        // Act
        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        // Assert
        scenario.ViewModel.AccountPage.Should().BeSameAs(page);
        page.MessageCountText.Should().Be("7");
    }

    /// <summary>
    /// A status notification of the shown account updates the page; one of another account does not read anything.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncStatusChanged_ShownAccount_UpdatesStatus()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario();
        var page = await OpenAsync(scenario);
        var succeeded = new DateTimeOffset(2026, 3, 4, 5, 6, 0, TimeSpan.Zero);
        scenario.SyncStatus = new AccountSyncStatus(false, succeeded, ["Other"], succeeded.AddHours(1), "offline", false);
        var queries = scenario.Dispatcher.Sent<GetAccountSyncStatusQuery>().Count;

        // Act
        scenario.SyncEvents.RaiseSyncStatus(Guid.NewGuid());
        var afterOther = scenario.Dispatcher.Sent<GetAccountSyncStatusQuery>().Count;
        scenario.SyncEvents.RaiseSyncStatus(scenario.Account.Id);
        await scenario.ViewModel.WhenIdleAsync();

        // Assert
        afterOther.Should().Be(queries);
        page.SyncSummary.Should().StartWith("同步失败：");
        page.SyncError.Should().Be("offline");
        page.LastSuccessText.Should().StartWith("上次同步成功：");
        page.FailedFoldersText.Should().Contain("Other");
    }

    /// <summary>
    /// An expired sign-in asks to sign in again instead of showing the technical message.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_SignInExpired_AsksToSignInAgain()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario
        {
            SyncStatus = new AccountSyncStatus(false, null, [], DateTimeOffset.UnixEpoch, "expired", true),
        };

        // Act
        var page = await OpenAsync(scenario);

        // Assert
        page.SyncError.Should().Contain("重新登录");
        page.HasLastSuccess.Should().BeFalse();
    }

    /// <summary>
    /// The sync button of the page synchronizes only the shown account.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncCommand_OnAccountPage_SynchronizesTheAccount()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario();
        var page = await OpenAsync(scenario);

        // Act
        await ((IAsyncRelayCommand)page.SyncCommand).ExecuteAsync(null);
        await scenario.ViewModel.WhenIdleAsync();

        // Assert
        scenario.Dispatcher.Sent<SyncAccountCommand>().Should().ContainSingle().Which.AccountId.Should().Be(scenario.Account.Id);
        scenario.ViewModel.AccountPage.Should().BeSameAs(page);
    }

    /// <summary>
    /// Removing the shown account closes its page.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task DeleteAccount_ShownAccount_ClosesPage()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario();
        scenario.Dialogs.ConfirmResult = true;
        scenario.Dispatcher.On<DeleteAccountCommand>(_ =>
        {
            scenario.HasAccount = false;
            return Unit.Value;
        });
        var page = await OpenAsync(scenario);

        // Act
        await ((IAsyncRelayCommand)page.DeleteCommand).ExecuteAsync(page.Node);
        await scenario.ViewModel.WhenIdleAsync();

        // Assert
        scenario.ViewModel.AccountPage.Should().BeNull();
        scenario.ViewModel.SelectedNode.Should().BeNull("no account is left to list");
        scenario.ViewModel.ListEmptyState.Should().Be(EmptyState.NoAccounts);
    }

    /// <summary>
    /// Switching the language rewrites the page.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task LanguageChanged_AccountShown_RewritesTexts()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario();
        var page = await OpenAsync(scenario);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        scenario.Localizer.SetPreference("en");

        // Assert
        changed.Should().Contain(string.Empty);
        page.SyncSummary.Should().Be("Not synced since Swiftlet started");
        page.SignInMethodText.Should().Be("Password");
    }

    /// <summary>
    /// A failing count query is reported and leaves the values unknown.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_CountsFail_ReportsAndShowsUnknown()
    {
        // Arrange
        var scenario = new MainWindowViewModelTests.Scenario();
        scenario.Dispatcher.On<GetFolderMessageCountsQuery>(_ => throw new InvalidOperationException("broken"));

        // Act
        var page = await OpenAsync(scenario);

        // Assert
        page.MessageCountText.Should().Be("—");
        scenario.ViewModel.StatusText.Should().Contain("broken");
    }
}
