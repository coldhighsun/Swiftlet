using Swiftlet.Domain.Folders;

namespace Swiftlet.Application.Sync;

/// <summary>
/// The order in which the folders of an account are synchronized and backfilled.
/// </summary>
internal static class FolderSyncOrder
{
    /// <summary>
    /// Gets the sort rank of a role: inbox first, then sent mail, then everything else, then All Mail, with Trash and Junk last.
    /// </summary>
    /// <param name="role">The folder role.</param>
    /// <returns>The sort rank.</returns>
    public static int Priority(FolderRole role)
    {
        return role switch
        {
            FolderRole.Inbox => 0,
            FolderRole.Sent => 1,
            FolderRole.AllMail => 3,
            FolderRole.Trash or FolderRole.Junk => 4,
            _ => 2,
        };
    }
}
