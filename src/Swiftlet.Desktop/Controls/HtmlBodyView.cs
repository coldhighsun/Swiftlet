using Avalonia;
using Avalonia.Controls;
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
    /// The embedded web view.
    /// </summary>
    private readonly NativeWebView _webView = new();

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
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HtmlBodyView"/> class.
    /// </summary>
    public HtmlBodyView()
    {
        Child = _webView;
        _webView.AdapterCreated += (_, _) =>
        {
            _adapterReady = true;
            LoadDocument();
        };
        _webView.AdapterDestroyed += (_, _) =>
        {
            _adapterReady = false;
            _loadedDocument = null;
        };
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NewWindowRequested += OnNewWindowRequested;
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
    /// Loads the current document into the web view once the platform adapter is available.
    /// </summary>
    private void LoadDocument()
    {
        if (_adapterReady && Document is { } document && !string.Equals(document, _loadedDocument, StringComparison.Ordinal))
        {
            _loadedDocument = document;
            _webView.NavigateToString(document, null);
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
