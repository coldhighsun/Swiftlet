using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Labels;
using Swiftlet.Infrastructure.Persistence.Configurations;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// The EF Core context of the global store: the account registry, the labels and the links between labels and
/// messages. The mail itself lives in one <see cref="AccountDbContext"/> database per account.
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
    }
}
