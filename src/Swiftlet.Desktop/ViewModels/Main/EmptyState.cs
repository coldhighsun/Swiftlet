namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// Why the message list has nothing to show.
/// </summary>
public enum EmptyState
{
    /// <summary>
    /// Nothing to say: messages are listed, still loading, or no list view is open.
    /// </summary>
    None,

    /// <summary>
    /// No account is configured.
    /// </summary>
    NoAccounts,

    /// <summary>
    /// The opened folder or view holds no messages.
    /// </summary>
    EmptyView,

    /// <summary>
    /// A search found nothing.
    /// </summary>
    NoSearchResults,
}
