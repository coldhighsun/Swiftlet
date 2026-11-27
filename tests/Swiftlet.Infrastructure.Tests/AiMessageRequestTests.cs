using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Mediator;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Application;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Ai;
using Swiftlet.Application.Messages;
using Swiftlet.Domain.Labels;
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
        services.AddScoped<ILabelRepository>(_ => _database.CreateLabelRepository());
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
    /// A rewrite sends only the given text between delimiters, and nothing of it is logged.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Rewrite_Text_SendsItDelimitedWithoutLoggingIt()
    {
        _model.Pieces = ["private-answer"];

        var parts = await StreamAsync(new RewriteTextQuery("private-draft", AiWritingAction.Formal));

        parts.Should().Equal(new AiOutput(AiOutputKind.Text, "private-answer"));
        var prompt = _model.Prompts.Should().ContainSingle().Subject;
        prompt[0].Text.Should().Contain("formal");
        prompt[1].Text.Should().StartWith("<draft-").And.Contain("private-draft").And.NotContain("private-body");
        _log.Messages.Should().Contain(m => m.Contains("RewriteTextQuery", StringComparison.Ordinal))
            .And.NotContain(m => m.Contains("private-", StringComparison.Ordinal));
    }

    /// <summary>
    /// A draft sends the replied message and the notes, each in its own delimiters.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task DraftReply_Notes_SendsMailAndNotesWithoutLoggingThem()
    {
        _model.Pieces = ["private-answer"];

        await StreamAsync(new DraftReplyQuery(_message.AccountId, _message.Id, "private-notes"));

        var content = _model.Prompts.Should().ContainSingle().Subject[1].Text;
        content.Should().StartWith("<email-").And.Contain("private-body").And.Contain("<notes-").And.Contain("private-notes");
        _log.Messages.Should().NotContain(m => m.Contains("private-", StringComparison.Ordinal));
    }

    /// <summary>
    /// A text of the user above the limit is refused instead of being cut, because the answer would replace it.
    /// </summary>
    /// <param name="draft">Whether a reply is drafted rather than a text rewritten.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writing_TextAboveTheLimit_SendsNothing(bool draft)
    {
        _settings.SetAi(_settings.Ai with { MaxInputCharacters = 3 });
        IStreamQuery<AiOutput> query = draft
            ? new DraftReplyQuery(_message.AccountId, _message.Id, "long notes")
            : new RewriteTextQuery("long text", AiWritingAction.Polish);

        var act = () => StreamAsync(query);

        (await act.Should().ThrowAsync<AiException>()).Which.Failure.Should().Be(AiFailure.TooLong);
        _model.Prompts.Should().BeEmpty();
    }

    /// <summary>
    /// Writing help sends nothing while AI is off.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Rewrite_NotReady_SendsNothing()
    {
        _settings.SetAi(_settings.Ai with { IsEnabled = false });

        var act = () => StreamAsync(new RewriteTextQuery("text", AiWritingAction.Polish));

        (await act.Should().ThrowAsync<AiException>()).Which.Failure.Should().Be(AiFailure.NotConfigured);
        _model.Prompts.Should().BeEmpty();
    }

    /// <summary>
    /// Each message is asked about on its own with the label names, and only changes are suggested.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Classify_Messages_SuggestsOnlyChangesOnePerRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Label.Create("private-work", "#E53935");
        var home = Label.Create("private-home", "#1E88E5");
        await _database.CreateLabelRepository().AddAsync(work, ct);
        await _database.CreateLabelRepository().AddAsync(home, ct);
        var second = await AddMessageAsync(2, "second-body");
        second.AddLabel(work);
        await _database.CreateMessageRepository().SaveLabelsAsync(second, ct);
        _model.Reply = _ => """{"labels":["private-work"],"priority":"normal"}""";

        var results = await ClassifyAsync(Reference(_message), Reference(second), new(_message.AccountId, Guid.NewGuid()));

        results.Should().HaveCount(3);
        results.Single(r => r.Message == Reference(_message)).Should().Match<MessageClassification>(r =>
            r.Status == ClassificationStatus.Suggested && r.LabelIds.Single() == work.Id && r.Priority == null);
        results.Where(r => r.Message != Reference(_message))
            .Should().OnlyContain(r => r.Status == ClassificationStatus.NoSuggestion);
        _model.Prompts.Should().HaveCount(2).And.OnlyContain(prompt =>
            prompt[0].Text.Contains("private-home")
            && prompt[1].Text.Contains("private-body") != prompt[1].Text.Contains("second-body"));
        _log.Messages.Should().NotContain(m => m.Contains("private-", StringComparison.Ordinal));
        (await _database.CreateMessageRepository().GetAsync(_message.AccountId, _message.Id, ct))!
            .Labels.Should().BeEmpty("nothing is applied by classifying");
    }

    /// <summary>
    /// At most two requests run at once and at most twenty messages are classified.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Classify_ManyMessages_LimitsCountAndConcurrency()
    {
        _model.Reply = _ => """{"priority":"low"}""";

        var results = await ClassifyAsync([.. Enumerable.Repeat(Reference(_message), 25)]);

        results.Should().HaveCount(ClassifyMessagesQuery.MaxMessages)
            .And.OnlyContain(r => r.Priority == MessagePriority.Low);
        _model.MostAtOnce.Should().Be(2);
    }

    /// <summary>
    /// A failure that may concern one message is reported for it and the others go on.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Classify_OneRejected_ContinuesWithTheOthers()
    {
        var second = await AddMessageAsync(2, "second-body");
        _model.Reply = content => content.Contains("second-body", StringComparison.Ordinal)
            ? throw new AiException(AiFailure.Rejected)
            : """{"priority":"high"}""";

        var results = await ClassifyAsync(Reference(_message), Reference(second));

        results.Single(r => r.Message == Reference(second)).Should().Match<MessageClassification>(r =>
            r.Status == ClassificationStatus.Failed && r.Failure == AiFailure.Rejected);
        results.Single(r => r.Message == Reference(_message)).Priority.Should().Be(MessagePriority.High);
    }

    /// <summary>
    /// A failure that concerns every message ends the request.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Classify_Unauthorized_Stops()
    {
        _model.Reply = _ => throw new AiException(AiFailure.Unauthorized);

        var act = () => ClassifyAsync([.. Enumerable.Repeat(Reference(_message), 10)]);

        (await act.Should().ThrowAsync<AiException>()).Which.Failure.Should().Be(AiFailure.Unauthorized);
        _model.Prompts.Count.Should().BeLessThan(10);
    }

    /// <summary>
    /// The priority is stored, kept by a repeated read and cleared again.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SetMessagePriority_Value_IsStoredAndCleared()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = _database.CreateMessageRepository();
        var handler = new SetMessagePriorityCommandHandler(repository);

        await handler.Handle(new SetMessagePriorityCommand(_message.AccountId, _message.Id, MessagePriority.High), ct);

        (await repository.GetAsync(_message.AccountId, _message.Id, ct))!.AiPriority.Should().Be(MessagePriority.High);
        await using var verifier = _database.CreateVerifier();
        (await verifier.Messages().GetAsync(_message.AccountId, _message.Id, ct))!.AiPriority.Should().Be(MessagePriority.High);

        await handler.Handle(new SetMessagePriorityCommand(_message.AccountId, _message.Id, null), ct);
        await using var later = _database.CreateVerifier();
        (await later.Messages().GetAsync(_message.AccountId, _message.Id, ct))!.AiPriority.Should().BeNull();
    }

    /// <summary>
    /// Gets the reference of a message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The reference.</returns>
    private static MessageReference Reference(MailMessage message)
    {
        return new MessageReference(message.AccountId, message.Id);
    }

    /// <summary>
    /// Adds another message to the seeded folder.
    /// </summary>
    /// <param name="uid">The UID.</param>
    /// <param name="body">The body.</param>
    /// <returns>The message.</returns>
    private async Task<MailMessage> AddMessageAsync(uint uid, string body)
    {
        var message = MailMessage.Create(
            _message.AccountId, _message.FolderId, uid, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, body);
        await _database.CreateMessageRepository().AddAsync(message, TestContext.Current.CancellationToken);
        return message;
    }

    /// <summary>
    /// Runs a classification.
    /// </summary>
    /// <param name="references">The messages.</param>
    /// <returns>The results.</returns>
    private async Task<List<MessageClassification>> ClassifyAsync(params MessageReference[] references)
    {
        await using var scope = _services.CreateAsyncScope();
        var results = new List<MessageClassification>();
        await foreach (var result in scope.ServiceProvider.GetRequiredService<ISender>()
            .CreateStream(new ClassifyMessagesQuery(references), TestContext.Current.CancellationToken))
        {
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Runs a streamed request through Mediator.
    /// </summary>
    /// <param name="query">The request.</param>
    /// <returns>The parts of the answer.</returns>
    private async Task<List<AiOutput>> StreamAsync(IStreamQuery<AiOutput> query)
    {
        await using var scope = _services.CreateAsyncScope();
        var parts = new List<AiOutput>();
        await foreach (var part in scope.ServiceProvider.GetRequiredService<ISender>()
            .CreateStream(query, TestContext.Current.CancellationToken))
        {
            parts.Add(part);
        }

        return parts;
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
        /// Gets or sets a function answering by the sent content, used instead of <see cref="Pieces"/>.
        /// </summary>
        public Func<string, string>? Reply { get; set; }

        /// <summary>
        /// Gets the largest number of requests that ran at the same time.
        /// </summary>
        public int MostAtOnce { get; private set; }

        /// <summary>
        /// How many requests run now.
        /// </summary>
        private int _active;

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
            List<ChatMessage> prompt = [.. messages];
            lock (Prompts)
            {
                Prompts.Add(prompt);
                Options.Add(options);
                MostAtOnce = Math.Max(MostAtOnce, ++_active);
            }

            try
            {
                if (Reply is not null)
                {
                    // Long enough for a second request to start while this one is still running.
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                }

                foreach (var piece in Reply is null ? Pieces : [Reply(prompt[1].Text)])
                {
                    await Task.Yield();
                    yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
                }
            }
            finally
            {
                lock (Prompts)
                {
                    _active--;
                }
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
