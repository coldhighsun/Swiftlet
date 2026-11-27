using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Mediator;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Application;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Ai;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests the summary and translation requests through the real Mediator pipeline, a real database and a fake model.
/// </summary>
public sealed class AiMessageRequestTests : IAsyncLifetime
{
    /// <summary>
    /// The database.
    /// </summary>
    private TestDatabase _database = null!;

    /// <summary>
    /// The services.
    /// </summary>
    private ServiceProvider _services = null!;

    /// <summary>
    /// The fake model, which is its own factory.
    /// </summary>
    private readonly FakeModel _model = new();

    /// <summary>
    /// The settings.
    /// </summary>
    private readonly DisabledAiSettings _settings = new();

    /// <summary>
    /// Everything logged.
    /// </summary>
    private readonly CapturingLoggerProvider _log = new();

    /// <summary>
    /// The seeded message.
    /// </summary>
    private MailMessage _message = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await _database.SeedAccountAsync(ct);
        _message = MailMessage.Create(
            account.Id, folder.Id, 1, "private-subject", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "private-body");
        await _database.CreateMessageRepository().AddAsync(_message, ct);

        _settings.SetAi(AiConfiguration.Default with
        {
            IsEnabled = true,
            ConfirmedDestination = "Claude|https://api.anthropic.com",
        });
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(_log));
        services.AddApplication();
        services.AddSingleton<IAiSettings>(_settings);
        services.AddSingleton<IAiClientFactory>(_model);
        services.AddSingleton<ISecretStore>(new EmptySecrets());
        services.AddScoped<IMessageRepository>(_ => _database.CreateMessageRepository());
        _services = services.BuildServiceProvider();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    /// <summary>
    /// The answer streams through, the mail is sent between delimiters and the log holds neither mail nor answer.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Summarize_Ready_StreamsTheAnswerWithoutLoggingContent()
    {
        _model.Pieces = ["<think>hidden</think>", "private-", "answer"];

        var parts = await SummarizeAsync();

        parts.Should().Equal(new AiOutput(AiOutputKind.Text, "private-"), new AiOutput(AiOutputKind.Text, "answer"));
        var prompt = _model.Prompts.Should().ContainSingle().Subject;
        prompt[0].Role.Should().Be(ChatRole.System);
        prompt[1].Text.Should().Contain("private-body").And.Contain("private-subject");
        _model.Options.Should().ContainSingle().Which!.MaxOutputTokens.Should().Be(16_000);
        _model.Options[0]!.Tools.Should().BeNull();
        _log.Messages.Should().Contain(m => m.Contains("SummarizeMessageQuery", StringComparison.Ordinal))
            .And.NotContain(m => m.Contains("private-", StringComparison.Ordinal));
    }

    /// <summary>
    /// A second request is answered from memory; asking for a fresh one calls the model again.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Summarize_Repeated_UsesTheKeptAnswerUnlessFresh()
    {
        _model.Pieces = ["a", "b"];
        await SummarizeAsync();

        var kept = await SummarizeAsync();
        await SummarizeAsync(fresh: true);

        kept.Should().Equal(new AiOutput(AiOutputKind.Text, "ab"));
        _model.Prompts.Should().HaveCount(2);
    }

    /// <summary>
    /// Nothing is sent while AI is off.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Translate_NotReady_ThrowsWithoutSending()
    {
        _settings.SetAi(_settings.Ai with { IsEnabled = false });

        var act = async () =>
        {
            await using var scope = _services.CreateAsyncScope();
            var query = new TranslateMessageQuery(_message.AccountId, _message.Id, AiLanguage.English);
            await foreach (var _ in scope.ServiceProvider.GetRequiredService<ISender>()
                .CreateStream(query, TestContext.Current.CancellationToken))
            {
                // Only the failure matters.
            }
        };

        (await act.Should().ThrowAsync<AiException>()).Which.Failure.Should().Be(AiFailure.NotConfigured);
        _model.Prompts.Should().BeEmpty();
    }

    /// <summary>
    /// A long body is reported as cut, and an answer ending at the length limit too; the cached answer says both again.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Summarize_Truncated_ReportsBothEnds()
    {
        _settings.SetAi(_settings.Ai with { MaxInputCharacters = 3 });
        _model.Pieces = ["a"];
        _model.Finish = ChatFinishReason.Length;
        AiOutputKind[] expected = [AiOutputKind.InputTruncated, AiOutputKind.Text, AiOutputKind.OutputTruncated];

        var first = await SummarizeAsync();
        var second = await SummarizeAsync();

        first.Select(p => p.Kind).Should().Equal(expected);
        second.Select(p => p.Kind).Should().Equal(expected);
    }

    /// <summary>
    /// A refusal is reported and not kept.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Summarize_Refused_ReportsItAndKeepsNothing()
    {
        _model.Pieces = ["partial"];
        _model.Finish = ChatFinishReason.ContentFilter;

        var parts = await SummarizeAsync();
        await SummarizeAsync();

        parts[^1].Kind.Should().Be(AiOutputKind.Refused);
        _model.Prompts.Should().HaveCount(2);
    }

    /// <summary>
    /// Compatible services get no output limit, because many reject a large one.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Summarize_CompatibleService_SetsNoOutputLimit()
    {
        _settings.SetAi(_settings.Ai with
        {
            Provider = AiProvider.OpenAiCompatible,
            Endpoint = "http://localhost:11434/v1",
            ConfirmedDestination = "OpenAiCompatible|http://localhost:11434",
        });
        _model.Pieces = ["a"];

        await SummarizeAsync();

        _model.Options.Should().ContainSingle().Which!.MaxOutputTokens.Should().BeNull();
    }

    /// <summary>
    /// Runs a summary request.
    /// </summary>
    /// <param name="fresh">Whether to ignore the kept answer.</param>
    /// <returns>The parts of the answer.</returns>
    private async Task<List<AiOutput>> SummarizeAsync(bool fresh = false)
    {
        await using var scope = _services.CreateAsyncScope();
        var query = new SummarizeMessageQuery(_message.AccountId, _message.Id, AiLanguage.English, fresh);
        var parts = new List<AiOutput>();
        await foreach (var part in scope.ServiceProvider.GetRequiredService<ISender>()
            .CreateStream(query, TestContext.Current.CancellationToken))
        {
            parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// A model answering with prepared pieces.
    /// </summary>
    private sealed class FakeModel : IAiClientFactory, IChatClient
    {
        /// <summary>
        /// Gets or sets the pieces of the answer.
        /// </summary>
        public string[] Pieces { get; set; } = [];

        /// <summary>
        /// Gets or sets the reason the answer ends with.
        /// </summary>
        public ChatFinishReason Finish { get; set; } = ChatFinishReason.Stop;

        /// <summary>
        /// Gets the conversations received.
        /// </summary>
        public List<List<ChatMessage>> Prompts { get; } = [];

        /// <summary>
        /// Gets the options received.
        /// </summary>
        public List<ChatOptions?> Options { get; } = [];

        /// <inheritdoc />
        public IChatClient Create(AiConnection connection)
        {
            return this;
        }

        /// <inheritdoc />
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Prompts.Add([.. messages]);
            Options.Add(options);
            foreach (var piece in Pieces)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
            }

            yield return new ChatResponseUpdate { FinishReason = Finish };
        }

        /// <inheritdoc />
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing is held.
        }
    }

    /// <summary>
    /// A secret store without secrets.
    /// </summary>
    private sealed class EmptySecrets : ISecretStore
    {
        /// <inheritdoc />
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        /// <inheritdoc />
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Keeps the text of every log entry, exceptions included.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
    {
        /// <summary>
        /// Gets the texts.
        /// </summary>
        public List<string> Messages { get; } = [];

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add($"{formatter(state, exception)} {exception}");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing is held.
        }
    }
}
