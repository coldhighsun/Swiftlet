namespace Swiftlet.Domain.Contacts;

/// <summary>
/// An address a message was sent to from this application.
/// </summary>
/// <param name="AccountId">The sending account.</param>
/// <param name="Address">The recipient address, in lower case.</param>
/// <param name="Name">The name used with the address; empty when none was given.</param>
/// <param name="LastUsedAt">When a message was last sent to the address.</param>
public sealed record SentRecipient(Guid AccountId, string Address, string Name, DateTimeOffset LastUsedAt);
