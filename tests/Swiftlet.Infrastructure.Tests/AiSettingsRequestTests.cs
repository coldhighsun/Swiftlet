using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Ai;
using Swiftlet.Application.Logging;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests the AI settings commands and the API key store.
/// </summary>
public sealed class AiSettingsRequestTests
{
    /// <summary>
    /// The settings the commands change.
    /// </summary>
    private readonly DisabledAiSettings _settings = new();

    /// <summary>
    /// The secret store behind the key store.
    /// </summary>
    private readonly DictionarySecrets _secrets = new();

    /// <summary>
    /// Switching on saves the settings, confirms the destination and keeps the key out of the settings.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SaveAiSettings_Enabled_ConfirmsTheDestinationAndStoresTheKey()
    {
        await SaveAsync(true, "https://api.anthropic.com", " key-1 ");

        _settings.Ai.IsReady.Should().BeTrue();
        _settings.Ai.ConfirmedDestination.Should().Be("Claude|https://api.anthropic.com");
        _settings.Ai.ToString().Should().NotContain("key-1");
        var stored = await Keys().GetAsync(
            AiProvider.Claude, new Uri("https://api.anthropic.com/v1"), TestContext.Current.CancellationToken);
        stored.Should().Be("key-1");
    }

    /// <summary>
    /// A key entered for one host is not handed out for another.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task GetAsync_OtherHost_ReturnsNoKey()
    {
        await SaveAsync(true, "https://api.anthropic.com", "key-1");

        var key = await Keys().GetAsync(
            AiProvider.Claude, new Uri("https://gateway.example.com"), TestContext.Current.CancellationToken);

        key.Should().BeNull();
    }

    /// <summary>
    /// Saving without a typed key keeps the stored one.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SaveAiSettings_NoKeyTyped_KeepsTheStoredKey()
    {
        await SaveAsync(true, "https://api.anthropic.com", "key-1");

        await SaveAsync(true, "https://api.anthropic.com", null);

        _secrets.Values.Values.Should().ContainSingle().Which.Should().Contain("key-1");
    }

    /// <summary>
    /// Switching off keeps the earlier consent and makes the settings not ready.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SaveAiSettings_Disabled_KeepsTheConsentAndIsNotReady()
    {
        await SaveAsync(true, "https://api.anthropic.com", null);

        await SaveAsync(false, "https://other.example.com", null);

        _settings.Ai.IsReady.Should().BeFalse();
        _settings.Ai.ConfirmedDestination.Should().Be("Claude|https://api.anthropic.com");
    }

    /// <summary>
    /// An address that would send content unprotected cannot be switched on.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SaveAiSettings_PlainHttpToAnotherHost_Throws()
    {
        var act = () => SaveAsync(true, "http://example.com/v1", "key");

        await act.Should().ThrowAsync<ArgumentException>();
        _settings.Ai.IsEnabled.Should().BeFalse();
        _secrets.Values.Should().BeEmpty();
    }

    /// <summary>
    /// The log description and the text form of the commands never hold the key.
    /// </summary>
    [Fact]
    public void Commands_WithKey_DescribeWithoutIt()
    {
        var save = new SaveAiSettingsCommand(true, AiProvider.Claude, "https://api.anthropic.com", "m", 1000, "key-1");
        var test = new TestAiConnectionCommand(AiProvider.Claude, "https://api.anthropic.com", "m", "key-1");
        var connection = new AiConnection(AiProvider.Claude, new Uri("https://api.anthropic.com"), "m", "key-1");

        new[] { save.DescribeForLog(new LogNames()), save.ToString(), test.ToString(), connection.ToString() }
            .Should().AllSatisfy(text => text.Should().NotContain("key-1"));
    }

    /// <summary>
    /// The connection test uses the stored key when none was typed, and sends no mail content.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task TestAiConnection_NoKeyTyped_UsesTheStoredKey()
    {
        await SaveAsync(true, "https://api.anthropic.com", "key-1");
        var clients = new RecordingClientFactory();
        var handler = new TestAiConnectionCommandHandler(clients, Keys());

        await handler.Handle(
            new TestAiConnectionCommand(AiProvider.Claude, "https://api.anthropic.com", "m", " "),
            TestContext.Current.CancellationToken);

        clients.Connections.Should().ContainSingle().Which.ApiKey.Should().Be("key-1");
        clients.Prompts.Should().ContainSingle().Which.Should().Be("Reply with the single word OK.");
    }

    /// <summary>
    /// Removing a key empties the secret store.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task RemoveAiKey_StoredKey_IsRemoved()
    {
        await SaveAsync(true, "https://api.anthropic.com", "key-1");

        await new RemoveAiKeyCommandHandler(Keys())
            .Handle(new RemoveAiKeyCommand(AiProvider.Claude), TestContext.Current.CancellationToken);

        _secrets.Values.Should().BeEmpty();
    }

    /// <summary>
    /// Only https and loopback http addresses without credentials or query are accepted.
    /// </summary>
    /// <param name="text">The address.</param>
    /// <param name="expected">Whether it is acceptable.</param>
    [Theory]
    [InlineData("https://api.anthropic.com", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://[::1]:1234/v1", true)]
    [InlineData("http://192.168.1.5:11434/v1", false)]
    [InlineData("http://localhost.example.com/v1", false)]
    [InlineData("https://user:secret@example.com", false)]
    [InlineData("https://example.com/v1?key=1", false)]
    [InlineData("file:///c:/temp", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void TryParse_Address_AcceptsOnlySafeOnes(string text, bool expected)
    {
        AiEndpoint.TryParse(text, out _).Should().Be(expected);
    }

    /// <summary>
    /// Settings pointing somewhere else than the confirmed destination are not ready.
    /// </summary>
    [Fact]
    public void IsReady_EndpointChangedOutsideTheApp_IsFalse()
    {
        var settings = AiConfiguration.Default with
        {
            IsEnabled = true,
            Endpoint = "https://other.example.com",
            ConfirmedDestination = "Claude|https://api.anthropic.com",
        };

        settings.IsReady.Should().BeFalse();
    }

    /// <summary>
    /// Creates a key store over the test secrets.
    /// </summary>
    /// <returns>The store.</returns>
    private AiKeyStore Keys()
    {
        return new AiKeyStore(_secrets);
    }

    /// <summary>
    /// Runs the save command for Claude.
    /// </summary>
    /// <param name="enabled">Whether AI is switched on.</param>
    /// <param name="endpoint">The address.</param>
    /// <param name="key">The typed key.</param>
    /// <returns>A task that completes when the command ran.</returns>
    private async Task SaveAsync(bool enabled, string endpoint, string? key)
    {
        var command = new SaveAiSettingsCommand(enabled, AiProvider.Claude, endpoint, "m", 12_000, key);
        await new SaveAiSettingsCommandHandler(_settings, Keys()).Handle(command, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A secret store held in memory.
    /// </summary>
    private sealed class DictionarySecrets : ISecretStore
    {
        /// <summary>
        /// Gets the stored secrets.
        /// </summary>
        public Dictionary<string, string> Values { get; } = [];

        /// <inheritdoc />
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Values.GetValueOrDefault(key));
        }

        /// <inheritdoc />
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A client factory that records what it was asked to do.
    /// </summary>
    private sealed class RecordingClientFactory : IAiClientFactory, IChatClient
    {
        /// <summary>
        /// Gets the connections clients were created for.
        /// </summary>
        public List<AiConnection> Connections { get; } = [];

        /// <summary>
        /// Gets the texts sent.
        /// </summary>
        public List<string> Prompts { get; } = [];

        /// <inheritdoc />
        public IChatClient Create(AiConnection connection)
        {
            Connections.Add(connection);
            return this;
        }

        /// <inheritdoc />
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Prompts.AddRange(messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
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
}
