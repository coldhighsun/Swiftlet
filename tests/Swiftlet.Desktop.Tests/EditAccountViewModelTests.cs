using AwesomeAssertions;
using Swiftlet.Application.Accounts;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="EditAccountViewModel"/>.
/// </summary>
public class EditAccountViewModelTests
{
    [Fact]
    public void Load_PasswordAccount_ShowsStoredSettings()
    {
        var viewModel = Create(new FakeDispatcher(), TestData.Account());

        viewModel.Email.Should().Be("me@example.com");
        viewModel.DisplayName.Should().BeEmpty("the name was not typed by the user");
        viewModel.DisplayNamePlaceholder.Should().Be("Me");
        viewModel.SelectedInterval!.Interval.Should().Be(TimeSpan.FromMinutes(5));
        viewModel.SelectedInterval.Text.Should().Be("每 5 分钟");
        viewModel.IsPasswordAccount.Should().BeTrue();
        viewModel.Password.Should().BeEmpty();
        viewModel.ImapHost.Should().Be("imap.example.com");
        viewModel.ImapPort.Should().Be("993");
        viewModel.SmtpPort.Should().Be("465");
        viewModel.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Load_CustomNameAndUnusualInterval_PrefillsNameAndOffersTheInterval()
    {
        var account = TestData.Account();
        account.ChangeDisplayName("Work");
        account.ChangeSyncInterval(TimeSpan.FromMinutes(7));

        var viewModel = Create(new FakeDispatcher(), account);

        viewModel.DisplayName.Should().Be("Work");
        viewModel.DisplayNamePlaceholder.Should().Be("me");
        viewModel.IntervalOptions.Select(o => o.Interval.TotalMinutes).Should().Equal(1, 2, 5, 7, 10, 15, 30, 60);
        viewModel.SelectedInterval!.Interval.Should().Be(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task SaveAsync_PasswordAccount_SendsEverySettingAndClosesWithTheResult()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<UpdateAccountCommand>(_ => new UpdateAccountResult(true));
        var account = TestData.Account();
        var viewModel = Create(dispatcher, account);
        UpdateAccountResult? closedWith = null;
        viewModel.CloseRequested += (_, result) => closedWith = result;
        viewModel.DisplayName = " Work ";
        viewModel.SelectedInterval = viewModel.IntervalOptions.Single(o => o.Interval == TimeSpan.FromMinutes(30));
        viewModel.Password = "new-password";
        viewModel.ImapHost = " imap2.example.com ";
        viewModel.ImapPort = "143";
        viewModel.ImapUseSsl = false;

        await viewModel.SaveCommand.ExecuteAsync(null);

        dispatcher.Sent<UpdateAccountCommand>().Should().ContainSingle().Which.Should().Be(new UpdateAccountCommand(
            account.Id,
            "Work",
            TimeSpan.FromMinutes(30),
            new ServerEndpoint("imap2.example.com", 143, false),
            new ServerEndpoint("smtp.example.com", 465, true),
            "new-password"));
        closedWith.Should().Be(new UpdateAccountResult(true));
    }

    [Fact]
    public async Task SaveAsync_OAuthAccount_SendsNoConnectionSettings()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<UpdateAccountCommand>(_ => new UpdateAccountResult(false));
        var viewModel = Create(dispatcher, TestData.OAuthAccount());

        await viewModel.SaveCommand.ExecuteAsync(null);

        var command = dispatcher.Sent<UpdateAccountCommand>().Should().ContainSingle().Which;
        command.Imap.Should().BeNull();
        command.Smtp.Should().BeNull();
        command.Password.Should().BeNull();
    }

    [Theory]
    [InlineData("abc", "465")]
    [InlineData("0", "465")]
    [InlineData("993", "65536")]
    public async Task SaveAsync_InvalidPort_ShowsErrorWithoutSending(string imapPort, string smtpPort)
    {
        var dispatcher = new FakeDispatcher();
        var viewModel = Create(dispatcher, TestData.Account());
        viewModel.ImapPort = imapPort;
        viewModel.SmtpPort = smtpPort;

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Contain("端口");
        dispatcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_CommandFails_ShowsReasonAndStaysOpen()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<UpdateAccountCommand>(_ => throw new InvalidOperationException("authentication failed"));
        var viewModel = Create(dispatcher, TestData.Account());
        var closed = false;
        viewModel.CloseRequested += (_, _) => closed = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Contain("authentication failed");
        viewModel.IsBusy.Should().BeFalse();
        closed.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_WhileVerifying_CancelsTheCommandAndClosesWithoutResult()
    {
        var dispatcher = new FakeDispatcher();
        var cancelled = false;
        dispatcher.OnAsync<UpdateAccountCommand>(async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                cancelled = token.IsCancellationRequested;
            }

            return null;
        });
        var viewModel = Create(dispatcher, TestData.Account());
        var results = new List<UpdateAccountResult?>();
        viewModel.CloseRequested += (_, result) => results.Add(result);
        var save = viewModel.SaveCommand.ExecuteAsync(null);
        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();

        viewModel.CancelCommand.Execute(null);
        await save;

        cancelled.Should().BeTrue();
        results.Should().ContainSingle().Which.Should().BeNull();
        viewModel.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public void SaveCommand_BlankServerHost_IsDisabled()
    {
        var viewModel = Create(new FakeDispatcher(), TestData.Account());

        viewModel.SmtpHost = " ";

        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Creates a Chinese view model loaded with an account.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="account">The account.</param>
    /// <returns>The view model.</returns>
    private static EditAccountViewModel Create(FakeDispatcher dispatcher, MailAccount account)
    {
        var viewModel = new EditAccountViewModel(dispatcher, TestLocalizer.Chinese());
        viewModel.Load(account);
        return viewModel;
    }
}
