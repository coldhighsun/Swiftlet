namespace Swiftlet.Domain.Folders;

/// <summary>
/// An IMAP folder belonging to an account.
/// </summary>
public sealed class MailFolder
{
    /// <summary>
    /// Gets the unique identifier of the folder.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Gets the identifier of the owning account.
    /// </summary>
    public Guid AccountId { get; private set; }

    /// <summary>
    /// Gets the display name of the folder.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the full server-side path of the folder.
    /// </summary>
    public string FullName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the well-known role of the folder.
    /// </summary>
    public FolderRole Role { get; private set; }

    /// <summary>
    /// Gets the last known UIDVALIDITY value reported by the server, or <see langword="null"/> if never synchronized.
    /// </summary>
    public uint? UidValidity { get; private set; }

    /// <summary>
    /// Gets the highest UID of a pending "delete all messages" request, or <see langword="null"/> when none is pending.
    /// Every server message up to this UID is to be deleted; later ones arrived after the request and are kept.
    /// </summary>
    public uint? EmptyThroughUid { get; private set; }

    /// <summary>
    /// The least time between two checkpoints, which keeps their number small.
    /// </summary>
    private static readonly TimeSpan CheckpointSpacing = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets what was recorded about when messages were first seen in this folder, oldest first. Only the automatic
    /// cleanup of the Trash folder records any.
    /// </summary>
    public IReadOnlyList<UidCheckpoint> TrashCheckpoints { get; private set; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="MailFolder"/> class for persistence.
    /// </summary>
    private MailFolder()
    {
    }

    /// <summary>
    /// Creates a new folder.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="fullName">The full server-side path.</param>
    /// <param name="name">The display name.</param>
    /// <param name="role">The well-known role.</param>
    /// <returns>The created folder.</returns>
    public static MailFolder Create(Guid accountId, string fullName, string name, FolderRole role)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Account id is required.", nameof(accountId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new MailFolder
        {
            AccountId = accountId,
            FullName = fullName,
            Name = name,
            Role = role,
        };
    }

    /// <summary>
    /// Records the UIDVALIDITY reported by the server.
    /// </summary>
    /// <param name="uidValidity">The value reported by the server.</param>
    /// <returns>
    /// <see langword="true"/> if the value changed from a previously known one, meaning cached UIDs are no longer valid.
    /// </returns>
    public bool UpdateUidValidity(uint uidValidity)
    {
        var invalidated = UidValidity.HasValue && UidValidity.Value != uidValidity;
        UidValidity = uidValidity;
        if (invalidated)
        {
            // The boundary names UIDs of the old epoch; applying it to the new one could delete unrelated mail.
            EmptyThroughUid = null;
            TrashCheckpoints = [];
        }

        return invalidated;
    }

    /// <summary>
    /// Requests that every message up to a UID be deleted; an earlier request with a higher boundary is kept.
    /// </summary>
    /// <param name="throughUid">The highest UID to delete.</param>
    public void RequestEmpty(uint throughUid)
    {
        ArgumentOutOfRangeException.ThrowIfZero(throughUid);
        EmptyThroughUid = Math.Max(throughUid, EmptyThroughUid ?? 0);
    }

    /// <summary>
    /// Ends the pending request to delete all messages, because it was carried out or has to be abandoned.
    /// </summary>
    public void ClearEmptyRequest()
    {
        EmptyThroughUid = null;
    }

    /// <summary>
    /// Records what the folder holds now and tells which messages have been in it for the whole retention. A message
    /// never expires earlier than that; it may expire up to about a day later.
    /// </summary>
    /// <param name="highestUid">The highest UID the server holds in the folder, or 0 when it is empty.</param>
    /// <param name="now">The current time.</param>
    /// <param name="enabledAt">When cleaning up was switched on; older records are dropped.</param>
    /// <param name="retention">How long messages are kept.</param>
    /// <returns>The highest expired UID, or 0 when nothing has expired.</returns>
    public uint ObserveTrash(uint highestUid, DateTimeOffset now, DateTimeOffset enabledAt, TimeSpan retention)
    {
        var checkpoints = TrashCheckpoints.Where(c => c.ObservedAt >= enabledAt).ToList();
        if (highestUid > 0
            && (checkpoints.Count == 0
                || (highestUid > checkpoints[^1].Uid && now - checkpoints[^1].ObservedAt >= CheckpointSpacing)))
        {
            checkpoints.Add(new UidCheckpoint(highestUid, now));
        }

        // The newest expired record covers all older ones, and stays so a failed deletion is repeated.
        var expired = checkpoints.FindLastIndex(c => now - c.ObservedAt >= retention);
        if (expired > 0)
        {
            checkpoints.RemoveRange(0, expired);
        }

        TrashCheckpoints = checkpoints;
        return expired < 0 ? 0 : checkpoints[0].Uid;
    }
}
