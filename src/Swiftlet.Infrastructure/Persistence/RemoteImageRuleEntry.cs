namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// A saved rule that allows loading remote images in mail from a sender or a sender's domain.
/// </summary>
internal sealed class RemoteImageRuleEntry
{
    /// <summary>
    /// Gets or sets the normalized rule: an address (contains <c>@</c>) or a domain.
    /// </summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the rule was added.
    /// </summary>
    public DateTimeOffset AddedAt { get; set; }
}
