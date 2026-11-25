using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Accounts;

/// <summary>
/// Server settings of a well-known mail provider.
/// </summary>
/// <param name="Name">The provider name.</param>
/// <param name="Imap">The IMAP endpoint.</param>
/// <param name="Smtp">The SMTP endpoint.</param>
/// <param name="SavesSentCopyAutomatically">
/// Whether the provider's SMTP server stores a copy in Sent by itself, so the client must not append one.
/// </param>
/// <param name="RequiresAppPassword">Whether the user must create an app password / authorization code.</param>
public sealed record ProviderPreset(
    string Name,
    ServerEndpoint Imap,
    ServerEndpoint Smtp,
    bool SavesSentCopyAutomatically,
    bool RequiresAppPassword);

/// <summary>
/// Looks up server settings by email domain.
/// </summary>
public static class ProviderPresets
{
    /// <summary>
    /// The known providers keyed by lower-case email domain.
    /// </summary>
    private static readonly Dictionary<string, ProviderPreset> ByDomain = Build();

    /// <summary>
    /// Finds the preset for an email address.
    /// </summary>
    /// <param name="emailAddress">The email address.</param>
    /// <param name="preset">The preset, when found.</param>
    /// <returns><see langword="true"/> if the domain is known.</returns>
    public static bool TryGet(string emailAddress, out ProviderPreset preset)
    {
        preset = default!;
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            return false;
        }

        var at = emailAddress.LastIndexOf('@');
        if (at < 0 || at == emailAddress.Length - 1)
        {
            return false;
        }

        var domain = emailAddress[(at + 1)..].Trim().ToLowerInvariant();
        return ByDomain.TryGetValue(domain, out preset!);
    }

    /// <summary>
    /// Gets the server settings used for accounts signed in through an OAuth provider.
    /// </summary>
    /// <param name="provider">The OAuth provider.</param>
    /// <returns>The preset of the provider's mail service.</returns>
    public static ProviderPreset For(OAuthProvider provider)
    {
        var sample = provider == OAuthProvider.Google ? "user@gmail.com" : "user@outlook.com";
        return TryGet(sample, out var preset)
            ? preset
            : throw new InvalidOperationException($"No preset is defined for {provider}.");
    }

    /// <summary>
    /// Builds the domain lookup table.
    /// </summary>
    /// <returns>The lookup table.</returns>
    private static Dictionary<string, ProviderPreset> Build()
    {
        var gmail = new ProviderPreset(
            "Gmail",
            new ServerEndpoint("imap.gmail.com", 993, true),
            new ServerEndpoint("smtp.gmail.com", 465, true),
            SavesSentCopyAutomatically: true,
            RequiresAppPassword: true);
        var outlook = new ProviderPreset(
            "Outlook.com",
            new ServerEndpoint("outlook.office365.com", 993, true),
            new ServerEndpoint("smtp-mail.outlook.com", 587, false),
            SavesSentCopyAutomatically: true,
            RequiresAppPassword: true);
        var qq = new ProviderPreset(
            "QQ Mail",
            new ServerEndpoint("imap.qq.com", 993, true),
            new ServerEndpoint("smtp.qq.com", 465, true),
            SavesSentCopyAutomatically: false,
            RequiresAppPassword: true);
        var netease163 = new ProviderPreset(
            "163 Mail",
            new ServerEndpoint("imap.163.com", 993, true),
            new ServerEndpoint("smtp.163.com", 465, true),
            SavesSentCopyAutomatically: false,
            RequiresAppPassword: true);
        var netease126 = new ProviderPreset(
            "126 Mail",
            new ServerEndpoint("imap.126.com", 993, true),
            new ServerEndpoint("smtp.126.com", 465, true),
            SavesSentCopyAutomatically: false,
            RequiresAppPassword: true);
        var yahoo = new ProviderPreset(
            "Yahoo Mail",
            new ServerEndpoint("imap.mail.yahoo.com", 993, true),
            new ServerEndpoint("smtp.mail.yahoo.com", 465, true),
            SavesSentCopyAutomatically: false,
            RequiresAppPassword: true);
        var icloud = new ProviderPreset(
            "iCloud Mail",
            new ServerEndpoint("imap.mail.me.com", 993, true),
            new ServerEndpoint("smtp.mail.me.com", 587, false),
            SavesSentCopyAutomatically: false,
            RequiresAppPassword: true);

        return new Dictionary<string, ProviderPreset>(StringComparer.Ordinal)
        {
            ["gmail.com"] = gmail,
            ["googlemail.com"] = gmail,
            ["outlook.com"] = outlook,
            ["hotmail.com"] = outlook,
            ["live.com"] = outlook,
            ["msn.com"] = outlook,
            ["qq.com"] = qq,
            ["foxmail.com"] = qq,
            ["163.com"] = netease163,
            ["126.com"] = netease126,
            ["yahoo.com"] = yahoo,
            ["icloud.com"] = icloud,
            ["me.com"] = icloud,
        };
    }
}
