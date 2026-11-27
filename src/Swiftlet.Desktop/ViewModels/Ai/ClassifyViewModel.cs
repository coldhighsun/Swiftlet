using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Ai;
using Swiftlet.Application.Labels;
using Swiftlet.Application.Messages;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels.Ai;

/// <summary>
/// One suggested change of one message.
/// </summary>
public sealed partial class SuggestionViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestionViewModel"/> class, ticked.
    /// </summary>
    /// <param name="text">The text shown.</param>
    /// <param name="labelId">The label to attach, if this suggests one.</param>
    /// <param name="priority">The priority to set, if this suggests one.</param>
    public SuggestionViewModel(string text, Guid? labelId, MessagePriority? priority)
    {
        Text = text;
        LabelId = labelId;
        Priority = priority;
        IsChecked = true;
    }

    /// <summary>
    /// Gets the text shown.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the label to attach, if this suggests one.
    /// </summary>
    public Guid? LabelId { get; }

    /// <summary>
    /// Gets the priority to set, if this suggests one.
    /// </summary>
    public MessagePriority? Priority { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the change is to be applied.
    /// </summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// One message of the classification dialog.
/// </summary>
/// <param name="message">The message.</param>
public sealed partial class ClassifyItemViewModel(MailMessage message) : ObservableObject
{
    /// <summary>
    /// Gets the message identifiers.
    /// </summary>
    public MessageReference Reference { get; } = new(message.AccountId, message.Id);

    /// <summary>
    /// Gets the subject.
    /// </summary>
    public string Subject { get; } = message.Subject;

    /// <summary>
    /// Gets the sender.
    /// </summary>
    public string Sender { get; } = string.IsNullOrWhiteSpace(message.FromName) ? message.FromAddress : message.FromName;

    /// <summary>
    /// Gets or sets the state of a message without suggestions, or <see langword="null"/> once it has some.
    /// </summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>
    /// Gets or sets the suggestions.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<SuggestionViewModel> Suggestions { get; set; } = [];
}

/// <summary>
/// The view model of the dialog that suggests labels and priorities and applies the ticked ones.
/// </summary>
public sealed partial class ClassifyViewModel : ObservableObject, IDisposable
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
    /// The existing labels.
    /// </summary>
    private readonly IReadOnlyList<Label> _labels;

    /// <summary>
    /// The logger, if any.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// Cancels the running request.
    /// </summary>
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>
    /// How many messages have a result.
    /// </summary>
    private int _done;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClassifyViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="messages">The selected messages.</param>
    /// <param name="labels">The existing labels.</param>
    /// <param name="logger">The logger; optional.</param>
    public ClassifyViewModel(
        IRequestDispatcher dispatcher,
        ILocalizer localizer,
        IReadOnlyList<MailMessage> messages,
        IReadOnlyList<Label> labels,
        ILogger<ClassifyViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(messages);
        _dispatcher = dispatcher;
        _localizer = localizer;
        _labels = labels;
        _logger = logger;
        Items = [.. messages.Take(ClassifyMessagesQuery.MaxMessages)
            .Select(m => new ClassifyItemViewModel(m) { StatusText = localizer["Classify_Waiting"] })];
        LimitText = messages.Count > Items.Count
            ? localizer.Format("Classify_Limit", ClassifyMessagesQuery.MaxMessages)
            : null;
        UpdateProgress();
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Gets the messages.
    /// </summary>
    public IReadOnlyList<ClassifyItemViewModel> Items { get; }

    /// <summary>
    /// Gets the note that not all selected messages are classified, or <see langword="null"/>.
    /// </summary>
    public string? LimitText { get; }

    /// <summary>
    /// Gets a value indicating whether something was changed.
    /// </summary>
    public bool HasChanges { get; private set; }

    /// <summary>
    /// Gets or sets how many messages are done.
    /// </summary>
    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether results are still arriving.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(ApplyCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the changes are being saved.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsApplying { get; set; }

    /// <summary>
    /// Gets or sets the problem, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Gets the running request, so tests can wait for it.
    /// </summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts classifying; called once the window is shown.
    /// </summary>
    public void Start()
    {
        IsRunning = true;
        Running = ReceiveAsync();
    }

    /// <summary>
    /// Cancels the request; called when the window has closed.
    /// </summary>
    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    /// <summary>
    /// Stops asking and keeps the suggestions received.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _cancellation.Cancel();
    }

    /// <summary>
    /// Saves the ticked suggestions and closes the window.
    /// </summary>
    /// <returns>A task that completes when the changes are saved or the failure is shown.</returns>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        ErrorText = null;
        IsApplying = true;
        try
        {
            foreach (var item in Items)
            {
                var (accountId, messageId) = item.Reference;
                foreach (var suggestion in item.Suggestions.Where(s => s.IsChecked))
                {
                    if (suggestion.LabelId is { } labelId)
                    {
                        await _dispatcher.SendAsync(new SetMessageLabelCommand(accountId, messageId, labelId, true));
                    }
                    else
                    {
                        await _dispatcher.SendAsync(
                            new SetMessagePriorityCommand(accountId, messageId, suggestion.Priority));
                    }

                    HasChanges = true;
                }
            }

            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: what was applied stays, and the user can try the rest again.
            _logger?.LogWarning(ex, "The classification could not be applied");
            ErrorText = _localizer.Format("Classify_ApplyFailed", ex.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>
    /// Closes the window without applying anything more.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets a value indicating whether the suggestions can be applied. Which ones are ticked is not tracked: applying
    /// none just closes the window.
    /// </summary>
    private bool CanApply => !IsRunning && !IsApplying && Items.Any(i => i.Suggestions.Count > 0);

    /// <summary>
    /// Shows the results as they arrive.
    /// </summary>
    /// <returns>A task that completes when the request ended.</returns>
    private async Task ReceiveAsync()
    {
        var token = _cancellation.Token;
        try
        {
            var query = new ClassifyMessagesQuery([.. Items.Select(i => i.Reference)]);
            await foreach (var result in _dispatcher.StreamAsync(query, token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                Show(result);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The user stopped it or closed the window.
        }
        catch (AiException ex)
        {
            ErrorText = AiFailureText.Describe(_localizer, ex.Failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: reading the messages or the stored key can fail too.
            _logger?.LogWarning(ex, "The classification could not be completed");
            ErrorText = _localizer.Format("Error_Format", _localizer["Ai_ErrorUnknown"], ex.Message);
        }
        finally
        {
            var waiting = _localizer["Classify_Waiting"];
            foreach (var item in Items.Where(i => i.StatusText == waiting))
            {
                item.StatusText = _localizer["Classify_NotAsked"];
            }

            IsRunning = false;
        }
    }

    /// <summary>
    /// Shows the result of one message.
    /// </summary>
    /// <param name="result">The result.</param>
    private void Show(MessageClassification result)
    {
        if (Items.FirstOrDefault(i => i.Reference == result.Message) is not { } item)
        {
            return;
        }

        List<SuggestionViewModel> suggestions = [];
        foreach (var labelId in result.LabelIds)
        {
            if (_labels.FirstOrDefault(l => l.Id == labelId) is { } label)
            {
                suggestions.Add(new SuggestionViewModel(_localizer.Format("Classify_Label", label.Name), labelId, null));
            }
        }

        if (result.Priority is { } priority)
        {
            var name = priority switch
            {
                MessagePriority.High => _localizer["Priority_High"],
                MessagePriority.Low => _localizer["Priority_Low"],
                _ => _localizer["Priority_Normal"],
            };
            suggestions.Add(new SuggestionViewModel(_localizer.Format("Classify_Priority", name), null, priority));
        }

        item.Suggestions = suggestions;
        if (result.Failure is { } failure)
        {
            item.StatusText = AiFailureText.Describe(_localizer, failure);
        }
        else
        {
            item.StatusText = suggestions.Count == 0 ? _localizer["Classify_NoSuggestion"] : null;
        }

        _done++;
        UpdateProgress();
    }

    /// <summary>
    /// Describes how far the request got.
    /// </summary>
    private void UpdateProgress()
    {
        ProgressText = _localizer.Format("Classify_Progress", _done, Items.Count);
    }
}
