using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="MailAttachment"/>.
/// </summary>
internal sealed class MailAttachmentConfiguration : IEntityTypeConfiguration<MailAttachment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MailAttachment> builder)
    {
        builder.ToTable("Attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(512);
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(256);
    }
}
