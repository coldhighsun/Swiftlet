using AwesomeAssertions;
using Swiftlet.Application.Logging;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="LogNames"/>.
/// </summary>
public class LogNamesTests
{
    /// <summary>
    /// Verifies remembered names are returned, and the latest one wins.
    /// </summary>
    [Fact]
    public void Remember_ThenResolve_ReturnsLatestName()
    {
        var names = new LogNames();
        var id = Guid.NewGuid();

        names.RememberAccount(id, "me@example.com");
        names.RememberFolder(id, "INBOX");
        names.RememberLabel(id, "Old");
        names.RememberLabel(id, "New");

        names.Account(id).Should().Be("me@example.com");
        names.Folder(id).Should().Be("INBOX");
        names.Label(id).Should().Be("New");
    }

    /// <summary>
    /// Verifies an unknown identifier is shown shortened.
    /// </summary>
    [Fact]
    public void Resolve_Unknown_ReturnsShortIdentifier()
    {
        var names = new LogNames();
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");

        names.Account(id).Should().Be("01234567");
        names.Folder(id).Should().Be("01234567");
        names.Label(id).Should().Be("01234567");
    }
}
