using AwesomeAssertions;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Pins the flag model of <see cref="FakeImapSession"/> that the synchronization tests rely on.
/// </summary>
public sealed class FakeImapFlagTests
{
    /// <summary>
    /// Creates a session holding messages 1..3 in INBOX.
    /// </summary>
    /// <returns>The session.</returns>
    private static FakeImapSession CreateSession()
    {
        var session = new FakeImapSession();
        session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2), TestData.Remote(3)];
        return session;
    }

    /// <summary>
    /// Fetching flags returns only UIDs at or above the bound, in order, with unflagged defaults.
    /// </summary>
    [Fact]
    public async Task FetchFlagsAsync_MinUid_ReturnsOrderedStatesAtOrAboveBound()
    {
        var session = CreateSession();

        var states = await session.FetchFlagsAsync("INBOX", 2, TestContext.Current.CancellationToken);

        states.Should().Equal(new RemoteFlagState(2, false, false, false), new RemoteFlagState(3, false, false, false));
        session.FetchFlagsCalls.Should().Equal(("INBOX", 2u));
    }

    /// <summary>
    /// Setting flags updates the model and ignores UIDs that do not exist.
    /// </summary>
    [Fact]
    public async Task SetFlagsAsync_UpdatesExistingAndIgnoresMissingUids()
    {
        var session = CreateSession();

        await session.SetFlagsAsync(
            "INBOX",
            [new FlagUpdate(1, true, true), new FlagUpdate(99, true, true)],
            TestContext.Current.CancellationToken);

        session.GetServerFlags("INBOX", 1).Should().Be((true, true, false));
        var states = await session.FetchFlagsAsync("INBOX", 0, TestContext.Current.CancellationToken);
        states.Select(s => s.Uid).Should().Equal(1u, 2u, 3u);
        session.SetFlagsCalls.Should().ContainSingle();
    }

    /// <summary>
    /// A message marked deleted is still listed with its deleted flag; a removed one disappears.
    /// </summary>
    [Fact]
    public async Task MarkDeletedAndRemove_ReflectedInFetchedFlags()
    {
        var session = CreateSession();

        session.MarkDeletedOnServer("INBOX", 2);
        session.RemoveFromServer("INBOX", 3);
        var states = await session.FetchFlagsAsync("INBOX", 0, TestContext.Current.CancellationToken);

        states.Should().Equal(new RemoteFlagState(1, false, false, false), new RemoteFlagState(2, false, false, true));
        session.Messages["INBOX"].Select(m => m.Uid).Should().Equal(1u, 2u);
    }

    /// <summary>
    /// Configured failures are thrown by the respective method.
    /// </summary>
    [Fact]
    public async Task FlagOperations_WithConfiguredFailure_Throw()
    {
        var session = CreateSession();
        session.SetFlagsFailure = new IOException("set");
        session.FetchFlagsFailure = new IOException("fetch");

        var set = () => session.SetFlagsAsync("INBOX", [new FlagUpdate(1, true, false)], TestContext.Current.CancellationToken);
        var fetch = () => session.FetchFlagsAsync("INBOX", 0, TestContext.Current.CancellationToken);

        await set.Should().ThrowAsync<IOException>();
        await fetch.Should().ThrowAsync<IOException>();
    }
}
