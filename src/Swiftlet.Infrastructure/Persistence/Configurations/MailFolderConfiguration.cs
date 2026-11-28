using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping of <see cref="MailFolder"/>; the Trash checkpoints are stored as JSON text.
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
        builder.Property(f => f.TrashCheckpoints)
            .IsRequired()
            .HasConversion(
                checkpoints => Serialize(checkpoints),
                json => Deserialize(json),
                new ValueComparer<IReadOnlyList<UidCheckpoint>>(
                    (left, right) => left!.SequenceEqual(right!),
                    checkpoints => checkpoints.Aggregate(0, (hash, c) => HashCode.Combine(hash, c)),
                    checkpoints => checkpoints.ToList()));
        builder.HasIndex(f => new { f.AccountId, f.FullName }).IsUnique();
    }

    /// <summary>
    /// Writes checkpoints as JSON.
    /// </summary>
    /// <param name="checkpoints">The checkpoints.</param>
    /// <returns>The JSON text.</returns>
    private static string Serialize(IReadOnlyList<UidCheckpoint> checkpoints)
    {
        return JsonSerializer.Serialize(checkpoints);
    }

    /// <summary>
    /// Reads checkpoints written by <see cref="Serialize"/>.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The checkpoints.</returns>
    private static List<UidCheckpoint> Deserialize(string json)
    {
        return JsonSerializer.Deserialize<List<UidCheckpoint>>(json) ?? [];
    }
}
