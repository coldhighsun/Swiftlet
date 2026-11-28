using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using Swiftlet.Application.Ai;

namespace Swiftlet.Infrastructure.Ai;

/// <summary>
/// Creates clients for the Claude API and for OpenAI compatible services. Both are configured explicitly, so
/// environment variables of the SDKs cannot redirect mail content or add credentials; neither retries (a repeated
/// request is billed again) nor follows redirects (they would carry the content to another host).
/// </summary>
/// <param name="logger">The logger; optional.</param>
public sealed class AiClientFactory(ILogger<AiClientFactory>? logger = null) : IAiClientFactory, IDisposable
{
    /// <summary>
    /// How long one request may take; models running on this computer can be slow.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The connection pool shared by all clients.
    /// </summary>
    private readonly SocketsHttpHandler _handler = new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <inheritdoc />
    public IChatClient Create(AiConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Provider switch
        {
            AiProvider.Claude => CreateClaude(connection),
            AiProvider.OpenAiCompatible => CreateOpenAiCompatible(connection),
            _ => throw new ArgumentOutOfRangeException(nameof(connection), connection.Provider, "Unknown provider."),
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _handler.Dispose();
    }

    /// <summary>
    /// Creates a client for the Claude API.
    /// </summary>
    /// <param name="connection">The service to talk to.</param>
    /// <returns>The client.</returns>
    private TranslatingChatClient CreateClaude(AiConnection connection)
    {
        var apiKey = connection.ApiKey ?? string.Empty;
        var http = new HttpClient(new ClaudeHeaderHandler(apiKey, _handler), disposeHandler: false)
        {
            // The SDK enforces its own timeout.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // The SDK reads this variable once per process, at its first request, and adds the headers listed in it to every
        // request; on a developer machine they can hold the token of a gateway. An Authorization entry even makes it
        // fail before anything is sent, so the variable cannot be handled afterwards and is removed from this process.
        Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", null);

        // Every other setting the SDK would read from the environment is given here: the address, the credential
        // (setting the key stops it from looking for other ones) and the telemetry, which could export message content.
        var client = new AnthropicClient(new ClientOptions
        {
            BaseUrl = connection.Endpoint.AbsoluteUri.TrimEnd('/'),
            ApiKey = apiKey,
            MaxRetries = 0,
            Timeout = RequestTimeout,
            OpenTelemetry = false,
            HttpClient = http,
        });
        return new TranslatingChatClient(client.AsIChatClient(connection.Model), client, logger);
    }

    /// <summary>
    /// Creates a client for an OpenAI compatible service.
    /// </summary>
    /// <param name="connection">The service to talk to.</param>
    /// <returns>The client.</returns>
    private TranslatingChatClient CreateOpenAiCompatible(AiConnection connection)
    {
        var http = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        // Local services need no key, but the SDK insists on one.
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(connection.ApiKey) ? "none" : connection.ApiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = connection.Endpoint,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = RequestTimeout,
            Transport = new HttpClientPipelineTransport(http),
        };
        var client = new OpenAIClient(credential, options).GetChatClient(connection.Model);
        return new TranslatingChatClient(client.AsIChatClient(), http, logger);
    }

    /// <summary>
    /// Sends the configured key of a Claude request in <c>x-api-key</c> only: the SDK also puts it into
    /// <c>Authorization</c>.
    /// </summary>
    private sealed class ClaudeHeaderHandler : DelegatingHandler
    {
        /// <summary>
        /// The configured API key.
        /// </summary>
        private readonly string _apiKey;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClaudeHeaderHandler"/> class.
        /// </summary>
        /// <param name="apiKey">The configured API key.</param>
        /// <param name="inner">The shared connection pool.</param>
        public ClaudeHeaderHandler(string apiKey, HttpMessageHandler inner)
            : base(inner)
        {
            _apiKey = apiKey;
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = null;
            request.Headers.Remove("x-api-key");
            if (_apiKey.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
