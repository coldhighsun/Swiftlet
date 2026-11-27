using AwesomeAssertions;
using Mediator;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Queries;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the order and the expanded state of the account nodes in the folder tree.
/// </summary>
public class AccountOrderTests
{
    /// <summary>
    /// Creates a scenario with two accounts, the one of the scenario first.
    /// </summary>
    /// <param name="stored">The accounts the fake database returns; filled with both accounts.</param>
    /// <returns>The scenario and the second account.</returns>
    private static (MainWindowViewModelTests.Scenario Scenario, MailAccount Second) CreateTwoAccounts(out List<MailAccount> stored)
    {
        var scenario = new MainWindowViewModelTests.Scenario();
        var second = TestData.Account("second@example.com");
        var accounts = new List<MailAccount> { scenario.Account, second };
        stored = accounts;
        scenario.Dispatcher.On<GetAccountsQuery>(_ => accounts.ToList());
        scenario.Dispatcher.On<ReorderAccountsCommand>(_ => Unit.Value);
        scenario.Dispatcher.On<SetAccountExpandedCommand>(_ => Unit.Value);
        return (scenario, second);
    }

    /// <summary>
    /// Verifies moving an account reorders both lists of the tree, keeps the selection and sends the new order.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task MoveAccountAsync_ToNewIndex_ReordersTreeAndSendsOrder()
    {
        var (scenario, second) = CreateTwoAccounts(out _);
        await scenario.StartAsync();
        var first = scenario.ViewModel.Accounts[0];
        scenario.ViewModel.SelectedNode = first;

        await scenario.ViewModel.MoveAccountAsync(first, 1);

        scenario.ViewModel.Accounts.Select(n => n.Account.Id).Should().Equal(second.Id, scenario.Account.Id);
        scenario.ViewModel.TreeRoots.OfType<AccountNodeViewModel>().Select(n => n.Account.Id).Should().Equal(second.Id, scenario.Account.Id);
        scenario.ViewModel.TreeRoots[0].Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
        scenario.ViewModel.SelectedNode.Should().BeSameAs(first);
        scenario.Dispatcher.Sent<ReorderAccountsCommand>().Should().ContainSingle()
            .Which.AccountIds.Should().Equal(second.Id, scenario.Account.Id);
    }

    /// <summary>
    /// Verifies a move to the current position changes nothing and sends nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task MoveAccountAsync_SamePosition_SendsNothing()
    {
        var (scenario, _) = CreateTwoAccounts(out _);
        await scenario.StartAsync();

        await scenario.ViewModel.MoveAccountAsync(scenario.ViewModel.Accounts[0], 0);

        scenario.Dispatcher.Sent<ReorderAccountsCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a failed save shows the error and restores the order that is stored.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task MoveAccountAsync_SaveFails_RestoresStoredOrderAndShowsError()
    {
        var (scenario, _) = CreateTwoAccounts(out _);
        scenario.Dispatcher.On<ReorderAccountsCommand>(_ => throw new IOException("locked"));
        await scenario.StartAsync();

        await scenario.ViewModel.MoveAccountAsync(scenario.ViewModel.Accounts[0], 1);

        scenario.ViewModel.Accounts.Select(n => n.Account.Id).First().Should().Be(scenario.Account.Id);
        scenario.ViewModel.TreeRoots.OfType<AccountNodeViewModel>().First().Account.Id.Should().Be(scenario.Account.Id);
        scenario.ViewModel.StatusText.Should().Contain(scenario.Localizer["Error_ReorderAccounts"]);
    }

    /// <summary>
    /// Verifies a reload puts the nodes into the stored order, so a refresh never undoes a reordering.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Reload_StoredOrderChanged_ReordersNodes()
    {
        var (scenario, second) = CreateTwoAccounts(out var stored);
        await scenario.StartAsync();
        stored.Reverse();

        await scenario.ViewModel.RefreshAsync();

        scenario.ViewModel.Accounts.Select(n => n.Account.Id).Should().Equal(second.Id, scenario.Account.Id);
        scenario.ViewModel.TreeRoots.OfType<AccountNodeViewModel>().Select(n => n.Account.Id).Should().Equal(second.Id, scenario.Account.Id);
        scenario.ViewModel.TreeRoots[0].Should().BeSameAs(scenario.ViewModel.UnifiedInbox);
    }

    /// <summary>
    /// Verifies a node starts in the stored expanded state without writing it back.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Load_StoredCollapsedAccount_StartsCollapsedAndSendsNothing()
    {
        var (scenario, second) = CreateTwoAccounts(out _);
        second.SetExpanded(false);

        await scenario.StartAsync();

        scenario.ViewModel.Accounts[0].IsExpanded.Should().BeTrue();
        scenario.ViewModel.Accounts[1].IsExpanded.Should().BeFalse();
        scenario.Dispatcher.Sent<SetAccountExpandedCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a change by the user is stored, and a reload does not overwrite the state shown.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task IsExpanded_ChangedByUser_SendsCommandAndSurvivesReload()
    {
        var (scenario, _) = CreateTwoAccounts(out _);
        await scenario.StartAsync();
        var node = scenario.ViewModel.Accounts[0];

        node.IsExpanded = false;
        await WaitForAsync(() => scenario.Dispatcher.Sent<SetAccountExpandedCommand>().Count == 1);
        await scenario.ViewModel.RefreshAsync();

        var sent = scenario.Dispatcher.Sent<SetAccountExpandedCommand>().Single();
        sent.AccountId.Should().Be(scenario.Account.Id);
        sent.IsExpanded.Should().BeFalse();
        node.IsExpanded.Should().BeFalse();
    }

    /// <summary>
    /// Waits until a condition holds, so a fire-and-forget save can finish.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }
}
