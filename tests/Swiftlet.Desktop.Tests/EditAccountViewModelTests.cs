using AwesomeAssertions;
using Swiftlet.Application.Accounts;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

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

    [Fact]
    public async Task SaveAsync_ColorUntouched_ShowsTheCurrentOneAndSendsNone()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<UpdateAccountCommand>(_ => new UpdateAccountResult(false));
        var account = TestData.Account();
        var viewModel = Create(dispatcher, account);

        viewModel.Color.Should().Be(account.EffectiveColor);
        viewModel.ColorOptions.Should().Equal(AccountColors.Palette);
        await viewModel.SaveCommand.ExecuteAsync(null);

        dispatcher.Sent<UpdateAccountCommand>().Single().Color.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ColorChanged_SendsIt()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<UpdateAccountCommand>(_ => new UpdateAccountResult(false));
        var viewModel = Create(dispatcher, TestData.Account());
        viewModel.Color = "#C2185B";

        await viewModel.SaveCommand.ExecuteAsync(null);

        dispatcher.Sent<UpdateAccountCommand>().Single().Color.Should().Be("#C2185B");
    }

    [Fact]
    public void Load_ColorOutsideThePalette_OffersIt()
    {
        var account = TestData.Account();
        account.ChangeColor("#123456");

        var viewModel = Create(new FakeDispatcher(), account);

        viewModel.Color.Should().Be("#123456");
        viewModel.ColorOptions.Should().HaveCount(AccountColors.Palette.Count + 1).And.Contain("#123456");
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

    [Fact]
    public void Load_NoArchiveChosen_SelectsAutomaticAndOffersSuitableFolders()
    {
        var account = TestData.Account();
        MailFolder[] folders =
        [
            TestData.Folder(account, "INBOX", FolderRole.Inbox),
            TestData.Folder(account, "Sent", FolderRole.Sent),
            TestData.Folder(account, "Drafts", FolderRole.Drafts),
            TestData.Folder(account, "Trash", FolderRole.Trash),
            TestData.Folder(account, "Spam", FolderRole.Junk),
            TestData.Folder(account, "Old", FolderRole.Archive),
            TestData.Folder(account, "Done", FolderRole.Other),
        ];

        var viewModel = Create(new FakeDispatcher(), account, folders);

        viewModel.ArchiveFolderOptions.Select(o => o.FullName).Should().Equal(null, "Done", "Old", "Sent");
        viewModel.SelectedArchiveFolder.Should().BeSameAs(viewModel.ArchiveFolderOptions[0]);
        viewModel.SelectedArchiveFolder!.Text.Should().Be("自动（Old）");
    }

    [Fact]
    public void Load_NoArchiveFolderOnServer_SaysSo()
    {
        var viewModel = Create(new FakeDispatcher(), TestData.Account());

        viewModel.ArchiveFolderOptions.Should().ContainSingle().Which.Text.Should().Be("自动（未找到归档文件夹）");
    }

    [Fact]
    public async Task SaveAsync_ChosenFolderMissing_KeepsTheChoice()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<UpdateAccountCommand>(_ => new UpdateAccountResult(false));
        var account = TestData.Account();
        account.ChangeArchiveFolder("Gone");
        var viewModel = Create(dispatcher, account);

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.SelectedArchiveFolder!.Text.Should().Be("Gone（未找到）");
        dispatcher.Sent<UpdateAccountCommand>().Single().ArchiveFolderName.Should().Be("Gone");
    }

    [Theory]
    [InlineData("Done")]
    [InlineData(null)]
    public async Task SaveAsync_ArchiveSelection_IsSent(string? selected)
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<UpdateAccountCommand>(_ => new UpdateAccountResult(false));
        var account = TestData.Account();
        account.ChangeArchiveFolder("Done");
        var viewModel = Create(dispatcher, account, [TestData.Folder(account, "Done", FolderRole.Other)]);
        viewModel.SelectedArchiveFolder = viewModel.ArchiveFolderOptions.Single(o => o.FullName == selected);

        await viewModel.SaveCommand.ExecuteAsync(null);

        dispatcher.Sent<UpdateAccountCommand>().Single().ArchiveFolderName.Should().Be(selected);
    }

    [Fact]
    public void Load_NoTrashRetention_SelectsNeverAndOffersTheUsualPeriods()
    {
        var account = TestData.Account();

        var viewModel = Create(new FakeDispatcher(), account, [TestData.Folder(account, "Trash", FolderRole.Trash)]);

        viewModel.TrashRetentionOptions.Select(o => o.Days).Should().Equal(null, 7, 15, 30, 90);
        viewModel.TrashRetentionOptions.Select(o => o.Text).Should().Equal("从不", "7 天后", "15 天后", "30 天后", "90 天后");
        viewModel.SelectedTrashRetention!.Days.Should().BeNull();
        viewModel.HasTrashFolder.Should().BeTrue();
        viewModel.TrashRetentionHint.Should().Contain("永久删除");
    }

    [Fact]
    public void Load_UnusualRetention_OffersAndSelectsIt()
    {
        var account = TestData.Account();
        account.ChangeTrashRetention(45, DateTimeOffset.UnixEpoch);

        var viewModel = Create(new FakeDispatcher(), account);

        viewModel.TrashRetentionOptions.Select(o => o.Days).Should().Equal(null, 7, 15, 30, 45, 90);
        viewModel.SelectedTrashRetention!.Days.Should().Be(45);
    }

    [Fact]
    public void Load_NoTrashFolder_SaysWhyTheChoiceIsDisabled()
    {
        var viewModel = Create(new FakeDispatcher(), TestData.Account());

        viewModel.HasTrashFolder.Should().BeFalse();
        viewModel.TrashRetentionHint.Should().Contain("没有找到");
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(30, 7)]
    public async Task SaveAsync_CleanupSwitchedOnOrShortened_AsksAndSendsWhenConfirmed(int? before, int after)
    {
        var (dispatcher, dialogs, viewModel) = CreateWithRetention(before);
        dialogs.ConfirmResult = true;
        viewModel.SelectedTrashRetention = viewModel.TrashRetentionOptions.Single(o => o.Days == after);

        await viewModel.SaveCommand.ExecuteAsync(null);

        var confirmation = dialogs.Confirmations.Should().ContainSingle().Which;
        confirmation.Message.Should().Contain("me@example.com").And.Contain(after.ToString(System.Globalization.CultureInfo.InvariantCulture));
        dispatcher.Sent<UpdateAccountCommand>().Single().TrashRetentionDays.Should().Be(after);
    }

    [Fact]
    public async Task SaveAsync_CleanupNotConfirmed_SavesNothingAndStaysOpen()
    {
        var (dispatcher, dialogs, viewModel) = CreateWithRetention(null);
        dialogs.ConfirmResult = false;
        var closed = false;
        viewModel.CloseRequested += (_, _) => closed = true;
        viewModel.SelectedTrashRetention = viewModel.TrashRetentionOptions.Single(o => o.Days == 7);

        await viewModel.SaveCommand.ExecuteAsync(null);

        dispatcher.Requests.Should().BeEmpty();
        closed.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(30, 30)]
    [InlineData(30, 90)]
    [InlineData(30, null)]
    public async Task SaveAsync_NothingDeletedSooner_DoesNotAsk(int? before, int? after)
    {
        var (dispatcher, dialogs, viewModel) = CreateWithRetention(before);
        viewModel.SelectedTrashRetention = viewModel.TrashRetentionOptions.Single(o => o.Days == after);

        await viewModel.SaveCommand.ExecuteAsync(null);

        dialogs.Confirmations.Should().BeEmpty();
        dispatcher.Sent<UpdateAccountCommand>().Single().TrashRetentionDays.Should().Be(after);
    }

    /// <summary>
    /// Creates a view model of an account with a Trash folder and a retention.
    /// </summary>
    /// <param name="days">The stored retention.</param>
    /// <returns>The collaborators and the view model.</returns>
    private static (FakeDispatcher Dispatcher, FakeDialogService Dialogs, EditAccountViewModel ViewModel) CreateWithRetention(int? days)
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<UpdateAccountCommand>(_ => new UpdateAccountResult(false));
        var dialogs = new FakeDialogService();
        var account = TestData.Account();
        account.ChangeTrashRetention(days, DateTimeOffset.UnixEpoch);
        var viewModel = Create(dispatcher, account, [TestData.Folder(account, "Trash", FolderRole.Trash)], dialogs);
        return (dispatcher, dialogs, viewModel);
    }

    /// <summary>
    /// Creates a Chinese view model loaded with an account.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="account">The account.</param>
    /// <param name="folders">The cached folders; none by default.</param>
    /// <returns>The view model.</returns>
    private static EditAccountViewModel Create(
        FakeDispatcher dispatcher,
        MailAccount account,
        IReadOnlyList<MailFolder>? folders = null,
        FakeDialogService? dialogs = null)
    {
        var viewModel = new EditAccountViewModel(dispatcher, dialogs ?? new FakeDialogService(), TestLocalizer.Chinese());
        viewModel.Load(account, folders ?? []);
        return viewModel;
    }
}
