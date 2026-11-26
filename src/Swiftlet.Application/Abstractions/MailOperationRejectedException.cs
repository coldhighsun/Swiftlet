namespace Swiftlet.Application.Abstractions;

/// <summary>
/// The mail server refused an operation, so repeating it unchanged cannot succeed.
/// </summary>
public sealed class MailOperationRejectedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MailOperationRejectedException"/> class.
    /// </summary>
    public MailOperationRejectedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MailOperationRejectedException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public MailOperationRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MailOperationRejectedException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public MailOperationRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
