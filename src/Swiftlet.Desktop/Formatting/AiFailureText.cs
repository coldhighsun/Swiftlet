using Swiftlet.Application.Ai;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.Formatting;

/// <summary>
/// The text shown for a failed AI request.
/// </summary>
public static class AiFailureText
{
    /// <summary>
    /// Describes why a request failed.
    /// </summary>
    /// <param name="localizer">The localizer.</param>
    /// <param name="failure">The failure.</param>
    /// <returns>The text.</returns>
    public static string Describe(ILocalizer localizer, AiFailure failure)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return failure switch
        {
            AiFailure.Unauthorized => localizer["Ai_ErrorUnauthorized"],
            AiFailure.NotFound => localizer["Ai_ErrorNotFound"],
            AiFailure.RateLimited => localizer["Ai_ErrorRateLimited"],
            AiFailure.Rejected => localizer["Ai_ErrorRejected"],
            AiFailure.Unavailable => localizer["Ai_ErrorUnavailable"],
            AiFailure.Network => localizer["Ai_ErrorNetwork"],
            AiFailure.Timeout => localizer["Ai_ErrorTimeout"],
            AiFailure.NotConfigured => localizer["Ai_ErrorNotConfigured"],
            _ => localizer["Ai_ErrorUnknown"],
        };
    }
}
