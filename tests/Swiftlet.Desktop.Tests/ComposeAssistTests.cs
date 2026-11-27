using System.Threading.Channels;
using AwesomeAssertions;
using Swiftlet.Application.Ai;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Ai;
using Swiftlet.Desktop.ViewModels.Compose;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests the writing help of the compose window and the splitting of a body into own text and quote.
/// </summary>
public sealed class ComposeAssistTests
{
    /// <summary>
    /// The dispatcher.
    /// </summary>
    private readonly FakeDispatcher _dispatcher = new();

    /// <summary>
    /// The quote of the reply under test.
    /// </summary>
    private const string Quote = "On Monday, Alice wrote:\n> line one\n> line two";

    /// <summary>
    /// The own text and the quote are told apart, also after the quote was edited.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="original">The original quote.</param>
    /// <param name="own">The expected own text.</param>
    /// <param name="quote">The expected quote.</param>
    [Theory]
    [InlineData("Hi\n\n" + Quote, Quote, "Hi", Quote)]
    [InlineData("\n\n" + Quote, Quote, "", Quote)]
    [InlineData("Hi\r\nthere\r\n\r\nOn Monday, Alice wrote:\r\n> line one\r\n> line two", Quote, "Hi\nthere", Quote)]
    [InlineData("Hi\n\nOn Monday, Alice wrote:\n> line one changed", Quote, "Hi", "On Monday, Alice wrote:\n> line one changed")]
    [InlineData("Hi\n\nAlice said:\n> kept", Quote, "Hi", "Alice said:\n> kept")]
    [InlineData("Hi\n> quoted\nbelow", "", "Hi", "> quoted\nbelow")]
    [InlineData("张三写道：\n> 内容", "", "", "张三写道：\n> 内容")]
    [InlineData("Just text\n", "", "Just text", "")]
    [InlineData("", "", "", "")]
    [InlineData("Hi\n\nOn Monday, Alice wrote:\n> line one\n> line two\nmy answer below", Quote, "Hi", Quote + "\nmy answer below")]
    public void Split_Body_SeparatesOwnTextFromQuote(string body, string original, string own, string quote)
    {
        var parts = ComposeBodyParts.Split(body, original);

        parts.Own.Should().Be(own);
        parts.Quote.Should().Be(quote);
    }

    /// <summary>
    /// The quote a reply or forward was built with is exactly what splitting its untouched body finds.
    /// </summary>
    /// <param name="forward">Whether the message is forwarded.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Split_TemplateBody_FindsTheWholeQuote(bool forward)
    {
        var account = TestData.Account();
        var message = TestData.Message(account, TestData.Folder(account, "INBOX", FolderRole.Inbox), 1, "s");
        var localizer = TestLocalizer.English();

        var request = forward ? ComposeTemplates.Forward(message, localizer) : ComposeTemplates.Reply(message, localizer);
        var parts = ComposeBodyParts.Split("Mine" + request.Body, request.Quote);

        parts.Own.Should().Be("Mine");
        parts.Quote.Should().Be(request.Quote).And.NotBeEmpty();
        request.SourceMessageId.Should().Be(forward ? null : message.Id);
    }

    /// <summary>
    /// A rewrite sends only the own text and replacing keeps the quote.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task RewriteThenReplace_Reply_ChangesOnlyOwnText()
    {
        AnswerRewrite(new AiOutput(AiOutputKind.Text, "Better"));
        var (assist, body) = Create("hi\n\n" + Quote);

        assist.RewriteCommand.Execute(AiWritingAction.Polish);
        await assist.Running;
        body().Should().Be("hi\n\n" + Quote, "the answer is only previewed");
        assist.ReplaceCommand.Execute(null);

        _dispatcher.Sent<RewriteTextQuery>().Should().ContainSingle().Which.Should().Be(
            new RewriteTextQuery("hi", AiWritingAction.Polish));
        body().Should().Be("Better\n\n" + Quote);
        assist.HasResult.Should().BeFalse();
    }

    /// <summary>
    /// Inserting puts the answer above the own text.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task InsertCommand_Answer_KeepsOwnTextBelow()
    {
        AnswerRewrite(new AiOutput(AiOutputKind.Text, "New"));
        var (assist, body) = Create("old\n\n" + Quote);

        assist.RewriteCommand.Execute(AiWritingAction.ToEnglish);
        await assist.Running;
        assist.InsertCommand.Execute(null);

        body().Should().Be("New\n\nold\n\n" + Quote);
    }

    /// <summary>
    /// Text edited after asking is not overwritten.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task ReplaceCommand_TextChangedMeanwhile_KeepsIt()
    {
        AnswerRewrite(new AiOutput(AiOutputKind.Text, "Better"));
        var text = "first";
        var assist = new ComposeAssistViewModel(
            _dispatcher, TestLocalizer.English(), () => text, value => text = value, string.Empty, null);
        assist.RewriteCommand.Execute(AiWritingAction.Polish);
        await assist.Running;

        text = "first and more";
        assist.ReplaceCommand.Execute(null);

        text.Should().Be("first and more");
        assist.ErrorText.Should().StartWith("Your text changed");
        assist.HasResult.Should().BeTrue();
    }

    /// <summary>
    /// A cut-off or refused answer cannot be put into the message.
    /// </summary>
    /// <param name="end">How the answer ends.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData(AiOutputKind.OutputTruncated)]
    [InlineData(AiOutputKind.Refused)]
    public async Task Rewrite_IncompleteAnswer_CannotBeUsed(AiOutputKind end)
    {
        AnswerRewrite(new AiOutput(AiOutputKind.Text, "part"), new AiOutput(end));
        var (assist, _) = Create("text");

        assist.RewriteCommand.Execute(AiWritingAction.Concise);
        await assist.Running;

        assist.ReplaceCommand.CanExecute(null).Should().BeFalse();
        assist.InsertCommand.CanExecute(null).Should().BeFalse();
        assist.HasResult.Should().Be(end == AiOutputKind.OutputTruncated);
    }

    /// <summary>
    /// Stopping leaves the partial answer unusable and cancels the request.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task StopCommand_Running_LeavesPartUnusable()
    {
        var source = Channel.CreateUnbounded<AiOutput>();
        var seen = CancellationToken.None;
        _dispatcher.OnStream<RewriteTextQuery, AiOutput>((_, token) =>
        {
            seen = token;
            return source.Reader.ReadAllAsync(token);
        });
        var (assist, _) = Create("text");
        assist.RewriteCommand.Execute(AiWritingAction.Polish);
        await source.Writer.WriteAsync(new AiOutput(AiOutputKind.Text, "part"), TestContext.Current.CancellationToken);
        while (!assist.HasResult)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        }

        assist.RewriteCommand.CanExecute(AiWritingAction.Polish).Should().BeFalse();
        assist.ReplaceCommand.CanExecute(null).Should().BeFalse();
        assist.StopCommand.Execute(null);
        await assist.Running;

        seen.IsCancellationRequested.Should().BeTrue();
        assist.Result.Should().Be("part");
        assist.ReplaceCommand.CanExecute(null).Should().BeFalse();
        assist.NoticeText.Should().Be("Stopped; the answer is incomplete.");
    }

    /// <summary>
    /// With nothing written there is nothing to rewrite, and the quote is not sent.
    /// </summary>
    [Fact]
    public void RewriteCommand_OnlyQuote_SendsNothing()
    {
        var (assist, _) = Create("\n\n" + Quote);

        assist.RewriteCommand.Execute(AiWritingAction.Polish);

        _dispatcher.Requests.Should().BeEmpty();
        assist.ErrorText.Should().StartWith("Write some text first");
    }

    /// <summary>
    /// A draft sends the replied message and the notes, and replaces the empty own text.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task DraftReplyCommand_Reply_SendsSourceAndNotes()
    {
        _dispatcher.OnStream<DraftReplyQuery, AiOutput>((_, token) =>
            AiAssistViewModelTests.Parts([new AiOutput(AiOutputKind.Text, "Dear Alice")], token));
        var source = (AccountId: Guid.NewGuid(), MessageId: Guid.NewGuid());
        var text = "\n\n" + Quote;
        var assist = new ComposeAssistViewModel(
            _dispatcher, TestLocalizer.English(), () => text, value => text = value, Quote, source);
        assist.Notes = "say yes";

        assist.DraftReplyCommand.Execute(null);
        await assist.Running;
        assist.ReplaceCommand.Execute(null);

        _dispatcher.Sent<DraftReplyQuery>().Should().ContainSingle().Which.Should().Be(
            new DraftReplyQuery(source.AccountId, source.MessageId, "say yes"));
        text.Should().Be("Dear Alice\n\n" + Quote);
    }

    /// <summary>
    /// Writing help exists only while AI is ready, and drafting only for replies.
    /// </summary>
    /// <param name="mode">The compose mode.</param>
    /// <param name="ready">Whether AI is ready.</param>
    [Theory]
    [InlineData(ComposeMode.Reply, true)]
    [InlineData(ComposeMode.Forward, true)]
    [InlineData(ComposeMode.New, true)]
    [InlineData(ComposeMode.Reply, false)]
    public void ComposeViewModel_Settings_DecideWritingHelp(ComposeMode mode, bool ready)
    {
        var account = TestData.Account();
        var settings = new DisabledAiSettings();
        if (ready)
        {
            settings.SetAi(AiAssistViewModelTests.Ready());
        }

        var request = new ComposeRequest(mode, account.Id, string.Empty, string.Empty, string.Empty, null, Quote, Guid.NewGuid());
        var viewModel = new ComposeViewModel(
            _dispatcher, new FakeDialogService(), TestLocalizer.English(), [account], request, ai: settings);

        (viewModel.Assist is not null).Should().Be(ready);
        if (ready)
        {
            viewModel.Assist!.CanDraft.Should().Be(mode == ComposeMode.Reply);
            viewModel.Assist.DraftReplyCommand.CanExecute(null).Should().Be(mode == ComposeMode.Reply);
        }
    }

    /// <summary>
    /// Closing the panel drops the answer.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task ToggleCommand_ClosingThePanel_DropsTheAnswer()
    {
        AnswerRewrite(new AiOutput(AiOutputKind.Text, "x"));
        var (assist, _) = Create("text");
        assist.ToggleCommand.Execute(null);
        assist.RewriteCommand.Execute(AiWritingAction.Polish);
        await assist.Running;

        assist.ToggleCommand.Execute(null);

        assist.IsOpen.Should().BeFalse();
        assist.HasResult.Should().BeFalse();
        assist.Dispose();
    }

    /// <summary>
    /// Makes rewrites answer with the given parts.
    /// </summary>
    /// <param name="parts">The parts.</param>
    private void AnswerRewrite(params AiOutput[] parts)
    {
        _dispatcher.OnStream<RewriteTextQuery, AiOutput>((_, token) => AiAssistViewModelTests.Parts(parts, token));
    }

    /// <summary>
    /// Creates writing help over a body held by the test.
    /// </summary>
    /// <param name="body">The initial body.</param>
    /// <returns>The view model and a reader of the body.</returns>
    private (ComposeAssistViewModel Assist, Func<string> Body) Create(string body)
    {
        var text = body;
        var assist = new ComposeAssistViewModel(
            _dispatcher, TestLocalizer.English(), () => text, value => text = value, Quote, null);
        return (assist, () => text);
    }
}
