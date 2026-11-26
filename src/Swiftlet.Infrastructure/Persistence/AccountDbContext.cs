using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Configurations;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// The EF Core context of one account's mail cache: its folders, messages and attachments. Every account has its own
/// database file, created by <see cref="IAccountDbContextFactory"/>.
/// </summary>
/// <param name="options">The context options.</param>
public sealed class AccountDbContext(DbContextOptions<AccountDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Gets the folders.
    /// </summary>
    public DbSet<MailFolder> Folders => Set<MailFolder>();

    /// <summary>
    /// Gets the messages.
    /// </summary>
    public DbSet<MailMessage> Messages => Set<MailMessage>();

    /// <summary>
    /// Gets the attachments.
    /// </summary>
    public DbSet<MailAttachment> Attachments => Set<MailAttachment>();

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
        modelBuilder.ApplyConfiguration(new MailFolderConfiguration());
        modelBuilder.ApplyConfiguration(new MailMessageConfiguration());
        modelBuilder.ApplyConfiguration(new MailAttachmentConfiguration());
    }
}
