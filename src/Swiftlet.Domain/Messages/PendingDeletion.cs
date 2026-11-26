namespace Swiftlet.Domain.Messages;

/// <summary>
/// A cached message as far as deleting it on the server is concerned.
/// </summary>
/// <param name="Id">The cached message identifier.</param>
/// <param name="FolderId">The folder holding the message.</param>
/// <param name="Uid">The IMAP UID of the message in that folder.</param>
public sealed record PendingDeletion(Guid Id, Guid FolderId, uint Uid);
