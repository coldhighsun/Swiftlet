using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="RemoteImageRuleEntry"/>.
/// </summary>
internal sealed class RemoteImageRuleConfiguration : IEntityTypeConfiguration<RemoteImageRuleEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RemoteImageRuleEntry> builder)
    {
        builder.ToTable("RemoteImageRules");
        builder.HasKey(r => r.Rule);
        builder.Property(r => r.Rule).IsRequired().HasMaxLength(320);
    }
}
