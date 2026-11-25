using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Accounts;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the add-account dialog.
/// </summary>
public sealed partial class AddAccountViewModel : ObservableObject
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
    /// Whether the advanced section was opened automatically because the provider is unknown.
    /// </summary>
    private bool _advancedOpenedAutomatically;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddAccountViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="localizer">The localizer.</param>
    public AddAccountViewModel(IRequestDispatcher dispatcher, ILocalizer localizer)
    {
        _dispatcher = dispatcher;
        _localizer = localizer;
        DisplayName = string.Empty;
        Email = string.Empty;
        Password = string.Empty;
        ImapHost = string.Empty;
        ImapPort = "993";
        ImapUseSsl = true;
        SmtpHost = string.Empty;
        SmtpPort = "465";
        SmtpUseSsl = true;
        ProviderHint = string.Empty;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// Raised when the dialog should close; the argument is <see langword="true"/> if an account was added.
    /// </summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [ObservableProperty]
    public partial string DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the email address.
    /// </summary>
    [ObservableProperty]
    public partial string Email { get; set; }

    /// <summary>
    /// Gets or sets the password or app-specific password.
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
    /// Gets or sets a value indicating whether the server settings are shown.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowAdvanced { get; set; }

    /// <summary>
    /// Gets or sets the hint about the detected provider.
    /// </summary>
    [ObservableProperty]
    public partial string ProviderHint { get; set; }

    /// <summary>
    /// Gets or sets the last error to show to the user.
    /// </summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is being verified.
    /// </summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Verifies the login and adds the account.
    /// </summary>
    /// <returns>A task that completes when the account was added or the attempt failed.</returns>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        ErrorMessage = string.Empty;
        if (!int.TryParse(ImapPort, out var imapPort) || !int.TryParse(SmtpPort, out var smtpPort))
        {
            ErrorMessage = _localizer["AddAccount_PortMustBeNumber"];
            ShowAdvanced = true;
            return;
        }

        IsBusy = true;
        try
        {
            var email = Email.Trim();
            var name = string.IsNullOrWhiteSpace(DisplayName) ? email[..email.IndexOf('@')] : DisplayName.Trim();
            var command = new AddAccountCommand(
                name,
                email,
                Password,
                new ServerEndpoint(ImapHost.Trim(), imapPort, ImapUseSsl),
                new ServerEndpoint(SmtpHost.Trim(), smtpPort, SmtpUseSsl));
            await _dispatcher.SendAsync(command);
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Login or validation failures are shown inside the dialog so the user can correct the input.
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Closes the dialog without adding an account.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, false);
    }

    /// <summary>
    /// Toggles the visibility of the server settings.
    /// </summary>
    [RelayCommand]
    private void ToggleAdvanced()
    {
        ShowAdvanced = !ShowAdvanced;
        _advancedOpenedAutomatically = false;
    }

    /// <summary>
    /// Gets a value indicating whether the entered data is sufficient to attempt adding the account.
    /// </summary>
    private bool CanAdd =>
        !IsBusy
        && Email.Contains('@', StringComparison.Ordinal)
        && !string.IsNullOrEmpty(Password)
        && !string.IsNullOrWhiteSpace(ImapHost)
        && !string.IsNullOrWhiteSpace(SmtpHost);

    /// <summary>
    /// Re-evaluates the add command whenever an input changes and applies provider presets on email edits.
    /// </summary>
    /// <param name="e">The change details.</param>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Email))
        {
            ApplyPreset();
        }

        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Fills the server settings from the provider preset of the entered email address, or reveals them for unknown providers.
    /// </summary>
    private void ApplyPreset()
    {
        if (ProviderPresets.TryGet(Email, out var preset))
        {
            ImapHost = preset.Imap.Host;
            ImapPort = preset.Imap.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ImapUseSsl = preset.Imap.UseSsl;
            SmtpHost = preset.Smtp.Host;
            SmtpPort = preset.Smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SmtpUseSsl = preset.Smtp.UseSsl;
            ProviderHint = preset.RequiresAppPassword
                ? _localizer.Format("AddAccount_HintAppPassword", preset.Name)
                : preset.Name;
            if (_advancedOpenedAutomatically)
            {
                ShowAdvanced = false;
                _advancedOpenedAutomatically = false;
            }

            return;
        }

        var domainLooksComplete = Email.Contains('@', StringComparison.Ordinal) && Email[(Email.IndexOf('@', StringComparison.Ordinal) + 1)..].Contains('.', StringComparison.Ordinal);
        if (!domainLooksComplete)
        {
            ProviderHint = string.Empty;
            return;
        }

        ProviderHint = _localizer["AddAccount_HintUnknown"];
        if (!ShowAdvanced)
        {
            ShowAdvanced = true;
            _advancedOpenedAutomatically = true;
        }
    }
}
