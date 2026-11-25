namespace Swiftlet.Domain.Messages;

/// <summary>
/// A lightweight, untracked snapshot of the read/starred state of a cached message.
/// </summary>
/// <param name="Id">The message identifier.</param>
/// <param name="Uid">The IMAP UID.</param>
/// <param name="IsRead">Whether the message is read.</param>
/// <param name="IsFlagged">Whether the message is starred.</param>
/// <param name="IsFlagSyncPending">Whether a local change still has to be written to the server.</param>
public sealed record MessageFlagState(Guid Id, uint Uid, bool IsRead, bool IsFlagged, bool IsFlagSyncPending);
