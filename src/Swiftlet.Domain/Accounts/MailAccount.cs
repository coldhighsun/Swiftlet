namespace Swiftlet.Domain.Accounts;

/// <summary>
/// A mailbox account configured by the user (IMAP for receiving, SMTP for sending).
/// </summary>
public sealed class MailAccount
{
    /// <summary>
    /// Gets the unique identifier of the account.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Gets the display name shown in the UI.
    /// </summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the email address of the account.
    /// </summary>
    public string EmailAddress { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the authentication method.
    /// </summary>
    public AuthMethod AuthMethod { get; private set; }

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
    /// <returns>The created account.</returns>
    public static MailAccount Create(
        string displayName,
        string emailAddress,
        AuthMethod authMethod,
        ServerEndpoint imap,
        ServerEndpoint smtp,
        DateTimeOffset now)
    {
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
            EmailAddress = trimmed,
            AuthMethod = authMethod,
            Imap = imap,
            Smtp = smtp,
            CreatedAt = now,
        };
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
}
