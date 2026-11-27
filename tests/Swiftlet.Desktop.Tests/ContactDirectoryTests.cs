using AwesomeAssertions;
using Swiftlet.Application.Contacts;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="ContactDirectory"/> and <see cref="AddressText"/>.
/// </summary>
public class ContactDirectoryTests
{
    /// <summary>
    /// Creates a dispatcher answering with a fixed address book.
    /// </summary>
    /// <returns>The dispatcher.</returns>
    private static FakeDispatcher Dispatcher()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<GetContactNamesQuery>(_ => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["ann@x.com"] = "Ann",
            ["nameless@x.com"] = string.Empty,
        });
        return dispatcher;
    }

    /// <summary>
    /// Verifies the address book is read once and answers look-ups.
    /// </summary>
    [Fact]
    public async Task EnsureLoadedAsync_CalledTwice_LoadsOnce()
    {
        var dispatcher = Dispatcher();
        var directory = new ContactDirectory(dispatcher);

        await directory.EnsureLoadedAsync();
        await directory.EnsureLoadedAsync();

        dispatcher.Sent<GetContactNamesQuery>().Should().ContainSingle();
        directory.NameOf("ANN@x.com").Should().Be("Ann");
        directory.Contains("nameless@x.com").Should().BeTrue();
        directory.NameOf("nameless@x.com").Should().BeNull();
        directory.Contains("other@x.com").Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failed reload keeps the names loaded before.
    /// </summary>
    [Fact]
    public async Task ReloadAsync_Fails_KeepsPreviousNames()
    {
        var dispatcher = Dispatcher();
        var directory = new ContactDirectory(dispatcher);
        await directory.ReloadAsync();
        dispatcher.On<GetContactNamesQuery>(_ => throw new InvalidOperationException("broken"));

        await directory.ReloadAsync();

        directory.NameOf("ann@x.com").Should().Be("Ann");
    }

    /// <summary>
    /// Verifies the contact name wins in the full and compact texts, and others keep what the mail said.
    /// </summary>
    /// <param name="name">The name in the mail.</param>
    /// <param name="address">The address.</param>
    /// <param name="full">The expected full text.</param>
    /// <param name="compact">The expected compact text.</param>
    [Theory]
    [InlineData("Annie", "ann@x.com", "Ann <ann@x.com>", "Ann <ann@x.com>")]
    [InlineData("Bob", "bob@x.com", "Bob <bob@x.com>", "Bob")]
    [InlineData(" ", "bob@x.com", "bob@x.com", "bob@x.com")]
    [InlineData("Nick", "nameless@x.com", "Nick <nameless@x.com>", "Nick")]
    public async Task AddressText_ContactOrNot_FormatsName(string name, string address, string full, string compact)
    {
        var directory = new ContactDirectory(Dispatcher());
        await directory.ReloadAsync();

        AddressText.Full(directory, name, address).Should().Be(full);
        AddressText.Compact(directory, name, address).Should().Be(compact);
    }
}
