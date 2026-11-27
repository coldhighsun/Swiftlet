using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the tray icon: its tooltip, the texts of its menu and what the menu does.
/// </summary>
public sealed partial class TrayViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The main window's view model, which holds the unread counts and does the mail work.
    /// </summary>
    private readonly MainWindowViewModel _main;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Shows the main window and brings it to the front.
    /// </summary>
    private readonly Action _showWindow;

    /// <summary>
    /// Ends the application.
    /// </summary>
    private readonly Action _quit;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayViewModel"/> class.
    /// </summary>
    /// <param name="main">The main window's view model.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="showWindow">Shows the main window and brings it to the front.</param>
    /// <param name="quit">Ends the application.</param>
    public TrayViewModel(MainWindowViewModel main, ILocalizer localizer, Action showWindow, Action quit)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(localizer);
        _main = main;
        _localizer = localizer;
        _showWindow = showWindow;
        _quit = quit;

        // Until the accounts are loaded only the name is shown, rather than claiming there are none.
        ToolTipText = "Swiftlet";
        if (main.Accounts.Count > 0)
        {
            UpdateToolTip();
        }

        main.UnreadChanged += OnUnreadChanged;
        localizer.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Gets or sets the text shown when the pointer rests on the icon.
    /// </summary>
    [ObservableProperty]
    public partial string ToolTipText { get; set; }

    /// <summary>
    /// Gets the text of the menu item that shows the main window.
    /// </summary>
    public string OpenText => _localizer["Tray_Open"];

    /// <summary>
    /// Gets the text of the menu item that writes a new message.
    /// </summary>
    public string ComposeText => _localizer["Tray_Compose"];

    /// <summary>
    /// Gets the text of the menu item that synchronizes all accounts.
    /// </summary>
    public string SyncAllText => _localizer["Tray_SyncAll"];

    /// <summary>
    /// Gets the text of the menu item that opens the settings.
    /// </summary>
    public string SettingsText => _localizer["Tray_Settings"];

    /// <summary>
    /// Gets the text of the menu item that ends the application.
    /// </summary>
    public string ExitText => _localizer["Tray_Exit"];

    /// <summary>
    /// Stops following the unread counts and the language.
    /// </summary>
    public void Dispose()
    {
        _main.UnreadChanged -= OnUnreadChanged;
        _localizer.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    /// Shows the main window.
    /// </summary>
    [RelayCommand]
    private void Open()
    {
        _showWindow();
    }

    /// <summary>
    /// Opens a compose window; the main window stays where it is.
    /// </summary>
    /// <returns>A task that completes when the window has been opened.</returns>
    [RelayCommand]
    private async Task ComposeAsync()
    {
        await _main.ComposeCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Synchronizes all accounts, unless that is already running.
    /// </summary>
    /// <returns>A task that completes when the sync is done.</returns>
    [RelayCommand]
    private async Task SyncAllAsync()
    {
        if (_main.SyncAllCommand.CanExecute(null))
        {
            await _main.SyncAllCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Opens the settings window; the main window stays where it is.
    /// </summary>
    /// <returns>A task that completes when the request has been handled.</returns>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SettingsAsync()
    {
        await _main.OpenSettingsCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Ends the application.
    /// </summary>
    [RelayCommand]
    private void Exit()
    {
        _quit();
    }

    /// <summary>
    /// Rebuilds the tooltip after the unread counts changed.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnUnreadChanged(object? sender, EventArgs e)
    {
        UpdateToolTip();
    }

    /// <summary>
    /// Re-reads every text after the language changed.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(OpenText));
        OnPropertyChanged(nameof(ComposeText));
        OnPropertyChanged(nameof(SyncAllText));
        OnPropertyChanged(nameof(SettingsText));
        OnPropertyChanged(nameof(ExitText));
        UpdateToolTip();
    }

    /// <summary>
    /// Describes the unread mail in the inbox of every account.
    /// </summary>
    private void UpdateToolTip()
    {
        var accounts = _main.Accounts
            .Select(a => (
                a.Account.EmailAddress,
                a.Folders.Where(f => f.Folder.Role == FolderRole.Inbox).Sum(f => f.UnreadCount)))
            .ToList();
        ToolTipText = TraySummary.Format(accounts, _localizer);
    }
}
