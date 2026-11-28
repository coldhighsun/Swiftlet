using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using MailKit.Net.Imap;
using MailKit.Security;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the commands <see cref="MailKitImapSession"/> sends, against a scripted server on the loopback address.
/// </summary>
public class MailKitImapSessionTests
{
    /// <summary>
    /// Verifies an open-ended search ends in <c>*</c>, because some servers (QQ Mail) answer BAD to a UID near the 32-bit limit.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ListUidsBetweenAsync_NoUpperBound_SearchesUpToAsterisk()
    {
        // Arrange: "n:*" also matches the last message when it is older than n.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new ScriptedImapServer("* SEARCH 5 12 15");
        await using var session = await ConnectAsync(server.Port, cancellationToken);

        // Act
        var found = await session.ListUidsBetweenAsync("INBOX", 10, uint.MaxValue, cancellationToken);

        // Assert
        server.SearchCommands.Should().ContainSingle().Which.Should().Contain("UID 11:* ");
        found.Should().Equal(12u, 15u);
    }

    /// <summary>
    /// Verifies a bounded search names the last UID below the bound.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ListUidsBetweenAsync_UpperBound_SearchesBelowIt()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new ScriptedImapServer("* SEARCH 12");
        await using var session = await ConnectAsync(server.Port, cancellationToken);

        // Act
        var found = await session.ListUidsBetweenAsync("INBOX", 10, 20, cancellationToken);

        // Assert
        server.SearchCommands.Should().ContainSingle().Which.Should().Contain("UID 11:19 ");
        found.Should().Equal(12u);
    }

    /// <summary>
    /// Opens a session on the scripted server.
    /// </summary>
    /// <param name="port">The port the server listens on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The authenticated session.</returns>
    private static async Task<MailKitImapSession> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, cancellationToken);
        await client.AuthenticateAsync("me@example.com", "secret", cancellationToken);
        return new MailKitImapSession(client, new CapturingLogger<MailKitImapSession>());
    }

    /// <summary>
    /// A minimal IMAP server for one connection that records the searches it receives.
    /// </summary>
    private sealed class ScriptedImapServer : IAsyncDisposable
    {
        /// <summary>
        /// Accepts the connection.
        /// </summary>
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        /// <summary>
        /// Stops the server.
        /// </summary>
        private readonly CancellationTokenSource _stop = new();

        /// <summary>
        /// The untagged answer to every search.
        /// </summary>
        private readonly string _searchResponse;

        /// <summary>
        /// Serves the connection.
        /// </summary>
        private readonly Task _serving;

        /// <summary>
        /// Starts listening.
        /// </summary>
        /// <param name="searchResponse">The untagged answer to every search.</param>
        public ScriptedImapServer(string searchResponse)
        {
            _searchResponse = searchResponse;
            _listener.Start();
            _serving = ServeAsync(_stop.Token);
        }

        /// <summary>
        /// The port the server listens on.
        /// </summary>
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>
        /// The search commands received, each ending in a space so a range can be matched exactly.
        /// </summary>
        public List<string> SearchCommands { get; } = [];

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _serving;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Expected: the test is over and the client may have dropped the connection.
            }

            _listener.Stop();
            _stop.Dispose();
        }

        /// <summary>
        /// Answers the commands of one client until it disconnects.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the connection ends.</returns>
        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("* OK [CAPABILITY IMAP4rev1] ready");

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                var parts = line.Split(' ', 3);
                var command = parts[1].ToUpperInvariant();
                if (command == "CAPABILITY")
                {
                    await writer.WriteLineAsync("* CAPABILITY IMAP4rev1");
                }
                else if (command == "LIST")
                {
                    await writer.WriteLineAsync(parts[2].EndsWith("\"\"", StringComparison.Ordinal)
                        ? "* LIST (\\Noselect) \"/\" \"\""
                        : "* LIST () \"/\" INBOX");
                }
                else if (command == "EXAMINE")
                {
                    await writer.WriteLineAsync("* 3 EXISTS");
                    await writer.WriteLineAsync("* OK [UIDVALIDITY 1] valid");
                }
                else if (command == "UID")
                {
                    SearchCommands.Add(line + " ");
                    await writer.WriteLineAsync(_searchResponse);
                }
                else if (command == "LOGOUT")
                {
                    await writer.WriteLineAsync("* BYE");
                }

                // MailKit only treats a folder as open when the answer to EXAMINE names the access mode.
                var access = command == "EXAMINE" ? "[READ-ONLY] " : string.Empty;
                await writer.WriteLineAsync($"{parts[0]} OK {access}done");
            }
        }
    }
}
