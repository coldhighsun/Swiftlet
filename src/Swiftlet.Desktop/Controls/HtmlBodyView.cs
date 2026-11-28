using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Html;

namespace Swiftlet.Desktop.Controls;

/// <summary>
/// Shows a prepared HTML mail document (see <see cref="HtmlMailDocument"/>) in the platform web view. Navigation
/// away from the document is blocked; web and mail links open in the system's default handler instead.
/// </summary>
public sealed class HtmlBodyView : Border
{
    /// <summary>
    /// Identifies the <see cref="Document"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> DocumentProperty =
        AvaloniaProperty.Register<HtmlBodyView, string?>(nameof(Document));

    /// <summary>
    /// Identifies the inherited attached property that makes every view below an element give up its web view.
    /// </summary>
    public static readonly AttachedProperty<bool> IsReleasedProperty =
        AvaloniaProperty.RegisterAttached<HtmlBodyView, Control, bool>("IsReleased", inherits: true);

    /// <summary>
    /// The embedded web view, or <see langword="null"/> while it is released.
    /// </summary>
    private NativeWebView? _webView;

    /// <summary>
    /// Whether the platform adapter behind the web view exists, so documents can be loaded.
    /// </summary>
    private bool _adapterReady;

    /// <summary>
    /// The document last handed to the web view; an identical document is not loaded again, which would make the page flash.
    /// </summary>
    private string? _loadedDocument;

    /// <summary>
    /// Initializes static members of the <see cref="HtmlBodyView"/> class.
    /// </summary>
    static HtmlBodyView()
    {
        DocumentProperty.Changed.AddClassHandler<HtmlBodyView>((view, _) => view.LoadDocument());
        IsReleasedProperty.Changed.AddClassHandler<HtmlBodyView>((view, _) => view.UpdateWebView());
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtmlBodyView"/> class.
    /// </summary>
    public HtmlBodyView()
    {
        UpdateWebView();
    }

    /// <summary>
    /// Gets a value indicating whether a web view exists at the moment.
    /// </summary>
    internal bool HasWebView => _webView is not null;

    /// <summary>
    /// Releases the web views below an element, which ends the browser processes behind them, or creates them again.
    /// </summary>
    /// <param name="element">The element, usually a window.</param>
    /// <param name="value">Whether the web views are released.</param>
    public static void SetIsReleased(Control element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsReleasedProperty, value);
    }

    /// <summary>
    /// Gets or sets the complete HTML document to display.
    /// </summary>
    public string? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>
    /// Creates or drops the web view, following <see cref="IsReleasedProperty"/>.
    /// </summary>
    private void UpdateWebView()
    {
        var released = GetValue(IsReleasedProperty);
        if (!released && _webView is null)
        {
            var webView = new NativeWebView();
            webView.EnvironmentRequested += OnEnvironmentRequested;
            webView.AdapterCreated += OnAdapterCreated;
            webView.AdapterDestroyed += OnAdapterDestroyed;
            webView.NavigationStarted += OnNavigationStarted;
            webView.NewWindowRequested += OnNewWindowRequested;
            _webView = webView;
            Child = webView;
        }
        else if (released && _webView is { } webView)
        {
            webView.EnvironmentRequested -= OnEnvironmentRequested;
            webView.AdapterCreated -= OnAdapterCreated;
            webView.AdapterDestroyed -= OnAdapterDestroyed;
            webView.NavigationStarted -= OnNavigationStarted;
            webView.NewWindowRequested -= OnNewWindowRequested;
            _webView = null;
            _adapterReady = false;
            _loadedDocument = null;

            // Leaving the visual tree makes the control destroy its platform web view.
            Child = null;
        }
    }

    /// <summary>
    /// Shows the current document as soon as the platform web view exists.
    /// </summary>
    /// <param name="sender">The web view.</param>
    /// <param name="e">The adapter details.</param>
    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        _adapterReady = true;
        LoadDocument();
    }

    /// <summary>
    /// Remembers that nothing can be loaded and that the document has to be loaded again.
    /// </summary>
    /// <param name="sender">The web view.</param>
    /// <param name="e">The adapter details.</param>
    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        _adapterReady = false;
        _loadedDocument = null;
    }

    /// <summary>
    /// Keeps the web view off the graphics card when hardware rendering is switched off (Windows only; no such switch
    /// is passed to the web views of the other platforms).
    /// </summary>
    /// <param name="sender">The web view.</param>
    /// <param name="e">The settings of the browser about to be created.</param>
    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            windows.AdditionalBrowserArguments =
                HardwareRendering.BrowserArguments(windows.AdditionalBrowserArguments, HardwareRendering.IsEnabled);
        }
    }

    /// <summary>
    /// Loads the current document into the web view once the platform adapter is available.
    /// </summary>
    private void LoadDocument()
    {
        if (_adapterReady && _webView is { } webView && Document is { } document && !string.Equals(document, _loadedDocument, StringComparison.Ordinal))
        {
            _loadedDocument = document;
            webView.NavigateToString(document, null);
        }
    }

    /// <summary>
    /// Blocks navigation to web and mail links (opening them externally); other navigations such as the document load itself proceed.
    /// </summary>
    /// <param name="sender">The web view.</param>
    /// <param name="e">The navigation details.</param>
    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (HtmlMailDocument.IsExternalLink(e.Request))
        {
            e.Cancel = true;
            OpenExternally(e.Request);
        }
    }

    /// <summary>
    /// Opens links that ask for a new window in the system browser instead of a new web view window.
    /// </summary>
    /// <param name="sender">The web view.</param>
    /// <param name="e">The request details.</param>
    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (HtmlMailDocument.IsExternalLink(e.Request))
        {
            OpenExternally(e.Request);
        }
    }

    /// <summary>
    /// Hands a link to the operating system.
    /// </summary>
    /// <param name="uri">The link.</param>
    private async void OpenExternally(Uri uri)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            await topLevel.Launcher.LaunchUriAsync(uri);
        }
    }
}
