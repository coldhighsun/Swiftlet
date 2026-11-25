using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="MailFolder"/>.
/// </summary>
internal sealed class MailFolderConfiguration : IEntityTypeConfiguration<MailFolder>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MailFolder> builder)
    {
        builder.ToTable("Folders");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(255);
        builder.Property(f => f.FullName).IsRequired().HasMaxLength(1024);
        builder.Property(f => f.Role).HasConversion<int>();
        builder.HasIndex(f => new { f.AccountId, f.FullName }).IsUnique();
        builder.HasOne<MailAccount>().WithMany().HasForeignKey(f => f.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}
