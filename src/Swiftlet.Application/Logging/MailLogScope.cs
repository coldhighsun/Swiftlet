using Microsoft.Extensions.Logging;

namespace Swiftlet.Application.Logging;

/// <summary>
/// Opens logging scopes that tag every line written inside them with the mailbox and folder being worked on. The log file
/// template prints the two properties in front of the message.
/// </summary>
public static class MailLogScope
{
    /// <summary>
    /// The name of the scope property that holds the account tag.
    /// </summary>
    public const string AccountProperty = "AccountTag";

    /// <summary>
    /// The name of the scope property that holds the folder tag.
    /// </summary>
    public const string FolderProperty = "FolderTag";

    /// <summary>
    /// Opens a scope for an account and optionally one of its folders. The innermost scope wins, and a scope without a
    /// folder clears the folder of an outer one.
    /// </summary>
    /// <param name="logger">The logger; nothing happens without one.</param>
    /// <param name="account">
    /// The account, normally its email address; <see langword="null"/> for work that concerns no single account, which
    /// clears the tags inherited from an outer scope.
    /// </param>
    /// <param name="folder">The full folder name, if the work concerns one folder.</param>
    /// <returns>The scope to dispose when the work is done.</returns>
    public static IDisposable? BeginMailScope(this ILogger? logger, string? account, string? folder = null)
    {
        // The brackets are part of the values so that a line outside any scope shows nothing at all.
        return logger?.BeginScope<IReadOnlyList<KeyValuePair<string, object>>>(
        [
            new(AccountProperty, Tag(account)),
            new(FolderProperty, Tag(folder)),
        ]);
    }

    /// <summary>
    /// Formats one tag.
    /// </summary>
    /// <param name="name">The name, or <see langword="null"/> for no tag.</param>
    /// <returns>The tag with its leading space, or an empty string.</returns>
    private static string Tag(string? name)
    {
        return name is null ? string.Empty : $" [{name}]";
    }
}
