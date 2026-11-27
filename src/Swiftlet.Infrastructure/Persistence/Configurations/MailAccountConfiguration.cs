using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="MailAccount"/>.
/// </summary>
internal sealed class MailAccountConfiguration : IEntityTypeConfiguration<MailAccount>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MailAccount> builder)
    {
        builder.ToTable("Accounts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(a => a.EmailAddress).IsRequired().HasMaxLength(320);
        builder.Property(a => a.ArchiveFolderName).HasMaxLength(1024);
        builder.HasIndex(a => a.EmailAddress).IsUnique();
        builder.Property(a => a.AuthMethod).HasConversion<int>();
        builder.OwnsOne(a => a.Imap, imap => imap.Property(e => e.Host).IsRequired().HasMaxLength(255));
        builder.OwnsOne(a => a.Smtp, smtp => smtp.Property(e => e.Host).IsRequired().HasMaxLength(255));
        builder.Navigation(a => a.Imap).IsRequired();
        builder.Navigation(a => a.Smtp).IsRequired();
    }
}
