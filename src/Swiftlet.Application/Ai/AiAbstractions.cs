using Microsoft.Extensions.AI;

namespace Swiftlet.Application.Ai;

/// <summary>
/// The saved AI settings.
/// </summary>
public interface IAiSettings
{
    /// <summary>
    /// Gets the current settings.
    /// </summary>
    AiConfiguration Ai { get; }

    /// <summary>
    /// Raised after changed settings were saved.
    /// </summary>
    event EventHandler? AiChanged;

    /// <summary>
    /// Replaces the settings and saves them.
    /// </summary>
    /// <param name="value">The new settings.</param>
    void SetAi(AiConfiguration value);
}

/// <summary>
/// What is needed to talk to an AI service.
/// </summary>
/// <param name="Provider">The kind of service.</param>
/// <param name="Endpoint">The base address.</param>
/// <param name="Model">The model identifier.</param>
/// <param name="ApiKey">The API key, or <see langword="null"/> for a service that needs none.</param>
public sealed record AiConnection(AiProvider Provider, Uri Endpoint, string Model, string? ApiKey)
{
    /// <summary>
    /// Describes the connection without the API key, so it can never end up in a log.
    /// </summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        return $"{Provider} {Endpoint.Host}, model {Model}";
    }
}

/// <summary>
/// Creates clients for AI services.
/// </summary>
public interface IAiClientFactory
{
    /// <summary>
    /// Creates a client. Its requests fail with <see cref="AiException"/>.
    /// </summary>
    /// <param name="connection">The service to talk to.</param>
    /// <returns>The client, which the caller disposes.</returns>
    IChatClient Create(AiConnection connection);
}

/// <summary>
/// The settings used when the host registers none: AI stays switched off.
/// </summary>
public sealed class DisabledAiSettings : IAiSettings
{
    /// <inheritdoc />
    public AiConfiguration Ai { get; private set; } = AiConfiguration.Default;

    /// <inheritdoc />
    public event EventHandler? AiChanged;

    /// <inheritdoc />
    public void SetAi(AiConfiguration value)
    {
        Ai = value;
        AiChanged?.Invoke(this, EventArgs.Empty);
    }
}
