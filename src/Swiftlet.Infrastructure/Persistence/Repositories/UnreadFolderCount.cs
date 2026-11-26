namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// The number of unread messages of one folder.
/// </summary>
/// <param name="FolderId">The folder.</param>
/// <param name="Count">The number of unread messages.</param>
internal sealed record UnreadFolderCount(Guid FolderId, int Count);
