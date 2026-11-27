namespace Swiftlet.Domain.Messages;

/// <summary>
/// How important a message is to the user. The state is local; the values are stored, so they must not change.
/// </summary>
public enum MessagePriority
{
    /// <summary>
    /// More important than usual.
    /// </summary>
    High = 1,

    /// <summary>
    /// Nothing special.
    /// </summary>
    Normal = 2,

    /// <summary>
    /// Can wait or be skipped.
    /// </summary>
    Low = 3,
}
