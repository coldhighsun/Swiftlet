namespace Swiftlet.Domain.Messages;

/// <summary>
/// How many cached messages a folder holds.
/// </summary>
/// <param name="FolderId">The folder.</param>
/// <param name="Total">The number of cached messages.</param>
/// <param name="Unread">The number of those that are unread.</param>
public sealed record FolderMessageCount(Guid FolderId, int Total, int Unread);
