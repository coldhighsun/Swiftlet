using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using MailKit;
using MailKit.Security;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.OAuth;
using Swiftlet.Infrastructure.Resilience;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="TransientErrors"/>, <see cref="RetryPipelines"/> and the retries of the token refresh.
/// </summary>
public class ResilienceTests
{
    /// <summary>
    /// Gets failures that may pass by themselves.
    /// </summary>
    public static TheoryData<Exception> Passing =>
    [
        new IOException(),
        new SocketException(),
        new TimeoutException(),
        new ServiceNotConnectedException(),
        new HttpRequestException("offline"),
        new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable),
        new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests),
        new TaskCanceledException("timed out", new TimeoutException()),
    ];

    /// <summary>
    /// Gets failures that repeating cannot fix.
    /// </summary>
    public static TheoryData<Exception> Lasting =>
    [
        new AuthenticationException(),
        new OAuthReauthenticationRequiredException("me@example.com"),
        new MailOperationRejectedException(),
        new NotSupportedException(),
        new OperationCanceledException(),
        new InvalidOperationException(),
        new HttpRequestException("bad", null, HttpStatusCode.BadRequest),
    ];

    /// <summary>
    /// Verifies passing failures are retried.
    /// </summary>
    /// <param name="error">The failure.</param>
    [Theory]
    [MemberData(nameof(Passing))]
    public void IsTransient_PassingFailure_ReturnsTrue(Exception error)
    {
        TransientErrors.IsTransient(error).Should().BeTrue();
    }

    /// <summary>
    /// Verifies lasting failures, a rejected password among them, are not retried.
    /// </summary>
    /// <param name="error">The failure.</param>
    [Theory]
    [MemberData(nameof(Lasting))]
    public void IsTransient_LastingFailure_ReturnsFalse(Exception error)
    {
        TransientErrors.IsTransient(error).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a limited pipeline throws a lasting failure at once.
    /// </summary>
    [Fact]
    public async Task Transient_LastingFailure_IsNotRepeated()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var pipeline = RetryPipelines.Transient(new VirtualTimeProvider(), 3, TimeSpan.FromSeconds(1), null, "test");
        var attempts = 0;

        // Act
        var act = async () => await pipeline.ExecuteAsync<int>(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException();
            },
            ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }

    /// <summary>
    /// Verifies a busy token endpoint is asked again and the refresh then succeeds.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_EndpointBusy_RetriesAndSucceeds()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var clock = new VirtualTimeProvider();
        using var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = CreateClient(http, clock);

        // Act
        var refresh = client.RefreshAsync(OAuthProvider.Google, "refresh", ct);
        await WatcherHarness.WaitUntilAsync(() => clock.PendingTimerCount == 1, ct);
        clock.Advance(RetryPipelines.TokenRefreshDelay);
        var tokens = await refresh;

        // Assert
        tokens.AccessToken.Should().Be("access");
        handler.Calls.Should().Be(2);
    }

    /// <summary>
    /// Verifies a code exchange is never repeated, because a code works only once.
    /// </summary>
    [Fact]
    public async Task ExchangeCodeAsync_EndpointBusy_IsNotRepeated()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = CreateClient(http, new VirtualTimeProvider());

        // Act
        var act = () => client.ExchangeCodeAsync(OAuthProvider.Google, "code", "verifier", "http://127.0.0.1/", ct);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Calls.Should().Be(1);
    }

    /// <summary>
    /// Creates a token client with a configured Google client id.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="clock">The clock.</param>
    /// <returns>The client.</returns>
    private static OAuthTokenClient CreateClient(HttpClient http, TimeProvider clock)
    {
        var configuration = OAuthClientConfiguration.Load(
            Path.Combine(Path.GetTempPath(), $"swiftlet-missing-{Guid.NewGuid():N}.json"),
            name => name == OAuthClientConfiguration.GoogleClientIdVariable ? "client" : null);
        return new OAuthTokenClient(http, configuration, clock);
    }

    /// <summary>
    /// Answers token requests with the scripted status codes.
    /// </summary>
    /// <param name="statuses">The status of each answer, in order.</param>
    private sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        /// <summary>
        /// Gets the number of requests received.
        /// </summary>
        public int Calls { get; private set; }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = statuses[Calls++];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? """{"access_token":"access","expires_in":3600}""" : "<html>busy</html>"),
            });
        }
    }
}
