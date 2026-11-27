using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="SentRecipient"/>.
/// </summary>
internal sealed class SentRecipientConfiguration : IEntityTypeConfiguration<SentRecipient>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SentRecipient> builder)
    {
        builder.ToTable("SentRecipients");
        builder.HasKey(r => new { r.AccountId, r.Address });
        builder.Property(r => r.Address).IsRequired().HasMaxLength(Contact.MaxEmailLength);
        builder.Property(r => r.Name).IsRequired();
        builder.Property(r => r.LastUsedAt);
    }
}
