using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the new mail pop-up: a queue of the announced messages, shown one at a time, with actions that
/// mark the shown message as read and move on to the next one.
/// </summary>
public sealed partial class NewMailViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Runs the actions.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Told after an action changed a cached message.
    /// </summary>
    private readonly Action _changed;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// The messages still to be handled; never empty until the pop-up asks to close.
    /// </summary>
    private readonly List<Entry> _queue = [];

    /// <summary>
    /// The position of the shown message in the queue.
    /// </summary>
    private int _index;

    /// <summary>
    /// Counts the look-ups of possible actions, so a late answer about an earlier message is dropped.
    /// </summary>
    private int _availabilityVersion;

    /// <summary>
    /// Whether an action failed on the shown message.
    /// </summary>
    private bool _failed;

    /// <summary>
    /// Whether an action is running; every action and the navigation wait for it.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkReadCommand))]
    [NotifyCanExecuteChangedFor(nameof(StarCommand))]
    [NotifyCanExecuteChangedFor(nameof(ArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _isBusy;

    /// <summary>
    /// The possible actions for the shown message, or <see langword="null"/> while they are being looked up.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(ArchiveHint))]
    [NotifyPropertyChangedFor(nameof(DeleteHint))]
    private QuickActionAvailability? _availability;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewMailViewModel"/> class.
    /// </summary>
    /// <param name="arrival">The first arrived mail.</param>
    /// <param name="dispatcher">Runs the actions.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="changed">Told after an action changed a cached message.</param>
    /// <param name="logger">The logger; optional.</param>
    public NewMailViewModel(
        NewMailEventArgs arrival,
        IRequestDispatcher dispatcher,
        ILocalizer localizer,
        Action changed,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(changed);
        _dispatcher = dispatcher;
        _localizer = localizer;
        _changed = changed;
        _logger = logger ?? NullLogger.Instance;
        Enqueue(arrival);
        localizer.LanguageChanged += OnLanguageChanged;
        AvailabilityLoad = LoadAvailabilityAsync();
    }

    /// <summary>
    /// Raised when the user wants to see the mail.
    /// </summary>
    public event EventHandler? OpenRequested;

    /// <summary>
    /// Raised when the pop-up should disappear.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised after the user acted or moved to another message and the pop-up stays, so it should stay a while longer.
    /// </summary>
    public event EventHandler? Interacted;

    /// <summary>
    /// Gets the latest look-up of the possible actions.
    /// </summary>
    internal Task AvailabilityLoad { get; private set; }

    /// <summary>
    /// Gets the heading, which says how many messages are still to be handled.
    /// </summary>
    public string Title => _queue.Count <= 1
        ? _localizer["Notification_NewMessage"]
        : _localizer.Format("Notification_NewMessages", _queue.Count);

    /// <summary>
    /// Gets the sender of the shown message.
    /// </summary>
    public string Sender => Current is not { } entry
        ? string.Empty
        : string.IsNullOrWhiteSpace(entry.Message.FromName) ? entry.Message.FromAddress : entry.Message.FromName;

    /// <summary>
    /// Gets the subject of the shown message.
    /// </summary>
    public string Subject => Current is not { } entry
        ? string.Empty
        : string.IsNullOrWhiteSpace(entry.Message.Subject) ? _localizer["Message_NoSubject"] : entry.Message.Subject;

    /// <summary>
    /// Gets the beginning of the text of the shown message.
    /// </summary>
    public string Preview => Current?.Message.Preview ?? string.Empty;

    /// <summary>
    /// Gets the account of the shown message.
    /// </summary>
    public string AccountAddress => Current?.AccountAddress ?? string.Empty;

    /// <summary>
    /// Gets the place of the shown message among those still to be handled.
    /// </summary>
    public string Position => _queue.Count == 0 ? string.Empty : _localizer.Format("Notification_Position", _index + 1, _queue.Count);

    /// <summary>
    /// Gets a value indicating whether there is more than one message to move between.
    /// </summary>
    public bool HasSeveral => _queue.Count > 1;

    /// <summary>
    /// Gets what the archive button says, including why it is disabled.
    /// </summary>
    public string ArchiveHint => Availability is { CanArchive: false }
        ? _localizer["Notification_NoArchive"]
        : _localizer["Toolbar_Archive"];

    /// <summary>
    /// Gets what the delete button says, including why it is disabled.
    /// </summary>
    public string DeleteHint => Availability is { CanDelete: false }
        ? _localizer["Notification_NoTrash"]
        : _localizer["Toolbar_Delete"];

    /// <summary>
    /// Gets what went wrong with the last action on the shown message, if anything.
    /// </summary>
    public string? Error => _failed ? _localizer["Notification_ActionFailed"] : null;

    /// <summary>
    /// Gets a value indicating whether <see cref="Error"/> is to be shown.
    /// </summary>
    public bool HasError => _failed;

    /// <summary>
    /// Gets the shown message, if any is left.
    /// </summary>
    private Entry? Current => _index < _queue.Count ? _queue[_index] : null;

    /// <summary>
    /// Adds mail that arrived while the pop-up was still shown. It joins the end of the queue, so the shown message never
    /// changes under the pointer.
    /// </summary>
    /// <param name="arrival">The arrived mail.</param>
    public void Add(NewMailEventArgs arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var empty = _queue.Count == 0;
        Enqueue(arrival);
        RefreshTexts();
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        if (empty)
        {
            AvailabilityLoad = LoadAvailabilityAsync();
        }
    }

    /// <summary>
    /// Stops following the language.
    /// </summary>
    public void Dispose()
    {
        _localizer.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    /// Appends the messages of an arrival, newest first.
    /// </summary>
    /// <param name="arrival">The arrived mail.</param>
    private void Enqueue(NewMailEventArgs arrival)
    {
        foreach (var message in arrival.Messages.OrderByDescending(m => m.SentAt))
        {
            // The same message can be announced again, for example after its folder was emptied and refilled.
            if (!_queue.Any(e => e.Message.MessageId == message.MessageId))
            {
                _queue.Add(new Entry(arrival.AccountId, arrival.AccountAddress, message));
            }
        }
    }

    /// <summary>
    /// Marks the shown message as read.
    /// </summary>
    /// <returns>A task that completes when the next message is shown.</returns>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task MarkReadAsync()
    {
        return ActAsync(QuickMailAction.MarkRead);
    }

    /// <summary>
    /// Stars the shown message.
    /// </summary>
    /// <returns>A task that completes when the next message is shown.</returns>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task StarAsync()
    {
        return ActAsync(QuickMailAction.Star);
    }

    /// <summary>
    /// Archives the shown message.
    /// </summary>
    /// <returns>A task that completes when the next message is shown.</returns>
    [RelayCommand(CanExecute = nameof(CanArchive))]
    private Task ArchiveAsync()
    {
        return ActAsync(QuickMailAction.Archive);
    }

    /// <summary>
    /// Moves the shown message to Trash.
    /// </summary>
    /// <returns>A task that completes when the next message is shown.</returns>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync()
    {
        return ActAsync(QuickMailAction.Delete);
    }

    /// <summary>
    /// Determines whether an action can start.
    /// </summary>
    /// <returns><see langword="true"/> if a message is shown and no action is running.</returns>
    private bool CanAct()
    {
        return !IsBusy && Current is not null;
    }

    /// <summary>
    /// Determines whether the shown message can be archived.
    /// </summary>
    /// <returns><see langword="true"/> if so.</returns>
    private bool CanArchive()
    {
        return CanAct() && Availability is { CanArchive: true };
    }

    /// <summary>
    /// Determines whether the shown message can be moved to Trash.
    /// </summary>
    /// <returns><see langword="true"/> if so.</returns>
    private bool CanDelete()
    {
        return CanAct() && Availability is { CanDelete: true };
    }

    /// <summary>
    /// Shows the message before the shown one, which stays unread.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Previous()
    {
        Show(_index - 1);
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows the message after the shown one, which stays unread.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Next()
    {
        Show(_index + 1);
        Interacted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Determines whether there is a message before the shown one.
    /// </summary>
    /// <returns><see langword="true"/> if so.</returns>
    private bool CanGoBack()
    {
        return !IsBusy && _index > 0;
    }

    /// <summary>
    /// Determines whether there is a message after the shown one.
    /// </summary>
    /// <returns><see langword="true"/> if so.</returns>
    private bool CanGoForward()
    {
        return !IsBusy && _index < _queue.Count - 1;
    }

    /// <summary>
    /// Applies an action to the shown message and moves on unless the message has to stay.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>A task that completes when the pop-up shows the outcome.</returns>
    private async Task ActAsync(QuickMailAction action)
    {
        if (Current is not { } entry)
        {
            return;
        }

        IsBusy = true;
        SetFailed(false);
        try
        {
            var command = new QuickMailActionCommand(entry.AccountId, entry.Message.MessageId, entry.Message.FolderId, action);

            // Database work completes synchronously, so it is kept off the UI thread.
            var result = await Task.Run(() => _dispatcher.SendAsync(command));
            if (result == QuickMailActionResult.Unavailable)
            {
                // The account changed since the buttons were enabled.
                AvailabilityLoad = LoadAvailabilityAsync();
                return;
            }

            if (result == QuickMailActionResult.Done)
            {
                _changed();
            }

            Remove(entry);
        }
#pragma warning disable CA1031 // The UI boundary of a button: the failure is shown in the pop-up and the message stays.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "The action {Action} of the new mail pop-up failed", action);
            SetFailed(true);
        }
        finally
        {
            IsBusy = false;
            if (_queue.Count > 0)
            {
                Interacted?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Takes a handled message out of the queue and shows its successor, or asks to close after the last one.
    /// </summary>
    /// <param name="entry">The handled message.</param>
    private void Remove(Entry entry)
    {
        _queue.Remove(entry);
        if (_queue.Count == 0)
        {
            // The texts stay as they are, so the closing window does not flash empty.
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Show(Math.Min(_index, _queue.Count - 1));
    }

    /// <summary>
    /// Shows the message at a position of the queue.
    /// </summary>
    /// <param name="index">The position.</param>
    private void Show(int index)
    {
        _index = index;
        _failed = false;
        RefreshTexts();
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        AvailabilityLoad = LoadAvailabilityAsync();
    }

    /// <summary>
    /// Looks up which actions are possible for the shown message; the buttons stay disabled until the answer arrives.
    /// </summary>
    /// <returns>A task that completes when the buttons are up to date.</returns>
    private async Task LoadAvailabilityAsync()
    {
        var version = ++_availabilityVersion;
        Availability = null;
        if (Current is not { } entry)
        {
            return;
        }

        try
        {
            var query = new GetQuickActionsQuery(entry.AccountId, entry.Message.FolderId);
            var answer = await Task.Run(() => _dispatcher.SendAsync(query));
            if (version == _availabilityVersion)
            {
                Availability = answer;
            }
        }
#pragma warning disable CA1031 // Nobody awaits this look-up; without an answer the two buttons stay disabled.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "The actions of the new mail pop-up could not be looked up");
        }
    }

    /// <summary>
    /// Shows or hides the failure line.
    /// </summary>
    /// <param name="failed">Whether the last action failed.</param>
    private void SetFailed(bool failed)
    {
        _failed = failed;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>
    /// Asks to show the mail.
    /// </summary>
    [RelayCommand]
    private void Open()
    {
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Asks to close the pop-up.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-reads every text after the language changed.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshTexts();
    }

    /// <summary>
    /// Announces that every text may have changed.
    /// </summary>
    private void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Sender));
        OnPropertyChanged(nameof(Subject));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(AccountAddress));
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(HasSeveral));
        OnPropertyChanged(nameof(ArchiveHint));
        OnPropertyChanged(nameof(DeleteHint));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>
    /// A message waiting in the pop-up.
    /// </summary>
    /// <param name="AccountId">The account it arrived in.</param>
    /// <param name="AccountAddress">The address of that account.</param>
    /// <param name="Message">What is shown of it.</param>
    private sealed record Entry(Guid AccountId, string AccountAddress, NewMailSummary Message);
}
