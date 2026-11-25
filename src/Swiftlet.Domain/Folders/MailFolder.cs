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
        return invalidated;
    }
}
