using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Ai;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels.Ai;

/// <summary>
/// The AI panel of the reading pane: it summarizes or translates the shown message when the user asks. Showing another
/// message stops the request and closes the panel.
/// </summary>
public sealed partial class AiAssistViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The request dispatcher.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The dialog service, used for the clipboard.
    /// </summary>
    private readonly IDialogService _dialogs;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The AI settings, which decide whether the actions are offered.
    /// </summary>
    private readonly IAiSettings _settings;

    /// <summary>
    /// Brings settings changes to the UI thread.
    /// </summary>
    private readonly IUiThread _uiThread;

    /// <summary>
    /// The logger, if any.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// The shown message.
    /// </summary>
    private MailMessage? _message;

    /// <summary>
    /// The task of the open panel.
    /// </summary>
    private AiMessageTask _task;

    /// <summary>
    /// Cancels the running request; also tells a request whether it is still the current one.
    /// </summary>
    private CancellationTokenSource? _running;

    /// <summary>
    /// The key of the notice shown, so it can follow the language.
    /// </summary>
    private readonly List<AiNotice> _notices = [];

    /// <summary>
    /// The problem shown, kept so it can follow the language.
    /// </summary>
    private Func<string>? _error;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiAssistViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="dialogs">The dialog service.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="settings">The AI settings.</param>
    /// <param name="uiThread">The UI thread marshaller.</param>
    /// <param name="logger">The logger; optional.</param>
    public AiAssistViewModel(
        IRequestDispatcher dispatcher,
        IDialogService dialogs,
        ILocalizer localizer,
        IAiSettings settings,
        IUiThread uiThread,
        ILogger<AiAssistViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _localizer = localizer;
        _settings = settings;
        _uiThread = uiThread;
        _logger = logger;
        IsAvailable = settings.Ai.IsReady;
        settings.AiChanged += OnSettingsChanged;
    }

    /// <summary>
    /// What can be remarked about an answer.
    /// </summary>
    private enum AiNotice
    {
        /// <summary>
        /// Only the beginning of the message was sent.
        /// </summary>
        InputTruncated,

        /// <summary>
        /// The answer stopped at the length limit.
        /// </summary>
        OutputTruncated,

        /// <summary>
        /// The user stopped the request.
        /// </summary>
        Stopped,
    }

    /// <summary>
    /// Gets or sets a value indicating whether the AI actions are offered.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SummarizeCommand), nameof(TranslateCommand))]
    public partial bool IsAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the panel is shown.
    /// </summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a request is running.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(RegenerateCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets the answer received so far.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(HasText))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language of the answer.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnglish), nameof(IsChinese))]
    public partial AiLanguage Language { get; set; }

    /// <summary>
    /// Gets the title of the panel.
    /// </summary>
    public string Title => _task == AiMessageTask.Summarize
        ? _localizer["Ai_SummaryTitle"]
        : _localizer["Ai_TranslationTitle"];

    /// <summary>
    /// Gets a value indicating whether a request runs and nothing has arrived yet.
    /// </summary>
    public bool IsWaiting => IsRunning && Text.Length == 0;

    /// <summary>
    /// Gets a value indicating whether there is text to show.
    /// </summary>
    public bool HasText => Text.Length > 0;

    /// <summary>
    /// Gets or sets a value indicating whether the answer is in English; choosing it asks again.
    /// </summary>
    public bool IsEnglish
    {
        get => Language == AiLanguage.English;
        set => ChooseLanguage(value, AiLanguage.English);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the answer is in Chinese; choosing it asks again.
    /// </summary>
    public bool IsChinese
    {
        get => Language == AiLanguage.SimplifiedChinese;
        set => ChooseLanguage(value, AiLanguage.SimplifiedChinese);
    }

    /// <summary>
    /// Gets the remarks about the answer, or <see langword="null"/>.
    /// </summary>
    public string? NoticeText => _notices.Count == 0
        ? null
        : string.Join(' ', _notices.Select(notice => notice switch
        {
            AiNotice.InputTruncated => _localizer["Ai_InputTruncated"],
            AiNotice.OutputTruncated => _localizer["Ai_OutputTruncated"],
            _ => _localizer["Ai_Stopped"],
        }));

    /// <summary>
    /// Gets the problem of the last request, or <see langword="null"/>.
    /// </summary>
    public string? ErrorText => _error?.Invoke();

    /// <summary>
    /// Gets the latest request, so tests can wait for it.
    /// </summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Follows the selection of the message list.
    /// </summary>
    /// <param name="message">The selected message, if any.</param>
    public void Show(MailMessage? message)
    {
        if (message?.Id == _message?.Id)
        {
            // The same message, possibly read again by a refresh: the panel stays.
            _message = message;
            return;
        }

        Close();
        _message = message;
        SummarizeCommand.NotifyCanExecuteChanged();
        TranslateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Re-reads the texts built by this view model after the language changed.
    /// </summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(ErrorText));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.AiChanged -= OnSettingsChanged;
        Cancel();
    }

    /// <summary>
    /// Summarizes the shown message.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void Summarize()
    {
        Open(AiMessageTask.Summarize);
    }

    /// <summary>
    /// Translates the shown message.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void Translate()
    {
        Open(AiMessageTask.Translate);
    }

    /// <summary>
    /// Stops the running request and keeps what arrived.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        Cancel();
        _notices.Add(AiNotice.Stopped);
        OnPropertyChanged(nameof(NoticeText));
    }

    /// <summary>
    /// Asks again, ignoring the kept answer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private void Regenerate()
    {
        Start(fresh: true);
    }

    /// <summary>
    /// Copies the answer.
    /// </summary>
    /// <returns>A task that completes when the text is copied or the failure is shown.</returns>
    [RelayCommand(CanExecute = nameof(HasText))]
    private async Task CopyAsync()
    {
        try
        {
            await _dialogs.CopyTextAsync(Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: a clipboard another program holds must not crash the app.
            _logger?.LogWarning(ex, "The AI answer could not be copied");
            var message = ex.Message;
            SetError(() => message);
        }
    }

    /// <summary>
    /// Stops the request and hides the panel.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        Cancel();
        IsOpen = false;
        Clear();
    }

    /// <summary>
    /// Gets a value indicating whether an action can be started.
    /// </summary>
    private bool CanAsk => IsAvailable && _message is not null;

    /// <summary>
    /// Gets a value indicating whether the answer can be asked for again.
    /// </summary>
    private bool CanRegenerate => !IsRunning;

    /// <summary>
    /// Opens the panel for a task, in the language of the interface.
    /// </summary>
    /// <param name="task">The task.</param>
    private void Open(AiMessageTask task)
    {
        if (!IsOpen)
        {
            Language = _localizer.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? AiLanguage.SimplifiedChinese
                : AiLanguage.English;
        }

        _task = task;
        IsOpen = true;
        OnPropertyChanged(nameof(Title));
        Start(fresh: false);
    }

    /// <summary>
    /// Switches the answer language when a language button is ticked.
    /// </summary>
    /// <param name="ticked">The new state of the button.</param>
    /// <param name="language">The language of the button.</param>
    private void ChooseLanguage(bool ticked, AiLanguage language)
    {
        if (ticked && Language != language)
        {
            Language = language;
            if (IsOpen)
            {
                Start(fresh: false);
            }
        }
    }

    /// <summary>
    /// Replaces any running request by a new one.
    /// </summary>
    /// <param name="fresh">Whether to ignore the kept answer.</param>
    private void Start(bool fresh)
    {
        Cancel();
        Clear();
        if (_message is not { } message)
        {
            return;
        }

        var running = new CancellationTokenSource();
        _running = running;
        IsRunning = true;
        IStreamQuery<AiOutput> query = _task == AiMessageTask.Summarize
            ? new SummarizeMessageQuery(message.AccountId, message.Id, Language, fresh)
            : new TranslateMessageQuery(message.AccountId, message.Id, Language, fresh);
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
        var refused = false;
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
                        Text = answer.ToString();
                        break;
                    case AiOutputKind.InputTruncated:
                        AddNotice(AiNotice.InputTruncated);
                        break;
                    case AiOutputKind.OutputTruncated:
                        AddNotice(AiNotice.OutputTruncated);
                        break;
                    case AiOutputKind.Refused:
                        refused = true;
                        Text = string.Empty;
                        SetError(() => _localizer["Ai_Refused"]);
                        break;
                }
            }

            if (_running == running && !refused && string.IsNullOrWhiteSpace(Text))
            {
                SetError(() => _localizer["Ai_EmptyAnswer"]);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The user stopped it or moved on; what arrived stays as it is.
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
            _logger?.LogWarning(ex, "The AI request could not be completed");
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
    /// Empties the panel.
    /// </summary>
    private void Clear()
    {
        Text = string.Empty;
        _notices.Clear();
        OnPropertyChanged(nameof(NoticeText));
        SetError(null);
    }

    /// <summary>
    /// Adds a remark about the answer.
    /// </summary>
    /// <param name="notice">The remark.</param>
    private void AddNotice(AiNotice notice)
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
    /// Offers or withdraws the actions after the settings changed.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // The event may come from any thread, and a failure of this update would only be a bug worth surfacing there.
        _ = _uiThread.InvokeAsync(() =>
        {
            IsAvailable = _settings.Ai.IsReady;
            if (!IsAvailable)
            {
                Close();
            }

            return Task.CompletedTask;
        });
    }
}
