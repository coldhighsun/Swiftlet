using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Contacts;
using Swiftlet.Domain.Labels;
using Swiftlet.Infrastructure.Persistence.Configurations;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// The EF Core context of the global store: the account registry, the labels, the links between labels and
/// messages, the remote image rules and the address book. The mail itself lives in one
/// <see cref="AccountDbContext"/> database per account.
/// </summary>
/// <param name="options">The context options.</param>
public sealed class GlobalDbContext(DbContextOptions<GlobalDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Gets the accounts.
    /// </summary>
    public DbSet<MailAccount> Accounts => Set<MailAccount>();

    /// <summary>
    /// Gets the labels.
    /// </summary>
    public DbSet<Label> Labels => Set<Label>();

    /// <summary>
    /// Gets the links between labels and messages; messages are stored in the account databases, so there is no foreign key to them.
    /// </summary>
    internal DbSet<MessageLabel> MessageLabels => Set<MessageLabel>();

    /// <summary>
    /// Gets the saved remote image rules.
    /// </summary>
    internal DbSet<RemoteImageRuleEntry> RemoteImageRules => Set<RemoteImageRuleEntry>();

    /// <summary>
    /// Gets the contacts of the address book.
    /// </summary>
    public DbSet<Contact> Contacts => Set<Contact>();

    /// <summary>
    /// Gets the addresses mail was sent to from this application.
    /// </summary>
    internal DbSet<SentRecipient> SentRecipients => Set<SentRecipient>();

    /// <summary>
    /// Gets the addresses the user removed from the collected ones.
    /// </summary>
    internal DbSet<DismissedAddressEntry> DismissedAddresses => Set<DismissedAddressEntry>();

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new MailAccountConfiguration());
        modelBuilder.ApplyConfiguration(new LabelConfiguration());
        modelBuilder.ApplyConfiguration(new MessageLabelConfiguration());
        modelBuilder.ApplyConfiguration(new RemoteImageRuleConfiguration());
        modelBuilder.ApplyConfiguration(new ContactConfiguration());
        modelBuilder.ApplyConfiguration(new SentRecipientConfiguration());
        modelBuilder.ApplyConfiguration(new DismissedAddressConfiguration());
    }
}
