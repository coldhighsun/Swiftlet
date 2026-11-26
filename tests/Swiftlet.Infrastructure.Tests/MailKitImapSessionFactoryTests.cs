using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for how <see cref="MailKitImapSessionFactory"/> logs connection problems.
/// </summary>
public class MailKitImapSessionFactoryTests
{
    /// <summary>
    /// Verifies a connection the caller cancelled is logged as information without an exception, not as a warning with a stack trace.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ConnectAsync_CancelledByCaller_LogsNoWarning()
    {
        // Arrange
        var logger = new CapturingLogger<MailKitImapSession>();
        var factory = new MailKitImapSessionFactory(new InMemorySecretStore(), new NoTokenProvider(), logger);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var act = () => factory.ConnectAsync(TestData.Account("me@example.com"), cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logger.Entries.Should().NotContain(e => e.Exception != null);
    }

    /// <summary>
    /// Verifies a genuine connection failure is still logged as a warning with its exception.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task ConnectAsync_ServerUnreachable_LogsWarningWithException()
    {
        // Arrange: a port that was just free and has no listener.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var account = MailAccount.Create(
            "Me",
            "me@example.com",
            AuthMethod.Password,
            new ServerEndpoint("127.0.0.1", port, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<MailKitImapSession>();
        var factory = new MailKitImapSessionFactory(new InMemorySecretStore(), new NoTokenProvider(), logger);

        // Act
        var act = () => factory.ConnectAsync(account, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<Exception>();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception != null);
    }

    /// <summary>
    /// An access token provider for password accounts, which never asks for a token.
    /// </summary>
    private sealed class NoTokenProvider : IAccessTokenProvider
    {
        /// <inheritdoc />
        public Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
