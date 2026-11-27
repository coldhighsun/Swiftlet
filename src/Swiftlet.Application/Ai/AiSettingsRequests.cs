using Mediator;
using Microsoft.Extensions.AI;
using Swiftlet.Application.Logging;

namespace Swiftlet.Application.Ai;

/// <summary>
/// Saves the AI settings. Switching AI on records the destination as confirmed, so the caller must have asked the user
/// first when that destination is new.
/// </summary>
/// <param name="IsEnabled">Whether the AI features are switched on.</param>
/// <param name="Provider">The kind of service.</param>
/// <param name="Endpoint">The base address.</param>
/// <param name="Model">The model identifier.</param>
/// <param name="MaxInputCharacters">The largest amount of message text sent with one request.</param>
/// <param name="ApiKey">A newly typed API key, or <see langword="null"/> to keep the stored one.</param>
public sealed record SaveAiSettingsCommand(
    bool IsEnabled,
    AiProvider Provider,
    string Endpoint,
    string Model,
    int MaxInputCharacters,
    string? ApiKey) : ICommand<Unit>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        var host = AiEndpoint.TryParse(Endpoint, out var endpoint) ? endpoint.Host : "invalid";
        return $"enabled {IsEnabled}, provider {Provider}, host {host}, model {Model}, new key {ApiKey is not null}";
    }

    /// <summary>
    /// Describes the request without the API key.
    /// </summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        return DescribeForLog(new LogNames());
    }
}

/// <summary>
/// Handles <see cref="SaveAiSettingsCommand"/>.
/// </summary>
/// <param name="settings">The settings store.</param>
/// <param name="keys">The API key store.</param>
public sealed class SaveAiSettingsCommandHandler(IAiSettings settings, AiKeyStore keys)
    : ICommandHandler<SaveAiSettingsCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(SaveAiSettingsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var valid = AiEndpoint.TryParse(command.Endpoint, out var endpoint);
        if (command.IsEnabled)
        {
            if (!valid)
            {
                throw new ArgumentException("The address is not acceptable.", nameof(command));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(command.Model);
        }

        var apiKey = command.ApiKey?.Trim();
        if (valid && !string.IsNullOrEmpty(apiKey))
        {
            await keys.SetAsync(command.Provider, endpoint!, apiKey, cancellationToken);
        }

        // Switching off keeps the earlier consent, so switching on again for the same destination does not ask again.
        var confirmed = command.IsEnabled
            ? AiEndpoint.Destination(command.Provider, endpoint!)
            : settings.Ai.ConfirmedDestination;
        settings.SetAi(new AiConfiguration(
            command.IsEnabled,
            command.Provider,
            command.Endpoint.Trim(),
            command.Model.Trim(),
            Math.Clamp(
                command.MaxInputCharacters, AiConfiguration.MinInputCharacters, AiConfiguration.MaxAllowedInputCharacters),
            confirmed));
        return Unit.Value;
    }
}

/// <summary>
/// Removes the stored API key of a provider.
/// </summary>
/// <param name="Provider">The provider.</param>
public sealed record RemoveAiKeyCommand(AiProvider Provider) : ICommand<Unit>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"provider {Provider}";
    }
}

/// <summary>
/// Handles <see cref="RemoveAiKeyCommand"/>.
/// </summary>
/// <param name="keys">The API key store.</param>
public sealed class RemoveAiKeyCommandHandler(AiKeyStore keys) : ICommandHandler<RemoveAiKeyCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(RemoveAiKeyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await keys.RemoveAsync(command.Provider, cancellationToken);
        return Unit.Value;
    }
}

/// <summary>
/// Sends a tiny request holding no mail content to check that a service answers.
/// </summary>
/// <param name="Provider">The kind of service.</param>
/// <param name="Endpoint">The base address.</param>
/// <param name="Model">The model identifier.</param>
/// <param name="ApiKey">A newly typed API key, or <see langword="null"/> to use the stored one.</param>
public sealed record TestAiConnectionCommand(AiProvider Provider, string Endpoint, string Model, string? ApiKey)
    : ICommand<Unit>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        var host = AiEndpoint.TryParse(Endpoint, out var endpoint) ? endpoint.Host : "invalid";
        return $"provider {Provider}, host {host}, model {Model}";
    }

    /// <summary>
    /// Describes the request without the API key.
    /// </summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        return DescribeForLog(new LogNames());
    }
}

/// <summary>
/// Handles <see cref="TestAiConnectionCommand"/>; it fails with <see cref="AiException"/> when the service does not
/// answer.
/// </summary>
/// <param name="clients">The client factory.</param>
/// <param name="keys">The API key store.</param>
public sealed class TestAiConnectionCommandHandler(IAiClientFactory clients, AiKeyStore keys)
    : ICommandHandler<TestAiConnectionCommand, Unit>
{
    /// <summary>
    /// The output limit of the test request; models that think before answering count that too, so it is not tiny.
    /// </summary>
    private const int TestOutputTokens = 1_024;

    /// <inheritdoc />
    public async ValueTask<Unit> Handle(TestAiConnectionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!AiEndpoint.TryParse(command.Endpoint, out var endpoint))
        {
            throw new ArgumentException("The address is not acceptable.", nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.Model);
        var apiKey = command.ApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = await keys.GetAsync(command.Provider, endpoint, cancellationToken);
        }

        using var client = clients.Create(new AiConnection(command.Provider, endpoint, command.Model.Trim(), apiKey));
        await client.GetResponseAsync(
            "Reply with the single word OK.",
            new ChatOptions { MaxOutputTokens = TestOutputTokens },
            cancellationToken);
        return Unit.Value;
    }
}

/// <summary>
/// Asks whether an API key is stored for a destination.
/// </summary>
/// <param name="Provider">The kind of service.</param>
/// <param name="Endpoint">The base address.</param>
public sealed record HasAiKeyQuery(AiProvider Provider, string Endpoint) : IQuery<bool>;

/// <summary>
/// Handles <see cref="HasAiKeyQuery"/>.
/// </summary>
/// <param name="keys">The API key store.</param>
public sealed class HasAiKeyQueryHandler(AiKeyStore keys) : IQueryHandler<HasAiKeyQuery, bool>
{
    /// <inheritdoc />
    public async ValueTask<bool> Handle(HasAiKeyQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return AiEndpoint.TryParse(query.Endpoint, out var endpoint)
            && await keys.GetAsync(query.Provider, endpoint, cancellationToken) is not null;
    }
}
