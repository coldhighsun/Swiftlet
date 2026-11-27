namespace Swiftlet.Application.Ai;

/// <summary>
/// Why an AI request failed.
/// </summary>
public enum AiFailure
{
    /// <summary>
    /// A cause that fits no other value.
    /// </summary>
    Unknown,

    /// <summary>
    /// The API key was missing, wrong or not allowed to do this.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// The address or the model does not exist.
    /// </summary>
    NotFound,

    /// <summary>
    /// Too many requests, or no credit left.
    /// </summary>
    RateLimited,

    /// <summary>
    /// The service did not accept the request, for example because the text is too long.
    /// </summary>
    Rejected,

    /// <summary>
    /// The service failed or is overloaded.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The service could not be reached.
    /// </summary>
    Network,

    /// <summary>
    /// No answer arrived in time.
    /// </summary>
    Timeout,

    /// <summary>
    /// AI is switched off or not set up, so nothing was sent.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The user's text is longer than the input limit, so nothing was sent.
    /// </summary>
    TooLong,
}

/// <summary>
/// A failed AI request. It deliberately keeps neither the service's answer nor the original exception: a service may
/// echo the request, which holds mail content, and exceptions are written to the log.
/// </summary>
public sealed class AiException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AiException"/> class.
    /// </summary>
    /// <param name="failure">Why the request failed.</param>
    /// <param name="statusCode">The HTTP status code, if a response arrived.</param>
    public AiException(AiFailure failure, int? statusCode = null)
        : base(statusCode is { } status ? $"AI request failed: {failure} (HTTP {status})." : $"AI request failed: {failure}.")
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets why the request failed.
    /// </summary>
    public AiFailure Failure { get; }

    /// <summary>
    /// Gets the HTTP status code, if a response arrived.
    /// </summary>
    public int? StatusCode { get; }
}
