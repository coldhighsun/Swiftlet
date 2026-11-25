using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Labels;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="Label"/>.
/// </summary>
internal sealed class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Label> builder)
    {
        builder.ToTable("Labels");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Name).IsRequired().HasMaxLength(Label.MaxNameLength);
        builder.Property(l => l.Color).IsRequired().HasMaxLength(7);
    }
}
