namespace Swiftlet.Application.Ai;

/// <summary>
/// The kind of service that answers AI requests.
/// </summary>
public enum AiProvider
{
    /// <summary>
    /// The Claude Messages API.
    /// </summary>
    Claude,

    /// <summary>
    /// Any service speaking the OpenAI chat completions protocol, local ones included.
    /// </summary>
    OpenAiCompatible,
}

/// <summary>
/// The saved AI settings. The API key is not part of them; it lives in the secret store.
/// </summary>
/// <param name="IsEnabled">Whether the AI features are switched on.</param>
/// <param name="Provider">The kind of service.</param>
/// <param name="Endpoint">The base address of the service.</param>
/// <param name="Model">The model identifier.</param>
/// <param name="MaxInputCharacters">The largest amount of message text sent with one request.</param>
/// <param name="ConfirmedDestination">The destination the user agreed to send mail content to, see <see cref="AiEndpoint.Destination"/>.</param>
public sealed record AiConfiguration(
    bool IsEnabled,
    AiProvider Provider,
    string Endpoint,
    string Model,
    int MaxInputCharacters,
    string? ConfirmedDestination)
{
    /// <summary>
    /// The default input limit.
    /// </summary>
    public const int DefaultMaxInputCharacters = 12_000;

    /// <summary>
    /// The smallest input limit that can be chosen.
    /// </summary>
    public const int MinInputCharacters = 1_000;

    /// <summary>
    /// The largest input limit that can be chosen.
    /// </summary>
    public const int MaxAllowedInputCharacters = 500_000;

    /// <summary>
    /// Gets the settings of a new installation: switched off.
    /// </summary>
    public static AiConfiguration Default { get; } = new(
        false,
        AiProvider.Claude,
        AiEndpoint.DefaultFor(AiProvider.Claude),
        AiModels.DefaultClaudeModel,
        DefaultMaxInputCharacters,
        null);

    /// <summary>
    /// Gets a value indicating whether mail content may be sent: switched on, complete, and addressed to the destination
    /// the user agreed to. A settings file edited to point elsewhere is therefore not ready.
    /// </summary>
    public bool IsReady =>
        IsEnabled
        && !string.IsNullOrWhiteSpace(Model)
        && AiEndpoint.TryParse(Endpoint, out var endpoint)
        && ConfirmedDestination == AiEndpoint.Destination(Provider, endpoint);
}

/// <summary>
/// The models offered in the settings; any other identifier can be typed.
/// </summary>
public static class AiModels
{
    /// <summary>
    /// The Claude model selected by default.
    /// </summary>
    public const string DefaultClaudeModel = "claude-opus-5-5";

    /// <summary>
    /// Gets the suggested Claude models.
    /// </summary>
    public static IReadOnlyList<string> Claude { get; } =
        [DefaultClaudeModel, "claude-sonnet-5-5", "claude-haiku-5-5"];
}
