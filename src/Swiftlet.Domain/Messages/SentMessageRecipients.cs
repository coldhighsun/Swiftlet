namespace Swiftlet.Domain.Messages;

/// <summary>
/// The recipients of one cached message of a Sent folder.
/// </summary>
/// <param name="Recipients">The comma-separated recipient addresses.</param>
/// <param name="SentAt">When the message was sent.</param>
public sealed record SentMessageRecipients(string Recipients, DateTimeOffset SentAt);
