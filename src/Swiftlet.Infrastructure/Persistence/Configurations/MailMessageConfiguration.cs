using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="MailMessage"/>.
/// </summary>
internal sealed class MailMessageConfiguration : IEntityTypeConfiguration<MailMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MailMessage> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Subject).IsRequired();
        builder.Property(m => m.FromName).IsRequired();
        builder.Property(m => m.FromAddress).IsRequired().HasMaxLength(320);
        builder.Property(m => m.Recipients).IsRequired();
        builder.Property(m => m.BodyText).IsRequired();
        builder.Property(m => m.Preview).IsRequired().HasMaxLength(256);
        builder.HasIndex(m => new { m.FolderId, m.Uid }).IsUnique();
        builder.HasIndex(m => new { m.AccountId, m.SentAt });
        builder.HasOne<MailFolder>().WithMany().HasForeignKey(m => m.FolderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(m => m.Attachments).WithOne().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(m => m.Labels)
            .WithMany()
            .UsingEntity(
                "MessageLabels",
                right => right.HasOne(typeof(Label)).WithMany().HasForeignKey("LabelId").OnDelete(DeleteBehavior.Cascade),
                left => left.HasOne(typeof(MailMessage)).WithMany().HasForeignKey("MessageId").OnDelete(DeleteBehavior.Cascade));
        builder.Navigation(m => m.Labels).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
