using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Swiftlet.Application.Ai;
using Swiftlet.Infrastructure.Ai;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests the clients of <see cref="AiClientFactory"/> against a server on the loopback address.
/// </summary>
public sealed class AiClientFactoryTests : IDisposable
{
    /// <summary>
    /// A successful answer of the Claude API.
    /// </summary>
    private const string ClaudeAnswer =
        """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":1,"output_tokens":1}}
        """;

    /// <summary>
    /// A successful answer of an OpenAI compatible service.
    /// </summary>
    private const string OpenAiAnswer =
        """
        {"id":"c1","object":"chat.completion","created":1,"model":"m",
         "choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    /// <summary>
    /// The factory under test.
    /// </summary>
    private readonly AiClientFactory _factory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        _factory.Dispose();
    }

    /// <summary>
    /// The Claude client posts to the messages endpoint with the configured key only.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Create_Claude_SendsTheKeyToTheMessagesEndpoint()
    {
        using var server = new StubServer(200, "application/json", ClaudeAnswer);
        using var client = _factory.Create(Connection(AiProvider.Claude, server.Address, "key-1"));

        var response = await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        response.Text.Should().Be("OK");
        var request = server.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/v1/messages");
        request.Headers["x-api-key"].Should().Be("key-1");
        request.Headers.Should().NotContainKey("Authorization");
        request.Body.Should().Contain("claude-opus-5-5");
    }

    /// <summary>
    /// Variables read by the SDK neither redirect the request nor add credentials. The custom headers variable has to be
    /// removed, because the SDK caches it for the whole process: whether the headers reach this request would otherwise
    /// depend on the order of the tests.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Create_ClaudeWithSdkEnvironmentVariables_IgnoresThem()
    {
        var variables = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:1",
            ["ANTHROPIC_API_KEY"] = "environment-key",
            ["ANTHROPIC_AUTH_TOKEN"] = "environment-token",
            ["ANTHROPIC_CUSTOM_HEADERS"] = "x-gateway-token: environment-secret\nAuthorization: Bearer environment-header",
        };
        var previous = variables.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            using var server = new StubServer(200, "application/json", ClaudeAnswer);
            using var client = _factory.Create(Connection(AiProvider.Claude, server.Address, "key-1"));

            await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

            Environment.GetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS").Should().BeNull();
            var request = server.Requests.Should().ContainSingle().Subject;
            request.Headers["x-api-key"].Should().Be("key-1");
            string.Join('\n', request.Headers.Values).Should().NotContain("environment");
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>
    /// The Claude client streams the answer.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Create_ClaudeStreaming_YieldsTheText()
    {
        var events = string.Concat(
            Event("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));
        using var server = new StubServer(200, "text/event-stream", events);
        using var client = _factory.Create(Connection(AiProvider.Claude, server.Address, "key-1"));

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken))
        {
            text.Append(update.Text);
        }

        text.ToString().Should().Be("Hello");
    }

    /// <summary>
    /// The compatible client posts to the chat completions endpoint with the key as a bearer token.
    /// </summary>
    /// <param name="key">The configured key.</param>
    /// <param name="expected">The expected header.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData("key-2", "Bearer key-2")]
    [InlineData(null, "Bearer none")]
    public async Task Create_OpenAiCompatible_SendsTheKeyToTheCompletionsEndpoint(string? key, string expected)
    {
        using var server = new StubServer(200, "application/json", OpenAiAnswer);
        using var client = _factory.Create(Connection(AiProvider.OpenAiCompatible, new Uri(server.Address, "v1"), key));

        var response = await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        response.Text.Should().Be("OK");
        var request = server.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/v1/chat/completions");
        request.Headers["Authorization"].Should().Be(expected);
    }

    /// <summary>
    /// The compatible client streams the answer.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Create_OpenAiCompatibleStreaming_YieldsTheText()
    {
        var events =
            """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"Hel"}}]}""" + "\n\n"
            + """data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":"stop"}]}""" + "\n\n"
            + "data: [DONE]\n\n";
        using var server = new StubServer(200, "text/event-stream", events);
        using var client = _factory.Create(Connection(AiProvider.OpenAiCompatible, new Uri(server.Address, "v1"), null));

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken))
        {
            text.Append(update.Text);
        }

        text.ToString().Should().Be("Hello");
    }

    /// <summary>
    /// A refused request becomes an <see cref="AiException"/> that does not quote the answer, and is sent once.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="status">The status code.</param>
    /// <param name="failure">The expected failure.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData(AiProvider.Claude, 401, AiFailure.Unauthorized)]
    [InlineData(AiProvider.Claude, 429, AiFailure.RateLimited)]
    [InlineData(AiProvider.Claude, 500, AiFailure.Unavailable)]
    [InlineData(AiProvider.OpenAiCompatible, 401, AiFailure.Unauthorized)]
    [InlineData(AiProvider.OpenAiCompatible, 404, AiFailure.NotFound)]
    [InlineData(AiProvider.OpenAiCompatible, 429, AiFailure.RateLimited)]
    [InlineData(AiProvider.OpenAiCompatible, 500, AiFailure.Unavailable)]
    public async Task GetResponseAsync_ErrorStatus_ThrowsWithoutTheAnswerAndWithoutRetry(
        AiProvider provider,
        int status,
        AiFailure failure)
    {
        using var server = new StubServer(
            status, "application/json", """{"type":"error","error":{"type":"x","message":"echoed-mail-text"}}""");
        using var client = _factory.Create(Connection(provider, server.Address, "key"));

        var act = () => client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<AiException>()).Which;
        exception.Failure.Should().Be(failure);
        exception.StatusCode.Should().Be(status);
        exception.ToString().Should().NotContain("echoed-mail-text");
        server.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// A failing stream is translated too.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData(AiProvider.Claude)]
    [InlineData(AiProvider.OpenAiCompatible)]
    public async Task GetStreamingResponseAsync_ErrorStatus_ThrowsAiException(AiProvider provider)
    {
        using var server = new StubServer(401, "application/json", "{}");
        using var client = _factory.Create(Connection(provider, server.Address, "key"));

        var act = async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken))
            {
                // Only the failure matters.
            }
        };

        (await act.Should().ThrowAsync<AiException>()).Which.Failure.Should().Be(AiFailure.Unauthorized);
    }

    /// <summary>
    /// A redirect is not followed, so the content never reaches the other host.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData(AiProvider.Claude)]
    [InlineData(AiProvider.OpenAiCompatible)]
    public async Task GetResponseAsync_Redirect_IsNotFollowed(AiProvider provider)
    {
        using var target = new StubServer(200, "application/json", "{}");
        using var server = new StubServer(307, "text/plain", string.Empty, new Uri(target.Address, "elsewhere"));
        using var client = _factory.Create(Connection(provider, server.Address, "key"));

        var act = () => client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AiException>();
        target.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// A service that is not running is reported as unreachable.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData(AiProvider.Claude)]
    [InlineData(AiProvider.OpenAiCompatible)]
    public async Task GetResponseAsync_NothingListening_ReportsNetworkFailure(AiProvider provider)
    {
        var address = new Uri($"http://127.0.0.1:{StubServer.FreePort()}/");
        using var client = _factory.Create(Connection(provider, address, "key"));

        var act = () => client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<AiException>()).Which.Failure.Should().Be(AiFailure.Network);
    }

    /// <summary>
    /// Cancelling is not turned into a failure.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData(AiProvider.Claude)]
    [InlineData(AiProvider.OpenAiCompatible)]
    public async Task GetResponseAsync_Cancelled_ThrowsOperationCanceled(AiProvider provider)
    {
        using var server = new StubServer(200, "application/json", "{}");
        using var client = _factory.Create(Connection(provider, server.Address, "key"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => client.GetResponseAsync("hi", cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Builds a connection to a test server.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="address">The server address.</param>
    /// <param name="key">The key.</param>
    /// <returns>The connection.</returns>
    private static AiConnection Connection(AiProvider provider, Uri address, string? key)
    {
        return new AiConnection(provider, address, AiModels.DefaultClaudeModel, key);
    }

    /// <summary>
    /// Formats one server-sent event.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <param name="data">The event data.</param>
    /// <returns>The event.</returns>
    private static string Event(string name, string data)
    {
        return $"event: {name}\ndata: {data}\n\n";
    }

    /// <summary>
    /// A request the server received.
    /// </summary>
    /// <param name="Path">The path.</param>
    /// <param name="Headers">The headers, by case-insensitive name.</param>
    /// <param name="Body">The body.</param>
    private sealed record ReceivedRequest(string Path, IReadOnlyDictionary<string, string> Headers, string Body);

    /// <summary>
    /// An HTTP server answering every request the same way.
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        /// <summary>
        /// The listener.
        /// </summary>
        private readonly HttpListener _listener = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="StubServer"/> class and starts answering.
        /// </summary>
        /// <param name="status">The status code of the answer.</param>
        /// <param name="contentType">The content type of the answer.</param>
        /// <param name="body">The body of the answer.</param>
        /// <param name="location">The redirect target, if any.</param>
        public StubServer(int status, string contentType, string body, Uri? location = null)
        {
            Address = new Uri($"http://127.0.0.1:{FreePort()}/");
            _listener.Prefixes.Add(Address.AbsoluteUri);
            _listener.Start();
            _ = ServeAsync(status, contentType, Encoding.UTF8.GetBytes(body), location);
        }

        /// <summary>
        /// Gets the address of the server.
        /// </summary>
        public Uri Address { get; }

        /// <summary>
        /// Gets the requests received so far.
        /// </summary>
        public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

        /// <summary>
        /// Finds a port nothing listens on.
        /// </summary>
        /// <returns>The port.</returns>
        public static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _listener.Close();
        }

        /// <summary>
        /// Answers requests until the listener is closed.
        /// </summary>
        /// <param name="status">The status code.</param>
        /// <param name="contentType">The content type.</param>
        /// <param name="body">The body.</param>
        /// <param name="location">The redirect target, if any.</param>
        /// <returns>A task that completes when the server stopped.</returns>
        private async Task ServeAsync(int status, string contentType, byte[] body, Uri? location)
        {
            try
            {
                while (true)
                {
                    var context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var headers = context.Request.Headers.AllKeys.ToDictionary(
                        name => name!, name => context.Request.Headers[name]!, StringComparer.OrdinalIgnoreCase);
                    Requests.Enqueue(new ReceivedRequest(context.Request.Url!.AbsolutePath, headers, await reader.ReadToEndAsync()));

                    context.Response.StatusCode = status;
                    context.Response.ContentType = contentType;
                    if (location is not null)
                    {
                        context.Response.RedirectLocation = location.AbsoluteUri;
                    }

                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // The test closed the server.
            }
        }
    }
}
