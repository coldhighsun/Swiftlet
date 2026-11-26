namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// Links a label to a message of an account. The message lives in that account's own database, so the link carries
/// the account identifier instead of a foreign key.
/// </summary>
internal sealed class MessageLabel
{
    /// <summary>
    /// Gets or sets the message identifier.
    /// </summary>
    public Guid MessageId { get; set; }

    /// <summary>
    /// Gets or sets the label identifier.
    /// </summary>
    public Guid LabelId { get; set; }

    /// <summary>
    /// Gets or sets the account that owns the message.
    /// </summary>
    public Guid AccountId { get; set; }
}
