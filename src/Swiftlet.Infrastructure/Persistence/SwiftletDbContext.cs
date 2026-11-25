using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// The EF Core context of the local mail cache.
/// </summary>
/// <param name="options">The context options.</param>
public sealed class SwiftletDbContext(DbContextOptions<SwiftletDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Gets the accounts.
    /// </summary>
    public DbSet<MailAccount> Accounts => Set<MailAccount>();

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
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SwiftletDbContext).Assembly);
    }
}
