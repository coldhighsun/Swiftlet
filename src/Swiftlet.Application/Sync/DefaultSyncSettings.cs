using Swiftlet.Application.Abstractions;

namespace Swiftlet.Application.Sync;

/// <summary>
/// The fallback sync settings used until the host registers its own store; never removes local messages.
/// </summary>
internal sealed class DefaultSyncSettings : ISyncSettings
{
    /// <inheritdoc />
    public bool RemoveServerDeletedMessages => false;
}
