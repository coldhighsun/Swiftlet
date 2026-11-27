using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using Swiftlet.Desktop.ViewModels;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// The icon of the application in the system tray, with its menu.
/// </summary>
public sealed class AvaloniaTrayIcon : IDisposable
{
    /// <summary>
    /// The image shown in the tray.
    /// </summary>
    private static readonly Uri IconUri = new("avares://Swiftlet.Desktop/Assets/swiftlet.ico");

    /// <summary>
    /// The application the icon is attached to.
    /// </summary>
    private readonly Avalonia.Application _application;

    /// <summary>
    /// The texts and commands of the icon.
    /// </summary>
    private readonly TrayViewModel _viewModel;

    /// <summary>
    /// The menu items whose text follows the language, with the way to read that text.
    /// </summary>
    private readonly List<(NativeMenuItem Item, Func<string> Text)> _items = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AvaloniaTrayIcon"/> class and shows the icon.
    /// </summary>
    /// <param name="application">The running application.</param>
    /// <param name="viewModel">The texts and commands of the icon.</param>
    public AvaloniaTrayIcon(Avalonia.Application application, TrayViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(viewModel);
        _application = application;
        _viewModel = viewModel;

        var menu = new NativeMenu();
        menu.Items.Add(CreateItem(() => viewModel.OpenText, viewModel.OpenCommand));
        menu.Items.Add(CreateItem(() => viewModel.ComposeText, viewModel.ComposeCommand));
        menu.Items.Add(CreateItem(() => viewModel.SyncAllText, viewModel.SyncAllCommand));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateItem(() => viewModel.SettingsText, viewModel.SettingsCommand));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateItem(() => viewModel.ExitText, viewModel.ExitCommand));

        using var image = AssetLoader.Open(IconUri);
        Icon = new TrayIcon
        {
            Icon = new WindowIcon(image),
            ToolTipText = viewModel.ToolTipText,
            Menu = menu,

            // Runs on a click on the icon; macOS opens the menu instead.
            Command = viewModel.OpenCommand,
        };
        TrayIcon.SetIcons(application, [Icon]);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Gets the tray icon.
    /// </summary>
    internal TrayIcon Icon { get; }

    /// <summary>
    /// Removes the icon from the tray.
    /// </summary>
    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        TrayIcon.SetIcons(_application, []);
        Icon.Dispose();
    }

    /// <summary>
    /// Creates a menu item.
    /// </summary>
    /// <param name="text">Reads the localized text.</param>
    /// <param name="command">What the item does.</param>
    /// <returns>The item.</returns>
    private NativeMenuItem CreateItem(Func<string> text, System.Windows.Input.ICommand command)
    {
        var item = new NativeMenuItem(text()) { Command = command };
        _items.Add((item, text));
        return item;
    }

    /// <summary>
    /// Copies a changed text to the icon or its menu.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrayViewModel.ToolTipText))
        {
            Icon.ToolTipText = _viewModel.ToolTipText;
            return;
        }

        foreach (var (item, text) in _items)
        {
            item.Header = text();
        }
    }
}
