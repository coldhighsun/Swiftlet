using System.Text.RegularExpressions;

namespace Swiftlet.Domain.Accounts;

/// <summary>
/// A mailbox account configured by the user (IMAP for receiving, SMTP for sending).
/// </summary>
public sealed partial class MailAccount
{
    /// <summary>
    /// Gets the unique identifier of the account.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Gets the name used as the sender of outgoing mail.
    /// </summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the user typed <see cref="DisplayName"/>, rather than it being filled in from the
    /// address or the identity provider. Only a typed name replaces the address in the folder tree.
    /// </summary>
    public bool HasCustomDisplayName { get; private set; }

    /// <summary>
    /// Gets the email address of the account.
    /// </summary>
    public string EmailAddress { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the authentication method.
    /// </summary>
    public AuthMethod AuthMethod { get; private set; }

    /// <summary>
    /// Gets the OAuth 2.0 token issuer, or <see langword="null"/> for password accounts.
    /// </summary>
    public OAuthProvider? OAuthProvider { get; private set; }

    /// <summary>
    /// Gets the IMAP server settings.
    /// </summary>
    public ServerEndpoint Imap { get; private set; } = default!;

    /// <summary>
    /// Gets the SMTP server settings.
    /// </summary>
    public ServerEndpoint Smtp { get; private set; } = default!;

    /// <summary>
    /// Gets the interval between background synchronizations.
    /// </summary>
    public TimeSpan SyncInterval { get; private set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the full name of the folder archived messages are moved to, or <see langword="null"/> to pick it from the
    /// folder roles reported by the server.
    /// </summary>
    public string? ArchiveFolderName { get; private set; }

    /// <summary>
    /// Gets how many days a message stays in the Trash folder before it is deleted permanently, or
    /// <see langword="null"/> to keep it.
    /// </summary>
    public int? TrashRetentionDays { get; private set; }

    /// <summary>
    /// Gets when the automatic cleanup of the Trash folder was switched on, or <see langword="null"/> while it is off.
    /// </summary>
    public DateTimeOffset? TrashCleanupEnabledAt { get; private set; }

    /// <summary>
    /// Gets the color chosen for the account as <c>#RRGGBB</c>, or <see langword="null"/> to derive it from the address.
    /// </summary>
    public string? Color { get; private set; }

    /// <summary>
    /// Gets the color the account is shown in.
    /// </summary>
    public string EffectiveColor => Color ?? AccountColors.Derive(EmailAddress);

    /// <summary>
    /// Gets the position of the account in the folder tree; lower values come first.
    /// </summary>
    public int SortOrder { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the account node is expanded in the folder tree.
    /// </summary>
    public bool IsExpanded { get; private set; } = true;

    /// <summary>
    /// Gets the time the account was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MailAccount"/> class for persistence.
    /// </summary>
    private MailAccount()
    {
    }

    /// <summary>
    /// Creates a new account after validating the supplied values.
    /// </summary>
    /// <param name="displayName">The display name.</param>
    /// <param name="emailAddress">The email address.</param>
    /// <param name="authMethod">The authentication method.</param>
    /// <param name="imap">The IMAP server settings.</param>
    /// <param name="smtp">The SMTP server settings.</param>
    /// <param name="now">The creation time.</param>
    /// <param name="oauthProvider">The token issuer; required when <paramref name="authMethod"/> is OAuth 2.0.</param>
    /// <param name="hasCustomDisplayName">Whether the user typed <paramref name="displayName"/>.</param>
    /// <param name="color">The color as <c>#RRGGBB</c>; omitted to derive it from the address.</param>
    /// <returns>The created account.</returns>
    public static MailAccount Create(
        string displayName,
        string emailAddress,
        AuthMethod authMethod,
        ServerEndpoint imap,
        ServerEndpoint smtp,
        DateTimeOffset now,
        OAuthProvider? oauthProvider = null,
        bool hasCustomDisplayName = false,
        string? color = null)
    {
        if (authMethod == AuthMethod.OAuth2 && oauthProvider is null)
        {
            throw new ArgumentException("An OAuth provider is required for OAuth 2.0 accounts.", nameof(oauthProvider));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(emailAddress);
        ArgumentNullException.ThrowIfNull(imap);
        ArgumentNullException.ThrowIfNull(smtp);

        var trimmed = emailAddress.Trim();
        var at = trimmed.IndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1)
        {
            throw new ArgumentException("Email address is invalid.", nameof(emailAddress));
        }

        return new MailAccount
        {
            DisplayName = displayName.Trim(),
            HasCustomDisplayName = hasCustomDisplayName,
            EmailAddress = trimmed,
            AuthMethod = authMethod,
            OAuthProvider = authMethod == AuthMethod.OAuth2 ? oauthProvider : null,
            Imap = imap,
            Smtp = smtp,
            CreatedAt = now,
            Color = color is null ? null : ValidateColor(color),
        };
    }

    /// <summary>
    /// Changes the sender name. A blank name removes a name the user typed, falling back to the local part of the
    /// address; a name that was filled in automatically is kept.
    /// </summary>
    /// <param name="displayName">The name typed by the user; blank for none.</param>
    public void ChangeDisplayName(string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName.Trim();
            HasCustomDisplayName = true;
        }
        else if (HasCustomDisplayName)
        {
            DisplayName = EmailAddress[..EmailAddress.IndexOf('@', StringComparison.Ordinal)];
            HasCustomDisplayName = false;
        }
    }

    /// <summary>
    /// Changes the server settings of a password account; OAuth accounts use the fixed servers of their provider.
    /// </summary>
    /// <param name="imap">The IMAP server settings.</param>
    /// <param name="smtp">The SMTP server settings.</param>
    /// <exception cref="InvalidOperationException">The account signs in with OAuth 2.0.</exception>
    public void ChangeServers(ServerEndpoint imap, ServerEndpoint smtp)
    {
        if (AuthMethod != AuthMethod.Password)
        {
            throw new InvalidOperationException("The server settings of an OAuth 2.0 account cannot be changed.");
        }

        Validate(imap, nameof(imap));
        Validate(smtp, nameof(smtp));
        Imap = imap;
        Smtp = smtp;
    }

    /// <summary>
    /// Changes the background synchronization interval.
    /// </summary>
    /// <param name="interval">The new interval; must be at least one minute.</param>
    public void ChangeSyncInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromMinutes(1));
        SyncInterval = interval;
    }

    /// <summary>
    /// Changes the folder archived messages are moved to.
    /// </summary>
    /// <param name="folderName">The full name of the folder, kept exactly as the server reports it; blank for automatic.</param>
    public void ChangeArchiveFolder(string? folderName)
    {
        ArchiveFolderName = string.IsNullOrWhiteSpace(folderName) ? null : folderName;
    }

    /// <summary>
    /// Changes how long messages stay in the Trash folder.
    /// </summary>
    /// <param name="days">The number of days, at least one; <see langword="null"/> keeps them.</param>
    /// <param name="now">The current time, recorded when the cleanup is switched on.</param>
    public void ChangeTrashRetention(int? days, DateTimeOffset now)
    {
        if (days is { } value)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(days));
        }

        TrashRetentionDays = days;
        TrashCleanupEnabledAt = days is null ? null : TrashCleanupEnabledAt ?? now;
    }

    /// <summary>
    /// Changes the color of the account.
    /// </summary>
    /// <param name="color">The new color as <c>#RRGGBB</c>.</param>
    public void ChangeColor(string color)
    {
        Color = ValidateColor(color);
    }

    /// <summary>
    /// Changes the position of the account in the folder tree.
    /// </summary>
    /// <param name="order">The new position; must not be negative.</param>
    public void MoveTo(int order)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(order);
        SortOrder = order;
    }

    /// <summary>
    /// Remembers whether the account node is expanded in the folder tree.
    /// </summary>
    /// <param name="expanded">Whether the node is expanded.</param>
    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
    }

    /// <summary>
    /// Matches a color in <c>#RRGGBB</c> form.
    /// </summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ColorPattern();

    /// <summary>
    /// Validates a color.
    /// </summary>
    /// <param name="color">The raw color.</param>
    /// <returns>The validated color.</returns>
    private static string ValidateColor(string color)
    {
        if (color is null || !ColorPattern().IsMatch(color))
        {
            throw new ArgumentException("Color must be in #RRGGBB format.", nameof(color));
        }

        return color;
    }

    /// <summary>
    /// Rejects server settings that cannot be connected to.
    /// </summary>
    /// <param name="endpoint">The settings to check.</param>
    /// <param name="parameterName">The name reported by the exception.</param>
    private static void Validate(ServerEndpoint endpoint, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(endpoint, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Host, parameterName);
        if (endpoint.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName, endpoint.Port, "The port must be between 1 and 65535.");
        }
    }
}
