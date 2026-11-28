using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Messages;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// A selectable synchronization interval.
/// </summary>
/// <param name="Interval">The interval.</param>
/// <param name="Text">The text shown in the list.</param>
public sealed record SyncIntervalOption(TimeSpan Interval, string Text);

/// <summary>
/// A selectable archive folder.
/// </summary>
/// <param name="FullName">The full name of the folder, or <see langword="null"/> for the automatic choice.</param>
/// <param name="Text">The text shown in the list.</param>
public sealed record ArchiveFolderOption(string? FullName, string Text);

/// <summary>
/// A selectable time after which mail in Trash is deleted permanently.
/// </summary>
/// <param name="Days">The number of days, or <see langword="null"/> to keep the mail.</param>
/// <param name="Text">The text shown in the list.</param>
public sealed record TrashRetentionOption(int? Days, string Text);

/// <summary>
/// The view model of the account settings dialog. <see cref="Load"/> must be called before the dialog is shown.
/// </summary>
public sealed partial class EditAccountViewModel : ObservableObject
{
    /// <summary>
    /// The intervals offered in the list, in minutes.
    /// </summary>
    private static readonly int[] IntervalMinutes = [1, 2, 5, 10, 15, 30, 60];

    /// <summary>
    /// The Trash retention periods offered in the list, in days.
    /// </summary>
    private static readonly int[] RetentionDays = [7, 15, 30, 90];

    /// <summary>
    /// Asks before saving something that deletes mail.
    /// </summary>
    private readonly IDialogService _dialogs;

    /// <summary>
    /// The Trash retention the account had when the dialog opened.
    /// </summary>
    private int? _loadedRetention;

    /// <summary>
    /// The request dispatcher.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The color the account had when the dialog opened; only a different one is saved.
    /// </summary>
    private string? _loadedColor;

    /// <summary>
    /// The identifier of the edited account.
    /// </summary>
    private Guid _accountId;

    /// <summary>
    /// Cancels the pending save, if any.
    /// </summary>
    private CancellationTokenSource? _saveCancellation;

    /// <summary>
    /// Initializes a new instance of the <see cref="EditAccountViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="dialogs">The dialog service.</param>
    /// <param name="localizer">The localizer.</param>
    public EditAccountViewModel(IRequestDispatcher dispatcher, IDialogService dialogs, ILocalizer localizer)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _localizer = localizer;
        Email = string.Empty;
        DisplayName = string.Empty;
        DisplayNamePlaceholder = string.Empty;
        IntervalOptions = [];
        ArchiveFolderOptions = [];
        TrashRetentionOptions = [];
        TrashRetentionHint = string.Empty;
        Password = string.Empty;
        ImapHost = string.Empty;
        ImapPort = string.Empty;
        SmtpHost = string.Empty;
        SmtpPort = string.Empty;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// Raised when the dialog should close; the argument is <see langword="null"/> if nothing was saved.
    /// </summary>
    public event EventHandler<UpdateAccountResult?>? CloseRequested;

    /// <summary>
    /// Gets or sets the address of the account, which cannot be changed.
    /// </summary>
    [ObservableProperty]
    public partial string Email { get; set; }

    /// <summary>
    /// Gets or sets the name typed by the user; empty if the account has none.
    /// </summary>
    [ObservableProperty]
    public partial string DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the sender name used while the name box is empty.
    /// </summary>
    [ObservableProperty]
    public partial string DisplayNamePlaceholder { get; set; }

    /// <summary>
    /// Gets or sets the selectable synchronization intervals.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<SyncIntervalOption> IntervalOptions { get; set; }

    /// <summary>
    /// Gets or sets the selected synchronization interval.
    /// </summary>
    [ObservableProperty]
    public partial SyncIntervalOption? SelectedInterval { get; set; }

    /// <summary>
    /// Gets or sets the selectable archive folders; the first entry is the automatic choice.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<ArchiveFolderOption> ArchiveFolderOptions { get; set; }

    /// <summary>
    /// Gets or sets the selected archive folder.
    /// </summary>
    [ObservableProperty]
    public partial ArchiveFolderOption? SelectedArchiveFolder { get; set; }

    /// <summary>
    /// Gets or sets the selectable Trash retention periods; the first entry keeps the mail.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<TrashRetentionOption> TrashRetentionOptions { get; set; }

    /// <summary>
    /// Gets or sets the selected Trash retention period.
    /// </summary>
    [ObservableProperty]
    public partial TrashRetentionOption? SelectedTrashRetention { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account has a Trash folder, without which nothing can be cleaned up.
    /// </summary>
    [ObservableProperty]
    public partial bool HasTrashFolder { get; set; }

    /// <summary>
    /// Gets or sets the explanation shown below the Trash retention list.
    /// </summary>
    [ObservableProperty]
    public partial string TrashRetentionHint { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account signs in with a password, so password and servers can be changed.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPasswordAccount { get; set; }

    /// <summary>
    /// Gets or sets the new password; empty to keep the current one.
    /// </summary>
    [ObservableProperty]
    public partial string Password { get; set; }

    /// <summary>
    /// Gets or sets the IMAP host.
    /// </summary>
    [ObservableProperty]
    public partial string ImapHost { get; set; }

    /// <summary>
    /// Gets or sets the IMAP port as text.
    /// </summary>
    [ObservableProperty]
    public partial string ImapPort { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether IMAP uses implicit TLS (otherwise STARTTLS).
    /// </summary>
    [ObservableProperty]
    public partial bool ImapUseSsl { get; set; }

    /// <summary>
    /// Gets or sets the SMTP host.
    /// </summary>
    [ObservableProperty]
    public partial string SmtpHost { get; set; }

    /// <summary>
    /// Gets or sets the SMTP port as text.
    /// </summary>
    [ObservableProperty]
    public partial string SmtpPort { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether SMTP uses implicit TLS (otherwise STARTTLS).
    /// </summary>
    [ObservableProperty]
    public partial bool SmtpUseSsl { get; set; }

    /// <summary>
    /// Gets or sets the last error to show to the user.
    /// </summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the settings are being verified and saved.
    /// </summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Gets or sets the colors offered: the palette, plus the current color if it is not in it.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ColorOptions { get; set; } = AccountColors.Palette;

    /// <summary>
    /// Gets or sets the color of the account as <c>#RRGGBB</c>.
    /// </summary>
    [ObservableProperty]
    public partial string? Color { get; set; }

    /// <summary>
    /// Fills the dialog with the current settings of an account.
    /// </summary>
    /// <param name="account">The account to edit.</param>
    /// <param name="folders">The cached folders of the account.</param>
    public void Load(MailAccount account, IReadOnlyList<MailFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folders);

        _accountId = account.Id;
        _loadedColor = account.EffectiveColor;
        ColorOptions = AccountColors.Palette.Contains(_loadedColor, StringComparer.OrdinalIgnoreCase)
            ? AccountColors.Palette
            : [.. AccountColors.Palette, _loadedColor];
        Color = _loadedColor;
        Email = account.EmailAddress;
        DisplayName = account.HasCustomDisplayName ? account.DisplayName : string.Empty;
        DisplayNamePlaceholder = account.HasCustomDisplayName ? account.EmailAddress.Split('@')[0] : account.DisplayName;

        var intervals = IntervalMinutes.Select(m => TimeSpan.FromMinutes(m)).Append(account.SyncInterval).Distinct().Order();
        IntervalOptions = [.. intervals.Select(CreateOption)];
        SelectedInterval = IntervalOptions.First(o => o.Interval == account.SyncInterval);

        ArchiveFolderOptions = CreateArchiveOptions(account.ArchiveFolderName, folders);
        SelectedArchiveFolder = ArchiveFolderOptions.First(o => o.FullName == account.ArchiveFolderName);

        _loadedRetention = account.TrashRetentionDays;
        var periods = RetentionDays.Concat(_loadedRetention is { } stored ? [stored] : []).Distinct().Order();
        TrashRetentionOptions =
        [
            new TrashRetentionOption(null, _localizer["TrashRetention_Never"]),
            .. periods.Select(d => new TrashRetentionOption(d, TrashRetentionText.Format(_localizer, d))),
        ];
        SelectedTrashRetention = TrashRetentionOptions.First(o => o.Days == _loadedRetention);
        HasTrashFolder = folders.Any(f => f.Role == FolderRole.Trash);
        TrashRetentionHint = HasTrashFolder
            ? _localizer["EditAccount_TrashRetentionHint"]
            : _localizer["EditAccount_TrashRetentionNoTrash"];

        IsPasswordAccount = account.AuthMethod == AuthMethod.Password;
        ImapHost = account.Imap.Host;
        ImapPort = account.Imap.Port.ToString(CultureInfo.InvariantCulture);
        ImapUseSsl = account.Imap.UseSsl;
        SmtpHost = account.Smtp.Host;
        SmtpPort = account.Smtp.Port.ToString(CultureInfo.InvariantCulture);
        SmtpUseSsl = account.Smtp.UseSsl;
    }

    /// <summary>
    /// Abandons a save that is still verifying the sign-in; called when the dialog closes.
    /// </summary>
    public void CancelPendingSave()
    {
        _saveCancellation?.Cancel();
    }

    /// <summary>
    /// Verifies changed connection settings and saves the account.
    /// </summary>
    /// <returns>A task that completes when the account was saved or the attempt failed.</returns>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;
        ServerEndpoint? imap = null;
        ServerEndpoint? smtp = null;
        if (IsPasswordAccount)
        {
            if (!TryParsePort(ImapPort, out var imapPort) || !TryParsePort(SmtpPort, out var smtpPort))
            {
                ErrorMessage = _localizer["EditAccount_PortInvalid"];
                return;
            }

            imap = new ServerEndpoint(ImapHost.Trim(), imapPort, ImapUseSsl);
            smtp = new ServerEndpoint(SmtpHost.Trim(), smtpPort, SmtpUseSsl);
        }

        // Only a change that deletes mail sooner needs a confirmation.
        var retention = SelectedTrashRetention?.Days;
        if (retention is { } days
            && (_loadedRetention is not { } previous || days < previous)
            && !await _dialogs.ConfirmAsync(
                _localizer["Confirm_TrashRetention_Title"],
                _localizer.Format("Confirm_TrashRetention_Message", Email, days),
                _localizer["Confirm_TrashRetention_Ok"]))
        {
            return;
        }

        var command = new UpdateAccountCommand(
            _accountId,
            DisplayName.Trim(),
            SelectedInterval!.Interval,
            imap,
            smtp,
            IsPasswordAccount && Password.Length > 0 ? Password : null,
            SelectedArchiveFolder?.FullName,
            Color == _loadedColor ? null : Color,
            retention);
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        try
        {
            var result = await _dispatcher.SendAsync(command, cancellation.Token);
            CloseRequested?.Invoke(this, result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The user closed the dialog while the sign-in was being verified; nothing to report.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Verification or saving failed; show the reason inside the dialog so the user can correct the input.
            ErrorMessage = _localizer.Format("EditAccount_SaveFailed", ex.Message);
        }
        finally
        {
            _saveCancellation = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Closes the dialog without saving.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        CancelPendingSave();
        CloseRequested?.Invoke(this, null);
    }

    /// <summary>
    /// Gets a value indicating whether the entered data is sufficient to attempt saving.
    /// </summary>
    private bool CanSave =>
        !IsBusy
        && SelectedInterval is not null
        && (!IsPasswordAccount || (!string.IsNullOrWhiteSpace(ImapHost) && !string.IsNullOrWhiteSpace(SmtpHost)));

    /// <summary>
    /// Re-evaluates the save command whenever an input changes.
    /// </summary>
    /// <param name="e">The change details.</param>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Parses a TCP port.
    /// </summary>
    /// <param name="text">The typed text.</param>
    /// <param name="port">The port, if the text is a valid one.</param>
    /// <returns><see langword="true"/> if the text is a number from 1 to 65535.</returns>
    private static bool TryParsePort(string text, out int port)
    {
        return int.TryParse(text, out port) && port is >= 1 and <= 65535;
    }

    /// <summary>
    /// Creates the archive folder list: the automatic choice, then every folder that can serve as an archive. The saved
    /// folder is always listed, marked if it no longer exists, so saving other settings does not lose it.
    /// </summary>
    /// <param name="saved">The saved folder name, or <see langword="null"/> for automatic.</param>
    /// <param name="folders">The cached folders of the account.</param>
    /// <returns>The entries.</returns>
    private List<ArchiveFolderOption> CreateArchiveOptions(string? saved, IReadOnlyList<MailFolder> folders)
    {
        var automatic = ArchiveFolderResolver.Automatic(folders);
        var automaticText = automatic is null
            ? _localizer["EditAccount_ArchiveAutomaticNone"]
            : _localizer.Format("EditAccount_ArchiveAutomatic", automatic.FullName);

        List<ArchiveFolderOption> options = [new(null, automaticText)];
        options.AddRange(folders
            .Where(f => f.FullName == saved
                || f.Role is not (FolderRole.Inbox or FolderRole.Drafts or FolderRole.Junk or FolderRole.Trash))
            .Select(f => f.FullName)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(name => new ArchiveFolderOption(name, name)));
        if (saved is not null && options.All(o => o.FullName != saved))
        {
            options.Add(new ArchiveFolderOption(saved, _localizer.Format("EditAccount_ArchiveMissing", saved)));
        }

        return options;
    }

    /// <summary>
    /// Creates the list entry of an interval.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The entry.</returns>
    private SyncIntervalOption CreateOption(TimeSpan interval)
    {
        return new SyncIntervalOption(interval, SyncIntervalText.Format(_localizer, interval));
    }
}
