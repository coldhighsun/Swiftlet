namespace Swiftlet.Domain.Folders;

/// <summary>
/// The well-known purpose of a mail folder.
/// </summary>
public enum FolderRole
{
    /// <summary>
    /// A user-defined folder without special meaning.
    /// </summary>
    Other = 0,

    /// <summary>
    /// The inbox.
    /// </summary>
    Inbox = 1,

    /// <summary>
    /// Sent messages.
    /// </summary>
    Sent = 2,

    /// <summary>
    /// Drafts.
    /// </summary>
    Drafts = 3,

    /// <summary>
    /// Deleted messages.
    /// </summary>
    Trash = 4,

    /// <summary>
    /// Spam / junk messages.
    /// </summary>
    Junk = 5,

    /// <summary>
    /// Archived messages.
    /// </summary>
    Archive = 6,

    /// <summary>
    /// A folder that holds a copy of every message (Gmail's "All Mail"), so it duplicates the other folders.
    /// </summary>
    AllMail = 7,
}
