using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the new mail pop-up: the newest of the announced messages and how many there are.
/// </summary>
public sealed partial class NewMailViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The newest message announced so far.
    /// </summary>
    private NewMailSummary _latest;

    /// <summary>
    /// The address of the account the newest message arrived in.
    /// </summary>
    private string _accountAddress;

    /// <summary>
    /// The number of messages announced so far.
    /// </summary>
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewMailViewModel"/> class.
    /// </summary>
    /// <param name="arrival">The first arrived mail.</param>
    /// <param name="localizer">The localizer.</param>
    public NewMailViewModel(NewMailEventArgs arrival, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        ArgumentNullException.ThrowIfNull(localizer);
        _localizer = localizer;
        _latest = Newest(arrival);
        _accountAddress = arrival.AccountAddress;
        _count = arrival.Messages.Count;
        localizer.LanguageChanged += OnLanguageChanged;
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
    /// Gets the heading, which says how many messages arrived.
    /// </summary>
    public string Title => _count == 1
        ? _localizer["Notification_NewMessage"]
        : _localizer.Format("Notification_NewMessages", _count);

    /// <summary>
    /// Gets the sender of the newest message.
    /// </summary>
    public string Sender => string.IsNullOrWhiteSpace(_latest.FromName) ? _latest.FromAddress : _latest.FromName;

    /// <summary>
    /// Gets the subject of the newest message.
    /// </summary>
    public string Subject => string.IsNullOrWhiteSpace(_latest.Subject) ? _localizer["Message_NoSubject"] : _latest.Subject;

    /// <summary>
    /// Gets the account of the newest message and how many further messages are not shown.
    /// </summary>
    public string Footer => _count == 1
        ? _accountAddress
        : _localizer.Format("Notification_More", _accountAddress, _count - 1);

    /// <summary>
    /// Adds mail that arrived while the pop-up was still shown.
    /// </summary>
    /// <param name="arrival">The arrived mail.</param>
    public void Add(NewMailEventArgs arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var newest = Newest(arrival);
        if (newest.SentAt >= _latest.SentAt)
        {
            _latest = newest;
            _accountAddress = arrival.AccountAddress;
        }

        _count += arrival.Messages.Count;
        RefreshTexts();
    }

    /// <summary>
    /// Stops following the language.
    /// </summary>
    public void Dispose()
    {
        _localizer.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    /// Finds the most recently sent message of an arrival.
    /// </summary>
    /// <param name="arrival">The arrived mail.</param>
    /// <returns>The message.</returns>
    private static NewMailSummary Newest(NewMailEventArgs arrival)
    {
        return arrival.Messages.MaxBy(m => m.SentAt)!;
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
        OnPropertyChanged(nameof(Footer));
    }
}
