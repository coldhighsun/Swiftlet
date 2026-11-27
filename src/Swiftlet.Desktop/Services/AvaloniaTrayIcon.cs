using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
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
    /// The image with a red dot, shown while there is unread mail.
    /// </summary>
    private static readonly Uri UnreadIconUri = new("avares://Swiftlet.Desktop/Assets/swiftlet-unread.ico");

    /// <summary>
    /// How long each of the two pictures stays while blinking.
    /// </summary>
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(800);

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
    /// Switches the picture while blinking; runs only then.
    /// </summary>
    private readonly DispatcherTimer _blink = new() { Interval = BlinkInterval };

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

        NormalImage = LoadImage(IconUri);
        UnreadImage = LoadImage(UnreadIconUri);
        Icon = new TrayIcon
        {
            Icon = NormalImage,
            ToolTipText = viewModel.ToolTipText,
            Menu = menu,

            // Runs on a click on the icon; macOS opens the menu instead.
            Command = viewModel.OpenCommand,
        };
        TrayIcon.SetIcons(application, [Icon]);
        _blink.Tick += (_, _) => Blink();
        ApplyIndicator();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Gets the tray icon.
    /// </summary>
    internal TrayIcon Icon { get; }

    /// <summary>
    /// Gets the picture without a dot.
    /// </summary>
    internal WindowIcon NormalImage { get; }

    /// <summary>
    /// Gets the picture with the red dot.
    /// </summary>
    internal WindowIcon UnreadImage { get; }

    /// <summary>
    /// Gets a value indicating whether the icon is blinking.
    /// </summary>
    internal bool IsBlinking => _blink.IsEnabled;

    /// <summary>
    /// Removes the icon from the tray.
    /// </summary>
    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _blink.Stop();
        TrayIcon.SetIcons(_application, []);
        Icon.Dispose();
    }

    /// <summary>
    /// Switches to the other picture; the timer calls this while blinking.
    /// </summary>
    internal void Blink()
    {
        Icon.Icon = ReferenceEquals(Icon.Icon, UnreadImage) ? NormalImage : UnreadImage;
    }

    /// <summary>
    /// Reads one of the pictures of the application.
    /// </summary>
    /// <param name="uri">The asset.</param>
    /// <returns>The picture.</returns>
    private static WindowIcon LoadImage(Uri uri)
    {
        using var image = AssetLoader.Open(uri);
        return new WindowIcon(image);
    }

    /// <summary>
    /// Shows the picture that matches the view model and starts or stops blinking.
    /// </summary>
    private void ApplyIndicator()
    {
        _blink.Stop();
        Icon.Icon = _viewModel.Indicator == TrayIndicator.None ? NormalImage : UnreadImage;
        if (_viewModel.Indicator == TrayIndicator.Blink)
        {
            _blink.Start();
        }
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
    /// Copies a changed picture or text to the icon or its menu.
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

        // Raised only when the value really changes, so further unread mail does not restart the blinking.
        if (e.PropertyName == nameof(TrayViewModel.Indicator))
        {
            ApplyIndicator();
            return;
        }

        foreach (var (item, text) in _items)
        {
            item.Header = text();
        }
    }
}
