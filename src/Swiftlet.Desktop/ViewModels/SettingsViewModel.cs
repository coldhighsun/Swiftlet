using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the settings window; every change is saved immediately.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>
    /// The settings store.
    /// </summary>
    private readonly IAppSettings _settings;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="localizer">The localizer.</param>
    public SettingsViewModel(IAppSettings settings, ILocalizer localizer)
    {
        _settings = settings;
        _localizer = localizer;
        RemoveServerDeletedMessages = settings.RemoveServerDeletedMessages;
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Gets or sets a value indicating whether messages deleted on the server are removed from the local cache.
    /// </summary>
    [ObservableProperty]
    public partial bool RemoveServerDeletedMessages { get; set; }

    /// <summary>
    /// Gets or sets the localized error of the last failed save, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnRemoveServerDeletedMessagesChanged(bool value)
    {
        if (value == _settings.RemoveServerDeletedMessages)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetRemoveServerDeletedMessages(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            RemoveServerDeletedMessages = _settings.RemoveServerDeletedMessages;
        }
    }

    /// <summary>
    /// Asks the window to close.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
