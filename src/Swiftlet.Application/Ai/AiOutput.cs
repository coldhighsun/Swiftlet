namespace Swiftlet.Application.Ai;

/// <summary>
/// The language an AI answer is written in.
/// </summary>
public enum AiLanguage
{
    /// <summary>
    /// English.
    /// </summary>
    English,

    /// <summary>
    /// Simplified Chinese.
    /// </summary>
    SimplifiedChinese,
}

/// <summary>
/// What a part of a streamed AI answer says.
/// </summary>
public enum AiOutputKind
{
    /// <summary>
    /// The next piece of the answer.
    /// </summary>
    Text,

    /// <summary>
    /// Only the beginning of the message was sent, because it is longer than the input limit.
    /// </summary>
    InputTruncated,

    /// <summary>
    /// The answer stopped at the output limit.
    /// </summary>
    OutputTruncated,

    /// <summary>
    /// The model declined; any text received before is to be discarded.
    /// </summary>
    Refused,
}

/// <summary>
/// A part of a streamed AI answer.
/// </summary>
/// <param name="Kind">What the part says.</param>
/// <param name="Text">The text of an <see cref="AiOutputKind.Text"/> part.</param>
public sealed record AiOutput(AiOutputKind Kind, string Text = "");
