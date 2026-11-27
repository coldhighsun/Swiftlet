using GitHubReleaseUpdater.LastCheck;
using GitHubReleaseUpdater.Versioning;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Keeps the updater's memory (last check, skipped version, waiting update) in the settings file.
/// </summary>
/// <param name="settings">The settings store.</param>
public sealed class UpdateLastCheckStore(IAppSettings settings) : ILastCheckStore, IPendingUpdateStore
{
    /// <summary>
    /// Gets when the last check was made. While an update is waiting none is reported, so that the next start checks
    /// again and offers it again.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The time, or <see langword="null"/>.</returns>
    public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default)
    {
        var state = settings.UpdateCheckState;
        return Task.FromResult(state.Pending ? null : state.LastCheckedAt);
    }

    /// <inheritdoc />
    public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default)
    {
        settings.ChangeUpdateCheckState(state => state with { LastCheckedAt = checkedAt });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default)
    {
        var skipped = settings.UpdateCheckState.SkippedVersion;
        return Task.FromResult(SemanticVersion.TryParse(skipped, out var version) ? version : null);
    }

    /// <inheritdoc />
    public Task SetSkippedVersionAsync(SemanticVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        settings.ChangeUpdateCheckState(state => state with { SkippedVersion = version.ToString() });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default)
    {
        settings.ChangeUpdateCheckState(state => state with { SkippedVersion = null });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetUpdatePendingAsync(bool pending, CancellationToken cancellationToken = default)
    {
        settings.ChangeUpdateCheckState(state => state with { Pending = pending });
        return Task.CompletedTask;
    }
}
