namespace Swiftlet.Domain.Messages;

/// <summary>
/// The conditions of a search of the cached messages; a message must satisfy all conditions that are set.
/// </summary>
public sealed record MessageSearchCriteria
{
    /// <summary>
    /// Gets the free text matched against subject, sender and body; every space-separated term must match.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Gets the terms matched against the sender's name or address; every term must match.
    /// </summary>
    public string From { get; init; } = string.Empty;

    /// <summary>
    /// Gets the terms matched against the recipient addresses (To and Cc); every term must match.
    /// </summary>
    public string To { get; init; } = string.Empty;

    /// <summary>
    /// Gets the terms matched against the subject; every term must match.
    /// </summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>
    /// Gets the earliest sent time that matches (inclusive).
    /// </summary>
    public DateTimeOffset? SentFrom { get; init; }

    /// <summary>
    /// Gets the sent time from which on messages no longer match (exclusive).
    /// </summary>
    public DateTimeOffset? SentBefore { get; init; }

    /// <summary>
    /// Gets a value indicating whether only messages with attachments match.
    /// </summary>
    public bool HasAttachments { get; init; }

    /// <summary>
    /// Gets a value indicating whether only unread messages match.
    /// </summary>
    public bool UnreadOnly { get; init; }

    /// <summary>
    /// Gets a value indicating whether only flagged (starred) messages match.
    /// </summary>
    public bool FlaggedOnly { get; init; }

    /// <summary>
    /// Gets the account searched, or <see langword="null"/> for all accounts.
    /// </summary>
    public Guid? AccountId { get; init; }

    /// <summary>
    /// Gets the folder searched, or <see langword="null"/> for all folders; it belongs to <see cref="AccountId"/>.
    /// </summary>
    public Guid? FolderId { get; init; }

    /// <summary>
    /// Gets a value indicating whether no condition is set. The account and folder only limit where to look and are not
    /// conditions.
    /// </summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text)
        && string.IsNullOrWhiteSpace(From)
        && string.IsNullOrWhiteSpace(To)
        && string.IsNullOrWhiteSpace(Subject)
        && SentFrom is null
        && SentBefore is null
        && !HasAttachments
        && !UnreadOnly
        && !FlaggedOnly;
}
