using System.Text.Json;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.OAuth;

/// <summary>
/// The OAuth 2.0 client registrations of the application. No credentials are built in; they come from a settings file or
/// environment variables so each deployment uses its own registration.
/// </summary>
/// <param name="GoogleClientId">The Google desktop client id.</param>
/// <param name="GoogleClientSecret">The Google client secret (not confidential for desktop clients, but required by Google's token endpoint).</param>
/// <param name="MicrosoftClientId">The Microsoft public client (application) id.</param>
public sealed record OAuthClientConfiguration(string? GoogleClientId, string? GoogleClientSecret, string? MicrosoftClientId)
{
    /// <summary>
    /// The environment variable holding the Google client id.
    /// </summary>
    public const string GoogleClientIdVariable = "SWIFTLET_GOOGLE_CLIENT_ID";

    /// <summary>
    /// The environment variable holding the Google client secret.
    /// </summary>
    public const string GoogleClientSecretVariable = "SWIFTLET_GOOGLE_CLIENT_SECRET";

    /// <summary>
    /// The environment variable holding the Microsoft client id.
    /// </summary>
    public const string MicrosoftClientIdVariable = "SWIFTLET_MICROSOFT_CLIENT_ID";

    /// <summary>
    /// Gets the client id configured for a provider.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The client id, or <see langword="null"/> if not configured.</returns>
    public string? ClientIdFor(OAuthProvider provider)
    {
        var value = provider == OAuthProvider.Google ? GoogleClientId : MicrosoftClientId;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Gets the client secret configured for a provider.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The client secret, or <see langword="null"/> if none is configured.</returns>
    public string? ClientSecretFor(OAuthProvider provider)
    {
        var value = provider == OAuthProvider.Google ? GoogleClientSecret : null;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Describes how to configure the client ids.
    /// </summary>
    /// <param name="settingsPath">The path of the settings file.</param>
    /// <returns>The hint text.</returns>
    public static string ConfigurationHint(string settingsPath)
    {
        return $"Put {{\"google\":{{\"clientId\":\"...\"}},\"microsoft\":{{\"clientId\":\"...\"}}}} in {settingsPath}, "
            + $"or set {GoogleClientIdVariable} / {MicrosoftClientIdVariable}.";
    }

    /// <summary>
    /// Loads the configuration; environment variables take precedence over the settings file.
    /// </summary>
    /// <param name="settingsPath">The path of the settings file; it need not exist.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <returns>The configuration.</returns>
    public static OAuthClientConfiguration Load(string settingsPath, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var fromFile = ReadFile(settingsPath);
        return new OAuthClientConfiguration(
            Pick(getEnvironmentVariable(GoogleClientIdVariable), fromFile.Google?.ClientId),
            Pick(getEnvironmentVariable(GoogleClientSecretVariable), fromFile.Google?.ClientSecret),
            Pick(getEnvironmentVariable(MicrosoftClientIdVariable), fromFile.Microsoft?.ClientId));
    }

    /// <summary>
    /// Returns the first non-blank value.
    /// </summary>
    /// <param name="preferred">The preferred value.</param>
    /// <param name="fallback">The fallback value.</param>
    /// <returns>The chosen value, or <see langword="null"/>.</returns>
    private static string? Pick(string? preferred, string? fallback)
    {
        return !string.IsNullOrWhiteSpace(preferred) ? preferred : string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    /// <summary>
    /// Reads the settings file, treating a missing or unreadable file as empty.
    /// </summary>
    /// <param name="path">The settings file path.</param>
    /// <returns>The parsed settings.</returns>
    private static SettingsFile ReadFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), JsonSerializerOptions.Web) ?? new SettingsFile(null, null)
                : new SettingsFile(null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken settings file simply means "not configured"; the sign-in screen then explains how to fix it.
            return new SettingsFile(null, null);
        }
    }

    /// <summary>
    /// The shape of the settings file.
    /// </summary>
    /// <param name="Google">The Google section.</param>
    /// <param name="Microsoft">The Microsoft section.</param>
    private sealed record SettingsFile(ClientSection? Google, ClientSection? Microsoft);

    /// <summary>
    /// One provider section of the settings file.
    /// </summary>
    /// <param name="ClientId">The client id.</param>
    /// <param name="ClientSecret">The client secret.</param>
    private sealed record ClientSection(string? ClientId, string? ClientSecret);
}
