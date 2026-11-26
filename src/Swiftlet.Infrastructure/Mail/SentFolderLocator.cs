using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Finds the Sent folder in a folder listing.
/// </summary>
internal static class SentFolderLocator
{
    /// <summary>
    /// Finds the first selectable folder the server marks as Sent, the same rule the folder tree uses.
    /// </summary>
    /// <param name="folders">The listed folders.</param>
    /// <returns>The full name of the folder, or <see langword="null"/> if none is marked.</returns>
    public static string? Find(IReadOnlyList<RemoteFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return folders.FirstOrDefault(f => f.Role == FolderRole.Sent && f.CanSelect)?.FullName;
    }
}
