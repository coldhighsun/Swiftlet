using Swiftlet.Application.Abstractions;

namespace Swiftlet.Application.Sync;

/// <summary>
/// The fallback sync settings used until the host registers its own store; follows server deletions.
/// </summary>
internal sealed class DefaultSyncSettings : ISyncSettings
{
    /// <inheritdoc />
    public bool RemoveServerDeletedMessages => true;

    /// <inheritdoc />
    public bool DownloadFullHistory => true;

    /// <inheritdoc />
    public bool DownloadAllMail => true;
}
