using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Ai;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Compose;

namespace Swiftlet.Desktop.ViewModels.Ai;

/// <summary>
/// The writing help of a compose window: it drafts a reply or rewrites the user's own text. The answer is only
/// previewed; the body changes when the user chooses to replace or insert.
/// </summary>
public sealed partial class ComposeAssistViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The request dispatcher.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Reads the body of the message being written.
    /// </summary>
    private readonly Func<string> _getBody;

    /// <summary>
    /// Replaces the body of the message being written.
    /// </summary>
    private readonly Action<string> _setBody;

    /// <summary>
    /// The quote the window was opened with.
    /// </summary>
    private readonly string _quote;

    /// <summary>
    /// The account and message being replied to, if any.
    /// </summary>
    private readonly (Guid AccountId, Guid MessageId)? _source;

    /// <summary>
    /// The logger, if any.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// Cancels the running request; also tells a request whether it is still the current one.
    /// </summary>
    private CancellationTokenSource? _running;

    /// <summary>
    /// The user's text when the current answer was asked for.
    /// </summary>
    private string _asked = string.Empty;

    /// <summary>
    /// The problem shown, kept so it can follow the language.
    /// </summary>
    private Func<string>? _error;

    /// <summary>
    /// The remarks about the answer, kept so they can follow the language.
    /// </summary>
    private readonly List<Func<string>> _notices = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ComposeAssistViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="getBody">Reads the body.</param>
    /// <param name="setBody">Replaces the body.</param>
    /// <param name="quote">The quote the window was opened with; may be empty.</param>
    /// <param name="source">The account and message being replied to, if any.</param>
    /// <param name="logger">The logger; optional.</param>
    public ComposeAssistViewModel(
        IRequestDispatcher dispatcher,
        ILocalizer localizer,
        Func<string> getBody,
        Action<string> setBody,
        string quote,
        (Guid AccountId, Guid MessageId)? source,
        ILogger? logger = null)
    {
        _dispatcher = dispatcher;
        _localizer = localizer;
        _getBody = getBody;
        _setBody = setBody;
        _quote = quote;
        _source = source;
        _logger = logger;
        localizer.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the panel is shown.
    /// </summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>
    /// Gets or sets what the user wants the reply to say.
    /// </summary>
    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a request is running.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(DraftReplyCommand), nameof(RewriteCommand), nameof(StopCommand), nameof(ReplaceCommand), nameof(InsertCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the whole answer arrived; only such an answer can be used.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand), nameof(InsertCommand))]
    public partial bool IsComplete { get; set; }

    /// <summary>
    /// Gets or sets the answer received so far.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    public partial string Result { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether a reply can be drafted.
    /// </summary>
    public bool CanDraft => _source is not null;

    /// <summary>
    /// Gets a value indicating whether there is an answer to show.
    /// </summary>
    public bool HasResult => Result.Length > 0;

    /// <summary>
    /// Gets the remarks about the answer, or <see langword="null"/>.
    /// </summary>
    public string? NoticeText => _notices.Count == 0 ? null : string.Join(' ', _notices.Select(notice => notice()));

    /// <summary>
    /// Gets the problem of the last action, or <see langword="null"/>.
    /// </summary>
    public string? ErrorText => _error?.Invoke();

    /// <summary>
    /// Gets the latest request, so tests can wait for it.
    /// </summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Cancels the request and stops listening to the localizer, which outlives the window.
    /// </summary>
    public void Dispose()
    {
        _localizer.LanguageChanged -= OnLanguageChanged;
        Cancel();
    }

    /// <summary>
    /// Shows or hides the panel; hiding it drops the answer.
    /// </summary>
    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        if (!IsOpen)
        {
            Cancel();
            Clear();
        }
    }

    /// <summary>
    /// Drafts a reply from the notes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartDraft))]
    private void DraftReply()
    {
        var (accountId, messageId) = _source!.Value;
        Start(new DraftReplyQuery(accountId, messageId, Notes), ComposeBodyParts.Split(_getBody(), _quote).Own);
    }

    /// <summary>
    /// Rewrites the user's own text.
    /// </summary>
    /// <param name="action">How to rewrite it.</param>
    [RelayCommand(CanExecute = nameof(CanStartRewrite))]
    private void Rewrite(AiWritingAction action)
    {
        var own = ComposeBodyParts.Split(_getBody(), _quote).Own;
        if (string.IsNullOrWhiteSpace(own))
        {
            Cancel();
            Clear();
            SetError(() => _localizer["Ai_NothingToRewrite"]);
            return;
        }

        Start(new RewriteTextQuery(own, action), own);
    }

    /// <summary>
    /// Stops the running request; the partial answer stays visible but cannot be used.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        Cancel();
        AddNotice(() => _localizer["Ai_Stopped"]);
    }

    /// <summary>
    /// Replaces the user's text with the answer, unless the text changed since it was asked for.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Replace()
    {
        var parts = ComposeBodyParts.Split(_getBody(), _quote);
        if (parts.Own != _asked)
        {
            SetError(() => _localizer["Ai_TextChanged"]);
            return;
        }

        _setBody(ComposeBodyParts.Join(Result, parts.Quote));
        Clear();
    }

    /// <summary>
    /// Puts the answer above the user's text.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Insert()
    {
        var parts = ComposeBodyParts.Split(_getBody(), _quote);
        var own = parts.Own.Length == 0 ? Result : $"{Result.TrimEnd()}\n\n{parts.Own}";
        _setBody(ComposeBodyParts.Join(own, parts.Quote));
        Clear();
    }

    /// <summary>
    /// Drops the answer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void Discard()
    {
        Cancel();
        Clear();
    }

    /// <summary>
    /// Gets a value indicating whether a draft can be started.
    /// </summary>
    private bool CanStartDraft => !IsRunning && CanDraft;

    /// <summary>
    /// Gets a value indicating whether a rewrite can be started.
    /// </summary>
    /// <returns>True if no request is running.</returns>
    private bool CanStartRewrite()
    {
        return !IsRunning;
    }

    /// <summary>
    /// Gets a value indicating whether the answer can be put into the message.
    /// </summary>
    private bool CanUse => !IsRunning && IsComplete;

    /// <summary>
    /// Starts a request.
    /// </summary>
    /// <param name="query">The request.</param>
    /// <param name="own">The user's text at this moment.</param>
    private void Start(IStreamQuery<AiOutput> query, string own)
    {
        Cancel();
        Clear();
        _asked = own;
        var running = new CancellationTokenSource();
        _running = running;
        IsRunning = true;
        Running = ReceiveAsync(query, running);
    }

    /// <summary>
    /// Shows an answer as it arrives, as long as its request is the current one.
    /// </summary>
    /// <param name="query">The request.</param>
    /// <param name="running">The cancellation source of the request, disposed here.</param>
    /// <returns>A task that completes when the request ended.</returns>
    private async Task ReceiveAsync(IStreamQuery<AiOutput> query, CancellationTokenSource running)
    {
        var token = running.Token;
        var answer = new StringBuilder();
        var usable = true;
        try
        {
            await foreach (var part in _dispatcher.StreamAsync(query, token))
            {
                if (_running != running)
                {
                    return;
                }

                switch (part.Kind)
                {
                    case AiOutputKind.Text:
                        answer.Append(part.Text);
                        Result = answer.ToString();
                        break;
                    case AiOutputKind.InputTruncated:
                        AddNotice(() => _localizer["Ai_InputTruncated"]);
                        break;
                    case AiOutputKind.OutputTruncated:
                        // Replacing text with a cut answer would lose its end.
                        usable = false;
                        AddNotice(() => _localizer["Ai_OutputTruncated"]);
                        break;
                    case AiOutputKind.Refused:
                        usable = false;
                        Result = string.Empty;
                        SetError(() => _localizer["Ai_Refused"]);
                        break;
                }
            }

            if (_running == running)
            {
                if (usable && string.IsNullOrWhiteSpace(Result))
                {
                    SetError(() => _localizer["Ai_EmptyAnswer"]);
                }
                else
                {
                    IsComplete = usable;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The user stopped it or closed the window.
        }
        catch (AiException ex)
        {
            if (_running == running)
            {
                SetError(() => AiFailureText.Describe(_localizer, ex.Failure));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: reading the message or the stored key can fail too.
            _logger?.LogWarning(ex, "The AI writing request could not be completed");
            if (_running == running)
            {
                var message = ex.Message;
                SetError(() => _localizer.Format("Error_Format", _localizer["Ai_ErrorUnknown"], message));
            }
        }
        finally
        {
            if (_running == running)
            {
                _running = null;
                IsRunning = false;
            }

            running.Dispose();
        }
    }

    /// <summary>
    /// Cancels the current request, if any.
    /// </summary>
    private void Cancel()
    {
        if (_running is not { } running)
        {
            return;
        }

        // Cleared first, so whatever still arrives is ignored.
        _running = null;
        IsRunning = false;
        running.Cancel();
    }

    /// <summary>
    /// Drops the answer and the messages.
    /// </summary>
    private void Clear()
    {
        Result = string.Empty;
        IsComplete = false;
        _notices.Clear();
        OnPropertyChanged(nameof(NoticeText));
        SetError(null);
    }

    /// <summary>
    /// Adds a remark about the answer.
    /// </summary>
    /// <param name="notice">Builds the text in the current language.</param>
    private void AddNotice(Func<string> notice)
    {
        _notices.Add(notice);
        OnPropertyChanged(nameof(NoticeText));
    }

    /// <summary>
    /// Shows or clears the problem.
    /// </summary>
    /// <param name="error">Builds the text in the current language, or <see langword="null"/>.</param>
    private void SetError(Func<string>? error)
    {
        _error = error;
        OnPropertyChanged(nameof(ErrorText));
    }

    /// <summary>
    /// Re-reads the texts built by this view model.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(ErrorText));
    }
}
