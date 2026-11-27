using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="Contact"/>; the address and phone lists are stored as JSON text.
/// </summary>
internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("Contacts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.FirstName).IsRequired();
        builder.Property(c => c.LastName).IsRequired();
        builder.Ignore(c => c.DisplayName);
        builder.Property(c => c.Company).IsRequired();
        builder.Property(c => c.Note).IsRequired();
        builder.PrimitiveCollection(c => c.Emails).IsRequired();
        builder.PrimitiveCollection(c => c.Phones).IsRequired();
    }
}
