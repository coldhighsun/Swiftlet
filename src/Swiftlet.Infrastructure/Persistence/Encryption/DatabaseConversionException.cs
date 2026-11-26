namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Why a database could not be converted.
/// </summary>
public enum DatabaseConversionFailure
{
    /// <summary>
    /// The disk has too little free space.
    /// </summary>
    NotEnoughSpace,

    /// <summary>
    /// Another connection holds the database.
    /// </summary>
    InUse,

    /// <summary>
    /// Any other failure.
    /// </summary>
    Failed,
}

/// <summary>
/// A database could not be encrypted.
/// </summary>
/// <param name="failure">The reason.</param>
/// <param name="message">The description.</param>
/// <param name="innerException">The underlying failure, if any.</param>
public sealed class DatabaseConversionException(DatabaseConversionFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>
    /// Gets the reason.
    /// </summary>
    public DatabaseConversionFailure Failure { get; } = failure;
}
