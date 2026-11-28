using AwesomeAssertions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Domain.Tests;

/// <summary>
/// Tests for the retention setting of an account and the UID checkpoints of its Trash folder.
/// </summary>
public sealed class TrashRetentionTests
{
    /// <summary>
    /// The time cleaning up was switched on.
    /// </summary>
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The retention used by the folder tests.
    /// </summary>
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    /// <summary>
    /// A new account does not clean up.
    /// </summary>
    [Fact]
    public void Create_NewAccount_HasNoTrashRetention()
    {
        // Act
        var account = CreateAccount();

        // Assert
        account.TrashRetentionDays.Should().BeNull();
        account.TrashCleanupEnabledAt.Should().BeNull();
    }

    /// <summary>
    /// Switching cleaning up on records when.
    /// </summary>
    [Fact]
    public void ChangeTrashRetention_SwitchedOn_RecordsTheTime()
    {
        // Arrange
        var account = CreateAccount();

        // Act
        account.ChangeTrashRetention(30, Start);

        // Assert
        account.TrashRetentionDays.Should().Be(30);
        account.TrashCleanupEnabledAt.Should().Be(Start);
    }

    /// <summary>
    /// A different number of days keeps the time cleaning up was switched on.
    /// </summary>
    [Fact]
    public void ChangeTrashRetention_OtherDays_KeepsTheTime()
    {
        // Arrange
        var account = CreateAccount();
        account.ChangeTrashRetention(30, Start);

        // Act
        account.ChangeTrashRetention(7, Start.AddDays(10));

        // Assert
        account.TrashRetentionDays.Should().Be(7);
        account.TrashCleanupEnabledAt.Should().Be(Start);
    }

    /// <summary>
    /// Switching off forgets the time, so switching on again starts counting anew.
    /// </summary>
    [Fact]
    public void ChangeTrashRetention_OffAndOnAgain_StartsAnew()
    {
        // Arrange
        var account = CreateAccount();
        account.ChangeTrashRetention(30, Start);

        // Act
        account.ChangeTrashRetention(null, Start.AddDays(1));
        var whileOff = account.TrashCleanupEnabledAt;
        account.ChangeTrashRetention(30, Start.AddDays(2));

        // Assert
        whileOff.Should().BeNull();
        account.TrashCleanupEnabledAt.Should().Be(Start.AddDays(2));
    }

    /// <summary>
    /// A retention shorter than a day is rejected.
    /// </summary>
    /// <param name="days">The number of days.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ChangeTrashRetention_LessThanADay_Throws(int days)
    {
        // Arrange
        var account = CreateAccount();

        // Act
        var change = () => account.ChangeTrashRetention(days, Start);

        // Assert
        change.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The first look only records what is there.
    /// </summary>
    [Fact]
    public void ObserveTrash_FirstLook_RecordsACheckpointAndExpiresNothing()
    {
        // Arrange
        var folder = CreateTrash();

        // Act
        var expired = folder.ObserveTrash(10, Start, Start, Week);

        // Assert
        expired.Should().Be(0);
        folder.TrashCheckpoints.Should().Equal(new UidCheckpoint(10, Start));
    }

    /// <summary>
    /// A message expires exactly when the retention has passed since it was first seen.
    /// </summary>
    [Fact]
    public void ObserveTrash_RetentionPassed_ExpiresWhatWasSeenThen()
    {
        // Arrange
        var folder = CreateTrash();
        folder.ObserveTrash(10, Start, Start, Week);

        // Act
        var before = folder.ObserveTrash(10, Start + Week - TimeSpan.FromSeconds(1), Start, Week);
        var after = folder.ObserveTrash(12, Start + Week, Start, Week);

        // Assert
        before.Should().Be(0);
        after.Should().Be(10);
    }

    /// <summary>
    /// Checkpoints are at least a day apart, so their number stays small.
    /// </summary>
    [Fact]
    public void ObserveTrash_WithinADay_AddsNoCheckpoint()
    {
        // Arrange
        var folder = CreateTrash();
        folder.ObserveTrash(10, Start, Start, Week);

        // Act
        folder.ObserveTrash(11, Start.AddHours(23), Start, Week);
        folder.ObserveTrash(12, Start.AddDays(1), Start, Week);

        // Assert
        folder.TrashCheckpoints.Should().Equal(new UidCheckpoint(10, Start), new UidCheckpoint(12, Start.AddDays(1)));
    }

    /// <summary>
    /// Nothing new in the folder adds no checkpoint, which would restart the count of what is there.
    /// </summary>
    [Fact]
    public void ObserveTrash_NoNewMessage_AddsNoCheckpoint()
    {
        // Arrange
        var folder = CreateTrash();
        folder.ObserveTrash(10, Start, Start, Week);

        // Act
        folder.ObserveTrash(9, Start.AddDays(2), Start, Week);

        // Assert
        folder.TrashCheckpoints.Should().Equal(new UidCheckpoint(10, Start));
    }

    /// <summary>
    /// An empty folder is not recorded.
    /// </summary>
    [Fact]
    public void ObserveTrash_EmptyFolder_AddsNoCheckpoint()
    {
        // Arrange
        var folder = CreateTrash();

        // Act
        folder.ObserveTrash(0, Start, Start, Week);

        // Assert
        folder.TrashCheckpoints.Should().BeEmpty();
    }

    /// <summary>
    /// Only the newest expired checkpoint is kept; it still covers messages whose deletion failed.
    /// </summary>
    [Fact]
    public void ObserveTrash_SeveralExpired_KeepsTheNewestOfThem()
    {
        // Arrange
        var folder = CreateTrash();
        folder.ObserveTrash(10, Start, Start, Week);
        folder.ObserveTrash(20, Start.AddDays(1), Start, Week);
        folder.ObserveTrash(30, Start.AddDays(5), Start, Week);

        // Act
        var expired = folder.ObserveTrash(30, Start.AddDays(9), Start, Week);
        var again = folder.ObserveTrash(30, Start.AddDays(10), Start, Week);

        // Assert
        expired.Should().Be(20);
        again.Should().Be(20);
        folder.TrashCheckpoints.Should().Equal(new UidCheckpoint(20, Start.AddDays(1)), new UidCheckpoint(30, Start.AddDays(5)));
    }

    /// <summary>
    /// Checkpoints recorded before cleaning up was last switched on do not count.
    /// </summary>
    [Fact]
    public void ObserveTrash_CheckpointOlderThanTheSwitch_IsDropped()
    {
        // Arrange
        var folder = CreateTrash();
        folder.ObserveTrash(10, Start, Start, Week);
        var switchedOnAgain = Start.AddDays(30);

        // Act
        var expired = folder.ObserveTrash(10, switchedOnAgain, switchedOnAgain, Week);

        // Assert
        expired.Should().Be(0);
        folder.TrashCheckpoints.Should().Equal(new UidCheckpoint(10, switchedOnAgain));
    }

    /// <summary>
    /// A new UIDVALIDITY makes the recorded UIDs meaningless.
    /// </summary>
    [Fact]
    public void UpdateUidValidity_Changed_ForgetsTheCheckpoints()
    {
        // Arrange
        var folder = CreateTrash();
        folder.UpdateUidValidity(1);
        folder.ObserveTrash(10, Start, Start, Week);

        // Act
        folder.UpdateUidValidity(2);

        // Assert
        folder.TrashCheckpoints.Should().BeEmpty();
    }

    /// <summary>
    /// Creates an account.
    /// </summary>
    /// <returns>The account.</returns>
    private static MailAccount CreateAccount()
    {
        return MailAccount.Create(
            "Me",
            "me@example.com",
            AuthMethod.Password,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            Start);
    }

    /// <summary>
    /// Creates a Trash folder.
    /// </summary>
    /// <returns>The folder.</returns>
    private static MailFolder CreateTrash()
    {
        return MailFolder.Create(Guid.NewGuid(), "Trash", "Trash", FolderRole.Trash);
    }
}
