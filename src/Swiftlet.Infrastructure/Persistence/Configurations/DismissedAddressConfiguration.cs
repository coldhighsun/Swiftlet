using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="DismissedAddressEntry"/>.
/// </summary>
internal sealed class DismissedAddressConfiguration : IEntityTypeConfiguration<DismissedAddressEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DismissedAddressEntry> builder)
    {
        builder.ToTable("DismissedAddresses");
        builder.HasKey(a => a.Address);
        builder.Property(a => a.Address).IsRequired().HasMaxLength(Contact.MaxEmailLength);
    }
}
