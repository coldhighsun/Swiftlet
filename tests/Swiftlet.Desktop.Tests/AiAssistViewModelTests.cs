using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using Swiftlet.Application.Ai;
using Swiftlet.Desktop.ViewModels.Ai;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests <see cref="AiAssistViewModel"/>.
/// </summary>
public sealed class AiAssistViewModelTests
{
    /// <summary>
    /// The dispatcher.
    /// </summary>
    private readonly FakeDispatcher _dispatcher = new();

    /// <summary>
    /// The dialogs.
    /// </summary>
    private readonly FakeDialogService _dialogs = new();

    /// <summary>
    /// The settings.
    /// </summary>
    private readonly DisabledAiSettings _settings = new();

    /// <summary>
    /// The shown message.
    /// </summary>
    private readonly MailMessage _message;

    /// <summary>
    /// Another message.
    /// </summary>
    private readonly MailMessage _other;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiAssistViewModelTests"/> class with AI ready.
    /// </summary>
    public AiAssistViewModelTests()
    {
        var account = TestData.Account();
        var inbox = TestData.Folder(account, "INBOX", FolderRole.Inbox);
        _message = TestData.Message(account, inbox, 1, "one");
        _other = TestData.Message(account, inbox, 2, "two");
        _settings.SetAi(Ready());
    }

    /// <summary>
    /// The actions need AI to be ready and a message, and follow a change of the settings.
    /// </summary>
    [Fact]
    public void SummarizeCommand_SettingsAndSelection_DecideAvailability()
    {
        var viewModel = Create();
        viewModel.SummarizeCommand.CanExecute(null).Should().BeFalse("no message is shown");

        viewModel.Show(_message);
        viewModel.SummarizeCommand.CanExecute(null).Should().BeTrue();

        _settings.SetAi(_settings.Ai with { IsEnabled = false });
        viewModel.IsAvailable.Should().BeFalse();
        viewModel.TranslateCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// The answer appears in the panel as it arrives, in the language of the interface.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SummarizeCommand_Answer_ShowsItAndTheNotices()
    {
        Answer(
            new AiOutput(AiOutputKind.InputTruncated),
            new AiOutput(AiOutputKind.Text, "Hel"),
            new AiOutput(AiOutputKind.Text, "lo"));
        var viewModel = Create();
        viewModel.Show(_message);

        viewModel.SummarizeCommand.Execute(null);
        await viewModel.Running;

        viewModel.IsOpen.Should().BeTrue();
        viewModel.Title.Should().Be("Summary");
        viewModel.Text.Should().Be("Hello");
        viewModel.IsRunning.Should().BeFalse();
        viewModel.NoticeText.Should().Be("The message is long: only its beginning was sent.");
        viewModel.ErrorText.Should().BeNull();
        _dispatcher.Sent<SummarizeMessageQuery>().Should().ContainSingle().Which.Should().Be(
            new SummarizeMessageQuery(_message.AccountId, _message.Id, AiLanguage.English));
    }

    /// <summary>
    /// Choosing the other language asks again, and generating again ignores the kept answer.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task LanguageAndRegenerate_OpenPanel_AskAgain()
    {
        _dispatcher.OnStream<TranslateMessageQuery, AiOutput>((_, token) =>
            Parts([new AiOutput(AiOutputKind.Text, "x")], token));
        var viewModel = Create();
        viewModel.Show(_message);
        viewModel.TranslateCommand.Execute(null);

        viewModel.IsChinese = true;
        await viewModel.Running;
        viewModel.RegenerateCommand.Execute(null);
        await viewModel.Running;

        _dispatcher.Sent<TranslateMessageQuery>().Select(q => (q.Language, q.Fresh)).Should().Equal(
            (AiLanguage.English, false),
            (AiLanguage.SimplifiedChinese, false),
            (AiLanguage.SimplifiedChinese, true));
    }

    /// <summary>
    /// A refusal removes the partial answer.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SummarizeCommand_Refused_DiscardsTheText()
    {
        Answer(new AiOutput(AiOutputKind.Text, "part"), new AiOutput(AiOutputKind.Refused));
        var viewModel = Create();
        viewModel.Show(_message);

        viewModel.SummarizeCommand.Execute(null);
        await viewModel.Running;

        viewModel.Text.Should().BeEmpty();
        viewModel.ErrorText.Should().Be("The model declined to process this message.");
    }

    /// <summary>
    /// A failed request shows why, and the text follows the language.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SummarizeCommand_Failure_ShowsTheReason()
    {
        _dispatcher.OnStream<SummarizeMessageQuery, AiOutput>((_, _) => throw new AiException(AiFailure.Network));
        var localizer = TestLocalizer.English();
        var viewModel = Create(localizer);
        viewModel.Show(_message);

        viewModel.SummarizeCommand.Execute(null);
        await viewModel.Running;

        viewModel.ErrorText.Should().Be("The service could not be reached.");
        localizer.SetPreference("zh-Hans");
        viewModel.ErrorText.Should().Be("无法连接到该服务。");
    }

    /// <summary>
    /// An answer without text is reported.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SummarizeCommand_NoText_SaysSo()
    {
        Answer();
        var viewModel = Create();
        viewModel.Show(_message);

        viewModel.SummarizeCommand.Execute(null);
        await viewModel.Running;

        viewModel.ErrorText.Should().Be("The model returned no text.");
    }

    /// <summary>
    /// Stopping keeps what arrived and cancels the request.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task StopCommand_Running_KeepsThePartAndCancels()
    {
        var (viewModel, source, token) = await StartPendingAsync();

        viewModel.StopCommand.Execute(null);
        await viewModel.Running;

        token().IsCancellationRequested.Should().BeTrue();
        viewModel.Text.Should().Be("part");
        viewModel.IsOpen.Should().BeTrue();
        viewModel.IsRunning.Should().BeFalse();
        viewModel.NoticeText.Should().Be("Stopped; the answer is incomplete.");
        source.Writer.TryComplete();
    }

    /// <summary>
    /// Showing another message cancels the request and closes the panel; showing the same one again does not.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Show_OtherMessage_ClosesThePanel()
    {
        var (viewModel, _, token) = await StartPendingAsync();

        viewModel.Show(_message);
        viewModel.IsOpen.Should().BeTrue();

        viewModel.Show(_other);
        await viewModel.Running;

        token().IsCancellationRequested.Should().BeTrue();
        viewModel.IsOpen.Should().BeFalse();
        viewModel.Text.Should().BeEmpty();
        viewModel.ErrorText.Should().BeNull();
    }

    /// <summary>
    /// The answer can be copied.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task CopyCommand_Answer_CopiesIt()
    {
        Answer(new AiOutput(AiOutputKind.Text, "copy me"));
        var viewModel = Create();
        viewModel.Show(_message);
        viewModel.CopyCommand.CanExecute(null).Should().BeFalse();
        viewModel.SummarizeCommand.Execute(null);
        await viewModel.Running;

        await viewModel.CopyCommand.ExecuteAsync(null);

        _dialogs.CopiedTexts.Should().Equal("copy me");
    }

    /// <summary>
    /// Builds settings that allow requests.
    /// </summary>
    /// <returns>The settings.</returns>
    internal static AiConfiguration Ready()
    {
        return AiConfiguration.Default with
        {
            IsEnabled = true,
            ConfirmedDestination = "Claude|https://api.anthropic.com",
        };
    }

    /// <summary>
    /// Streams prepared parts.
    /// </summary>
    /// <param name="parts">The parts.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream.</returns>
    internal static async IAsyncEnumerable<AiOutput> Parts(
        AiOutput[] parts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return part;
        }
    }

    /// <summary>
    /// Makes summaries answer with the given parts.
    /// </summary>
    /// <param name="parts">The parts.</param>
    private void Answer(params AiOutput[] parts)
    {
        _dispatcher.OnStream<SummarizeMessageQuery, AiOutput>((_, token) => Parts(parts, token));
    }

    /// <summary>
    /// Starts a summary that has delivered one part and waits for more.
    /// </summary>
    /// <returns>The view model, the source of further parts and the token of the request.</returns>
    private async Task<(AiAssistViewModel ViewModel, Channel<AiOutput> Source, Func<CancellationToken> Token)> StartPendingAsync()
    {
        var source = Channel.CreateUnbounded<AiOutput>();
        var seen = CancellationToken.None;
        _dispatcher.OnStream<SummarizeMessageQuery, AiOutput>((_, token) =>
        {
            seen = token;
            return source.Reader.ReadAllAsync(token);
        });
        var viewModel = Create();
        viewModel.Show(_message);
        viewModel.SummarizeCommand.Execute(null);
        viewModel.IsWaiting.Should().BeTrue();

        await source.Writer.WriteAsync(new AiOutput(AiOutputKind.Text, "part"), TestContext.Current.CancellationToken);
        while (viewModel.Text.Length == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        }

        return (viewModel, source, () => seen);
    }

    /// <summary>
    /// Creates the view model.
    /// </summary>
    /// <param name="localizer">The localizer; English by default.</param>
    /// <returns>The view model.</returns>
    private AiAssistViewModel Create(Swiftlet.Desktop.Localization.ILocalizer? localizer = null)
    {
        return new AiAssistViewModel(
            _dispatcher, _dialogs, localizer ?? TestLocalizer.English(), _settings, new FakeUiThread());
    }
}
