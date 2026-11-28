using AwesomeAssertions;
using Swiftlet.Application.Queries;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the account mark of the message rows in views that mix accounts.
/// </summary>
public class AccountTagTests
{
    /// <summary>
    /// Creates a scenario with a second account that has one message in the unified inbox.
    /// </summary>
    /// <param name="stored">The accounts the fake database returns; initially both.</param>
    /// <returns>The scenario and the second account.</returns>
    private static (MainWindowViewModelTests.Scenario Scenario, MailAccount Second) CreateTwoAccounts(out List<MailAccount> stored)
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        var second = TestData.Account("second@example.com");
        var inbox = TestData.Folder(second, "INBOX", FolderRole.Inbox);
        scenario.Messages.Add(TestData.Message(second, inbox, 3, "Hello 3"));
        var accounts = new List<MailAccount> { scenario.Account, second };
        stored = accounts;
        scenario.Dispatcher.On<GetAccountsQuery>(_ => accounts.ToList());
        scenario.Dispatcher.On<GetMessagesQuery>(q => scenario.Messages.Where(m => m.FolderId == q.FolderId).ToList());
        return (scenario, second);
    }

    /// <summary>
    /// Gets the account name marked on the row of each listed message, by subject.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The names; <see langword="null"/> for a row without a mark.</returns>
    private static Dictionary<string, string?> Marks(MainWindowViewModelTests.Scenario scenario)
    {
        return scenario.ViewModel.Messages.ToDictionary(m => m.Subject, m => m.AccountTag?.Name);
    }

    /// <summary>
    /// Verifies every row of the unified inbox names its own account.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task UnifiedInbox_TwoAccounts_MarksEachRowWithItsAccount()
    {
        var (scenario, second) = CreateTwoAccounts(out _);

        await scenario.StartAsync();

        Marks(scenario).Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Hello 1"] = "me@example.com",
            ["Hello 2"] = "me@example.com",
            ["Hello 3"] = "second@example.com",
        });
        scenario.ViewModel.Messages.Single(m => m.Subject == "Hello 3").AccountTag!.Address.Should().Be(second.EmailAddress);
    }

    /// <summary>
    /// Verifies a single account needs no mark.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task UnifiedInbox_OneAccount_ShowsNoMark()
    {
        var scenario = new MainWindowViewModelTests.Scenario();

        await scenario.StartAsync();

        scenario.ViewModel.Messages.Should().NotBeEmpty().And.OnlyContain(m => m.AccountTag == null);
    }

    /// <summary>
    /// Verifies the rows of one folder are not marked, and the mark follows the view when items are reused.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_FolderThenUnifiedInbox_MarksOnlyInTheUnifiedInbox()
    {
        var (scenario, _) = CreateTwoAccounts(out _);
        await scenario.StartInInboxAsync();
        scenario.ViewModel.Messages.Should().HaveCount(2).And.OnlyContain(m => m.AccountTag == null);
        var kept = scenario.ViewModel.Messages[0];

        scenario.ViewModel.SelectedNode = scenario.ViewModel.UnifiedInbox;
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Messages.Should().HaveCount(3).And.OnlyContain(m => m.AccountTag != null);
        scenario.ViewModel.Messages.Should().Contain(kept);

        scenario.ViewModel.SelectedNode = scenario.ViewModel.Accounts[0].Folders.Single(f => f.Folder.Id == scenario.Inbox.Id);
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Messages.Should().OnlyContain(m => m.AccountTag == null);
    }

    /// <summary>
    /// Verifies a label view, which spans accounts, marks its rows.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SelectedNode_Label_MarksRows()
    {
        var (scenario, _) = CreateTwoAccounts(out _);
        scenario.Messages[0].AddLabel(scenario.Work);
        await scenario.StartAsync();

        scenario.ViewModel.SelectedNode = scenario.ViewModel.TreeRoots.OfType<LabelNodeViewModel>().Single();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Messages.Should().ContainSingle().Which.AccountTag!.Name.Should().Be("me@example.com");
    }

    /// <summary>
    /// Verifies only a search across all accounts marks its results.
    /// </summary>
    /// <param name="scope">The searched scope.</param>
    /// <param name="marked">Whether the results are marked.</param>
    /// <returns>A task that completes when the test is done.</returns>
    [Theory]
    [InlineData(SearchScope.AllAccounts, true)]
    [InlineData(SearchScope.CurrentAccount, false)]
    [InlineData(SearchScope.CurrentFolder, false)]
    public async Task SearchAsync_Scope_MarksOnlyResultsAcrossAccounts(SearchScope scope, bool marked)
    {
        var (scenario, _) = CreateTwoAccounts(out _);
        await scenario.StartInInboxAsync();
        scenario.ViewModel.Filter.ScopeIndex = (int)scope;
        scenario.ViewModel.SearchText = "Hello";

        await scenario.ViewModel.SearchCommand.ExecuteAsync(null);

        scenario.ViewModel.Messages.Should().NotBeEmpty().And.OnlyContain(m => (m.AccountTag != null) == marked);
    }

    /// <summary>
    /// Verifies rows appended by scrolling are marked like the first page.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task LoadMoreAsync_UnifiedInbox_MarksAppendedRows()
    {
        var (scenario, _) = CreateTwoAccounts(out _);
        for (var uid = 10u; uid < 70; uid++)
        {
            scenario.Messages.Add(TestData.Message(scenario.Account, scenario.Inbox, uid, $"Paged {uid}"));
        }

        scenario.Dispatcher.On<GetUnifiedInboxQuery>(
            q => scenario.Messages.OrderByDescending(m => m.SentAt).Skip(q.Skip).Take(q.Take).ToList());
        await scenario.StartAsync();

        await scenario.ViewModel.LoadMoreAsync();

        scenario.ViewModel.Messages.Should().HaveCount(63).And.OnlyContain(m => m.AccountTag != null);
    }

    /// <summary>
    /// Verifies a reload shows a new account name on the listed rows and keeps unchanged marks.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Refresh_AccountRenamed_UpdatesItsMarksOnly()
    {
        var (scenario, second) = CreateTwoAccounts(out _);
        await scenario.StartAsync();
        var unchanged = scenario.ViewModel.Messages.Single(m => m.Subject == "Hello 1").AccountTag;
        second.ChangeDisplayName("Work mail");

        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        Marks(scenario)["Hello 3"].Should().Be("Work mail");
        scenario.ViewModel.Messages.Single(m => m.Subject == "Hello 1").AccountTag.Should().BeSameAs(unchanged);
    }

    /// <summary>
    /// Verifies the marks disappear when only one account is left.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Refresh_OneAccountLeft_RemovesMarks()
    {
        var (scenario, second) = CreateTwoAccounts(out var stored);
        await scenario.StartAsync();
        stored.Remove(second);
        scenario.Messages.RemoveAll(m => m.AccountId == second.Id);

        await scenario.ViewModel.RefreshAsync();
        await scenario.ViewModel.WhenIdleAsync();

        scenario.ViewModel.Messages.Should().NotBeEmpty().And.OnlyContain(m => m.AccountTag == null);
    }
}
