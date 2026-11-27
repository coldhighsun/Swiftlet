using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Accounts;

/// <summary>
/// Adds a password-authenticated IMAP/SMTP account after verifying the IMAP login.
/// </summary>
/// <param name="DisplayName">The name typed by the user; blank to use the local part of the address.</param>
/// <param name="EmailAddress">The email address, also used as the login name.</param>
/// <param name="Password">The password or app-specific password.</param>
/// <param name="Imap">The IMAP endpoint; omitted to use the provider preset.</param>
/// <param name="Smtp">The SMTP endpoint; omitted to use the provider preset.</param>
public sealed record AddAccountCommand(
    string DisplayName,
    string EmailAddress,
    string Password,
    ServerEndpoint? Imap = null,
    ServerEndpoint? Smtp = null) : ICommand<Guid>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return EmailAddress;
    }
}

/// <summary>
/// Handles <see cref="AddAccountCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="secrets">The secret store.</param>
/// <param name="imapFactory">The IMAP connection factory used to verify credentials.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class AddAccountCommandHandler(
    IAccountRepository accounts,
    ISecretStore secrets,
    IImapSessionFactory imapFactory,
    TimeProvider timeProvider,
    ILogger<AddAccountCommandHandler>? logger = null) : ICommandHandler<AddAccountCommand, Guid>
{
    /// <inheritdoc />
    public async ValueTask<Guid> Handle(AddAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Password);

        var imap = command.Imap;
        var smtp = command.Smtp;
        if (imap is null || smtp is null)
        {
            if (!ProviderPresets.TryGet(command.EmailAddress, out var preset))
            {
                throw new ArgumentException(
                    "The mail provider is unknown; IMAP and SMTP server settings must be supplied.",
                    nameof(command));
            }

            imap ??= preset.Imap;
            smtp ??= preset.Smtp;
        }

        var email = command.EmailAddress.Trim();
        var existing = await accounts.GetAllAsync(cancellationToken);
        if (existing.Any(a => string.Equals(a.EmailAddress, email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Account {email} already exists.");
        }

        var hasCustomName = !string.IsNullOrWhiteSpace(command.DisplayName);
        var account = MailAccount.Create(
            hasCustomName ? command.DisplayName : email.Split('@')[0],
            email,
            AuthMethod.Password,
            imap,
            smtp,
            timeProvider.GetUtcNow(),
            hasCustomDisplayName: hasCustomName);

        var key = AccountSecrets.PasswordKey(account.Id);
        await secrets.SetAsync(key, command.Password, cancellationToken);
        try
        {
            await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
            await accounts.AddAsync(account, cancellationToken);
        }
        catch
        {
            // Do not leave an orphaned password behind when verification or saving fails; the error still propagates.
            await secrets.RemoveAsync(key, CancellationToken.None);
            throw;
        }

        logger?.LogInformation("Account {Account} added (password)", email);
        return account.Id;
    }
}
