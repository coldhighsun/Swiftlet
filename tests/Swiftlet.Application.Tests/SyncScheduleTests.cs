using AwesomeAssertions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="SyncSchedule"/>.
/// </summary>
public class SyncScheduleTests
{
    /// <summary>
    /// Creates an account with the given sync interval.
    /// </summary>
    /// <param name="interval">The sync interval.</param>
    /// <returns>The account.</returns>
    private static MailAccount CreateAccount(TimeSpan interval)
    {
        var account = MailAccount.Create(
            "Me",
            $"{Guid.NewGuid():N}@example.com",
            AuthMethod.Password,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UtcNow);
        account.ChangeSyncInterval(interval);
        return account;
    }

    /// <summary>
    /// Verifies an account that was never attempted is due immediately.
    /// </summary>
    [Fact]
    public void GetDue_NeverAttempted_ReturnsAccount()
    {
        var schedule = new SyncSchedule();
        var account = CreateAccount(TimeSpan.FromMinutes(5));

        schedule.GetDue([account], DateTimeOffset.UtcNow).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies an account is not due before its interval elapses and is due afterwards.
    /// </summary>
    [Fact]
    public void GetDue_IntervalElapses_BecomesDue()
    {
        var schedule = new SyncSchedule();
        var account = CreateAccount(TimeSpan.FromMinutes(5));
        var start = DateTimeOffset.UtcNow;
        schedule.MarkAttempted(account.Id, start);

        schedule.GetDue([account], start.AddMinutes(4)).Should().BeEmpty();
        schedule.GetDue([account], start.AddMinutes(5)).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies each account is scheduled by its own interval.
    /// </summary>
    [Fact]
    public void GetDue_DifferentIntervals_ReturnsOnlyElapsedAccounts()
    {
        var schedule = new SyncSchedule();
        var fast = CreateAccount(TimeSpan.FromMinutes(1));
        var slow = CreateAccount(TimeSpan.FromMinutes(30));
        var start = DateTimeOffset.UtcNow;
        schedule.MarkAttempted(fast.Id, start);
        schedule.MarkAttempted(slow.Id, start);

        var due = schedule.GetDue([fast, slow], start.AddMinutes(2));

        due.Should().ContainSingle().Which.Id.Should().Be(fast.Id);
    }
}
