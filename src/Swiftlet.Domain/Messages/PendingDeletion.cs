namespace Swiftlet.Domain.Messages;

/// <summary>
/// A cached message as far as deleting or moving it on the server is concerned.
/// </summary>
/// <param name="Id">The cached message identifier.</param>
/// <param name="FolderId">The folder holding the message.</param>
/// <param name="Uid">The IMAP UID of the message in that folder.</param>
/// <param name="TargetFolderId">The folder the user moves the message to, or <see langword="null"/> to delete it.</param>
public sealed record PendingDeletion(Guid Id, Guid FolderId, uint Uid, Guid? TargetFolderId = null);
