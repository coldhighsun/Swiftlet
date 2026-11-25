using AwesomeAssertions;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests the baseline decision used by the IDLE wait.
/// </summary>
public sealed class FolderChangeTrackerTests
{
    /// <summary>
    /// The first observation of a folder counts as a change and records the baseline.
    /// </summary>
    [Fact]
    public void Observe_FirstCall_ReturnsTrue()
    {
        // Arrange
        var tracker = new FolderChangeTracker();

        // Act
        var changed = tracker.Observe("INBOX", 5, 6);

        // Assert
        changed.Should().BeTrue();
    }

    /// <summary>
    /// An identical state is not a change.
    /// </summary>
    [Fact]
    public void Observe_SameState_ReturnsFalse()
    {
        // Arrange
        var tracker = new FolderChangeTracker();
        tracker.Observe("INBOX", 5, 6);

        // Act
        var changed = tracker.Observe("INBOX", 5, 6);

        // Assert
        changed.Should().BeFalse();
    }

    /// <summary>
    /// A different count is a change and becomes the new baseline.
    /// </summary>
    [Fact]
    public void Observe_CountDiffers_ReturnsTrueOnceThenFalse()
    {
        // Arrange
        var tracker = new FolderChangeTracker();
        tracker.Observe("INBOX", 5, 6);

        // Act
        var first = tracker.Observe("INBOX", 6, 6);
        var second = tracker.Observe("INBOX", 6, 6);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    /// <summary>
    /// A different UIDNEXT with the same count (one arrived, one expunged) is a change.
    /// </summary>
    [Fact]
    public void Observe_UidNextDiffers_ReturnsTrue()
    {
        // Arrange
        var tracker = new FolderChangeTracker();
        tracker.Observe("INBOX", 5, 6);

        // Act
        var changed = tracker.Observe("INBOX", 5, 7);

        // Assert
        changed.Should().BeTrue();
    }

    /// <summary>
    /// Folders are tracked independently.
    /// </summary>
    [Fact]
    public void Observe_OtherFolder_IsItsOwnFirstObservation()
    {
        // Arrange
        var tracker = new FolderChangeTracker();
        tracker.Observe("INBOX", 5, 6);

        // Act
        var other = tracker.Observe("Other", 9, 10);
        var inbox = tracker.Observe("INBOX", 5, 6);

        // Assert
        other.Should().BeTrue();
        inbox.Should().BeFalse();
    }

    /// <summary>
    /// With an unknown UIDNEXT in the baseline and the same count, nothing changed.
    /// </summary>
    [Fact]
    public void Observe_BaselineUidNextUnknownSameCount_ReturnsFalse()
    {
        // Arrange
        var tracker = new FolderChangeTracker();
        tracker.Observe("INBOX", 5, 6);
        tracker.Observe("INBOX", 6, 0);

        // Act
        var changed = tracker.Observe("INBOX", 6, 7);

        // Assert
        changed.Should().BeFalse();
    }

    /// <summary>
    /// With an unknown UIDNEXT in the baseline a different count is still a change.
    /// </summary>
    [Fact]
    public void Observe_BaselineUidNextUnknownCountDiffers_ReturnsTrue()
    {
        // Arrange
        var tracker = new FolderChangeTracker();
        tracker.Observe("INBOX", 5, 6);
        tracker.Observe("INBOX", 6, 0);

        // Act
        var changed = tracker.Observe("INBOX", 7, 8);

        // Assert
        changed.Should().BeTrue();
    }

    /// <summary>
    /// Once the unknown UIDNEXT is resolved, a later UIDNEXT-only change is detected again.
    /// </summary>
    [Fact]
    public void Observe_AfterUnknownUidNextResolved_UidNextOnlyChangeReturnsTrue()
    {
        // Arrange
        var tracker = new FolderChangeTracker();
        tracker.Observe("INBOX", 5, 6);
        tracker.Observe("INBOX", 6, 0);
        tracker.Observe("INBOX", 6, 7);

        // Act
        var changed = tracker.Observe("INBOX", 6, 8);

        // Assert
        changed.Should().BeTrue();
    }
}
