namespace Swiftlet.Desktop.Settings;

/// <summary>
/// What is remembered about update checks between runs.
/// </summary>
/// <param name="LastCheckedAt">When GitHub was last asked.</param>
/// <param name="SkippedVersion">The version the user does not want to be offered again.</param>
/// <param name="Pending">Whether the last check found a newer version that is still waiting.</param>
public sealed record UpdateCheckState(
    DateTimeOffset? LastCheckedAt = null,
    string? SkippedVersion = null,
    bool Pending = false);
