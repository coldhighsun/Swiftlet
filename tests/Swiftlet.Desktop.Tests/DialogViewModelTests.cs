using AwesomeAssertions;
using Mediator;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Sending;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Desktop.ViewModels.Compose;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="AddAccountViewModel"/> and <see cref="ComposeViewModel"/>.
/// </summary>
public class DialogViewModelTests
{
    /// <summary>
    /// Verifies a known provider fills the server settings and hints at app passwords.
    /// </summary>
    [Fact]
    public void Email_KnownProvider_FillsServerSettings()
    {
        var viewModel = new AddAccountViewModel(new FakeDispatcher(), TestLocalizer.Chinese());

        viewModel.Email = "me@gmail.com";

        viewModel.ImapHost.Should().Be("imap.gmail.com");
        viewModel.SmtpHost.Should().Be("smtp.gmail.com");
        viewModel.SmtpPort.Should().Be("465");
        viewModel.ProviderHint.Should().Contain("应用专用密码");
        viewModel.ShowAdvanced.Should().BeFalse();
    }

    /// <summary>
    /// Verifies an unknown provider reveals the server settings, and a later known one hides them again.
    /// </summary>
    [Fact]
    public void Email_UnknownThenKnownProvider_TogglesAdvancedSection()
    {
        var viewModel = new AddAccountViewModel(new FakeDispatcher(), TestLocalizer.Chinese());

        viewModel.Email = "me@corp.example";
        viewModel.ShowAdvanced.Should().BeTrue();
        viewModel.ProviderHint.Should().Contain("未识别");

        viewModel.Email = "me@qq.com";
        viewModel.ShowAdvanced.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the add command needs an email, a password and server hosts.
    /// </summary>
    [Fact]
    public void AddCommand_CanExecute_RequiresEmailPasswordAndServers()
    {
        var viewModel = new AddAccountViewModel(new FakeDispatcher(), TestLocalizer.Chinese());
        viewModel.AddCommand.CanExecute(null).Should().BeFalse();

        viewModel.Email = "me@gmail.com";
        viewModel.AddCommand.CanExecute(null).Should().BeFalse();

        viewModel.Password = "secret";
        viewModel.AddCommand.CanExecute(null).Should().BeTrue();

        viewModel.Email = "me@corp.example";
        viewModel.ImapHost = string.Empty;
        viewModel.AddCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a successful add sends the command with the entered settings and closes the dialog.
    /// </summary>
    [Fact]
    public async Task AddCommand_Succeeds_SendsCommandAndCloses()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<AddAccountCommand>(_ => Guid.NewGuid());
        var viewModel = new AddAccountViewModel(dispatcher, TestLocalizer.Chinese()) { Email = "me@gmail.com", Password = "secret" };
        bool? closed = null;
        viewModel.CloseRequested += (_, added) => closed = added;

        await viewModel.AddCommand.ExecuteAsync(null);

        closed.Should().BeTrue();
        var command = dispatcher.Sent<AddAccountCommand>().Should().ContainSingle().Subject;
        command.DisplayName.Should().Be("me");
        command.Imap!.Host.Should().Be("imap.gmail.com");
        command.Smtp!.Port.Should().Be(465);
        command.Password.Should().Be("secret");
        viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failed login keeps the dialog open and shows the error.
    /// </summary>
    [Fact]
    public async Task AddCommand_LoginFails_ShowsErrorAndStaysOpen()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<AddAccountCommand>(_ => throw new InvalidOperationException("Authentication failed"));
        var viewModel = new AddAccountViewModel(dispatcher, TestLocalizer.Chinese()) { Email = "me@gmail.com", Password = "wrong" };
        var closed = false;
        viewModel.CloseRequested += (_, _) => closed = true;

        await viewModel.AddCommand.ExecuteAsync(null);

        closed.Should().BeFalse();
        viewModel.ErrorMessage.Should().Be("Authentication failed");
        viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// Verifies non-numeric ports are rejected before contacting the server.
    /// </summary>
    [Fact]
    public async Task AddCommand_InvalidPort_ShowsErrorWithoutDispatching()
    {
        var dispatcher = new FakeDispatcher();
        var viewModel = new AddAccountViewModel(dispatcher, TestLocalizer.Chinese()) { Email = "me@gmail.com", Password = "secret", ImapPort = "abc" };

        await viewModel.AddCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Contain("端口");
        viewModel.ShowAdvanced.Should().BeTrue();
        dispatcher.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies cancelling closes the dialog with a negative result.
    /// </summary>
    [Fact]
    public void CancelCommand_Executed_ClosesWithFalse()
    {
        var viewModel = new AddAccountViewModel(new FakeDispatcher(), TestLocalizer.Chinese());
        bool? closed = null;
        viewModel.CloseRequested += (_, added) => closed = added;

        viewModel.CancelCommand.Execute(null);

        closed.Should().BeFalse();
    }

    /// <summary>
    /// Creates a compose view model for a new message.
    /// </summary>
    /// <param name="dispatcher">The dispatcher.</param>
    /// <param name="dialogs">The dialogs.</param>
    /// <returns>The view model.</returns>
    private static ComposeViewModel CreateCompose(FakeDispatcher dispatcher, FakeDialogService? dialogs = null)
    {
        var account = TestData.Account();
        return new ComposeViewModel(
            dispatcher,
            dialogs ?? new FakeDialogService(),
            TestLocalizer.Chinese(),
            [account],
            new ComposeRequest(ComposeMode.New, account.Id, string.Empty, string.Empty, string.Empty, null));
    }

    /// <summary>
    /// Verifies recipient lists accept commas, semicolons (including full-width) and line breaks.
    /// </summary>
    [Fact]
    public void ParseRecipients_MixedSeparators_ReturnsTrimmedEntries()
    {
        var result = ComposeViewModel.ParseRecipients(" a@x.com; b@x.com，c@x.com\nd@x.com ,, ");

        result.Should().Equal("a@x.com", "b@x.com", "c@x.com", "d@x.com");
        ComposeViewModel.ParseRecipients(null).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the request pre-fills the compose window.
    /// </summary>
    [Fact]
    public void Constructor_ReplyRequest_PrefillsFields()
    {
        var account = TestData.Account();
        var request = new ComposeRequest(ComposeMode.Reply, account.Id, "bob@x.com", "Re: Hi", "quoted", "<1@x.com>");

        var viewModel = new ComposeViewModel(new FakeDispatcher(), new FakeDialogService(), TestLocalizer.Chinese(), [account], request);

        viewModel.Title.Should().Be("回复");
        viewModel.SelectedAccount.Should().BeSameAs(account);
        viewModel.To.Should().Be("bob@x.com");
        viewModel.Subject.Should().Be("Re: Hi");
        viewModel.Body.Should().Be("quoted");
    }

    /// <summary>
    /// Verifies sending without recipients fails fast.
    /// </summary>
    [Fact]
    public async Task SendCommand_NoRecipients_ShowsErrorWithoutDispatching()
    {
        var dispatcher = new FakeDispatcher();
        var viewModel = CreateCompose(dispatcher);

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Contain("收件人");
        dispatcher.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies addresses without an at-sign are rejected.
    /// </summary>
    [Fact]
    public async Task SendCommand_InvalidAddress_ShowsError()
    {
        var dispatcher = new FakeDispatcher();
        var viewModel = CreateCompose(dispatcher);
        viewModel.To = "a@x.com; nope";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Contain("nope");
        dispatcher.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a successful send builds the outgoing message, reads attachments and closes the window.
    /// </summary>
    [Fact]
    public async Task SendCommand_Valid_SendsMessageWithAttachmentAndCloses()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "attached", TestContext.Current.CancellationToken);
        var dispatcher = new FakeDispatcher();
        dispatcher.On<SendMessageCommand>(_ => Unit.Value);
        var dialogs = new FakeDialogService();
        dialogs.PickedFiles.Add(path);
        var viewModel = CreateCompose(dispatcher, dialogs);
        viewModel.To = "a@x.com, b@x.com";
        viewModel.Cc = "c@x.com";
        viewModel.Bcc = "d@x.com";
        viewModel.Subject = "Plan";
        viewModel.Body = "Hello";
        bool? closed = null;
        viewModel.CloseRequested += (_, sent) => closed = sent;

        try
        {
            await viewModel.AddAttachmentsCommand.ExecuteAsync(null);
            await viewModel.AddAttachmentsCommand.ExecuteAsync(null);
            viewModel.Attachments.Should().ContainSingle();

            await viewModel.SendCommand.ExecuteAsync(null);

            closed.Should().BeTrue();
            var command = dispatcher.Sent<SendMessageCommand>().Should().ContainSingle().Subject;
            command.AccountId.Should().Be(viewModel.SelectedAccount!.Id);
            command.Message.To.Should().Equal("a@x.com", "b@x.com");
            command.Message.Cc.Should().Equal("c@x.com");
            command.Message.Bcc.Should().Equal("d@x.com");
            command.Message.Subject.Should().Be("Plan");
            command.Message.TextBody.Should().Be("Hello");
            command.Message.Attachments.Should().ContainSingle().Which.FileName.Should().Be(Path.GetFileName(path));
            command.Message.Attachments[0].Content.ToArray().Should().Equal("attached"u8.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies a failed send keeps the window open with the draft intact.
    /// </summary>
    [Fact]
    public async Task SendCommand_ServerRejects_ShowsErrorAndStaysOpen()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<SendMessageCommand>(_ => throw new IOException("550 rejected"));
        var viewModel = CreateCompose(dispatcher);
        viewModel.To = "a@x.com";
        viewModel.Body = "draft";
        var closed = false;
        viewModel.CloseRequested += (_, _) => closed = true;

        await viewModel.SendCommand.ExecuteAsync(null);

        closed.Should().BeFalse();
        viewModel.ErrorMessage.Should().StartWith("发送失败").And.Contain("550 rejected");
        viewModel.Body.Should().Be("draft");
        viewModel.IsSending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies an attachment can be removed from the list.
    /// </summary>
    [Fact]
    public async Task RemoveCommand_Attachment_RemovesItFromList()
    {
        var dialogs = new FakeDialogService();
        dialogs.PickedFiles.Add(Path.Combine(Path.GetTempPath(), "does-not-need-to-exist.txt"));
        var viewModel = CreateCompose(new FakeDispatcher(), dialogs);
        await viewModel.AddAttachmentsCommand.ExecuteAsync(null);

        viewModel.Attachments[0].RemoveCommand.Execute(null);

        viewModel.Attachments.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies cancelling closes the window with a negative result.
    /// </summary>
    [Fact]
    public void CancelCommand_Compose_ClosesWithFalse()
    {
        var viewModel = CreateCompose(new FakeDispatcher());
        bool? closed = null;
        viewModel.CloseRequested += (_, sent) => closed = sent;

        viewModel.CancelCommand.Execute(null);

        closed.Should().BeFalse();
    }
}
