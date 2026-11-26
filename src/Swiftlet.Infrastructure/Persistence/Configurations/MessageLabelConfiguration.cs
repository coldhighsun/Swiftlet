using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Labels;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="MessageLabel"/>.
/// </summary>
internal sealed class MessageLabelConfiguration : IEntityTypeConfiguration<MessageLabel>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MessageLabel> builder)
    {
        builder.ToTable("MessageLabels");
        builder.HasKey(l => new { l.MessageId, l.LabelId });
        builder.HasIndex(l => l.LabelId);
        builder.HasIndex(l => l.AccountId);
        builder.HasOne<Label>().WithMany().HasForeignKey(l => l.LabelId).OnDelete(DeleteBehavior.Cascade);
    }
}
