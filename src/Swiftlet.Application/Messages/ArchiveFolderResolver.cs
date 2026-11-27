using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Determines which folder archived messages of an account are moved to.
/// </summary>
public static class ArchiveFolderResolver
{
    /// <summary>
    /// Gets the archive folder of an account: the folder chosen in the account settings, or, with none chosen, the one
    /// the server marks as archive, else its "All Mail" folder. A chosen folder that is not among the folders is never
    /// replaced by another one, so mail does not go where the user did not ask for it.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="folders">The cached folders of the account.</param>
    /// <returns>The folder, or <see langword="null"/> if messages of the account cannot be archived.</returns>
    public static MailFolder? Resolve(MailAccount account, IEnumerable<MailFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folders);

        var own = folders.Where(f => f.AccountId == account.Id).ToList();
        if (account.ArchiveFolderName is { } chosen)
        {
            return own.FirstOrDefault(f => string.Equals(f.FullName, chosen, StringComparison.Ordinal));
        }

        return Automatic(own);
    }

    /// <summary>
    /// Gets the folder used when none is chosen: the one the server marks as archive, else its "All Mail" folder.
    /// </summary>
    /// <param name="folders">The folders of one account.</param>
    /// <returns>The folder, or <see langword="null"/> if the server marks neither.</returns>
    public static MailFolder? Automatic(IReadOnlyCollection<MailFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return folders.FirstOrDefault(f => f.Role == FolderRole.Archive)
            ?? folders.FirstOrDefault(f => f.Role == FolderRole.AllMail);
    }
}
