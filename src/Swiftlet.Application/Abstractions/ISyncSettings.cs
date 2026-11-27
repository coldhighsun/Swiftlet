namespace Swiftlet.Application.Abstractions;

/// <summary>
/// User-configurable synchronization behavior, read on every sync so changes apply without a restart.
/// </summary>
public interface ISyncSettings
{
    /// <summary>
    /// Gets a value indicating whether cached messages that were deleted on the server are removed locally.
    /// </summary>
    bool RemoveServerDeletedMessages { get; }

    /// <summary>
    /// Gets a value indicating whether the full message history is downloaded in the background after the first sync.
    /// </summary>
    bool DownloadFullHistory { get; }
}
