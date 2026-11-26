namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// What stands in the way of using the databases.
/// </summary>
public enum DatabaseProtectionProblem
{
    /// <summary>
    /// Nothing.
    /// </summary>
    None,

    /// <summary>
    /// A plain database could not be encrypted; it can still be used as it is.
    /// </summary>
    ConversionFailed,

    /// <summary>
    /// The system keychain cannot be used right now.
    /// </summary>
    KeyStoreUnavailable,

    /// <summary>
    /// Encrypted data exists, but this run found no system keychain when it started.
    /// </summary>
    KeyStoreMissing,

    /// <summary>
    /// The key is missing, damaged, or does not open the global store.
    /// </summary>
    KeyLost,

    /// <summary>
    /// One account database cannot be read.
    /// </summary>
    AccountUnreadable,

    /// <summary>
    /// Another instance is preparing the databases.
    /// </summary>
    Busy,

    /// <summary>
    /// A database file could not be accessed.
    /// </summary>
    DatabaseError,
}

/// <summary>
/// How well the local data is protected.
/// </summary>
public enum DatabaseProtectionStatus
{
    /// <summary>
    /// Every database is encrypted.
    /// </summary>
    Encrypted,

    /// <summary>
    /// There is no system keychain, so nothing is encrypted.
    /// </summary>
    NotEncryptedNoKeychain,

    /// <summary>
    /// At least one database is still plain.
    /// </summary>
    PartlyEncrypted,
}

/// <summary>
/// The outcome of preparing the databases.
/// </summary>
/// <param name="Problem">What stands in the way.</param>
/// <param name="AccountId">The account of an unreadable account database.</param>
/// <param name="Conversion">Why a conversion failed.</param>
/// <param name="Detail">The message of the underlying failure.</param>
public sealed record DatabaseProtectionResult(
    DatabaseProtectionProblem Problem,
    Guid? AccountId = null,
    DatabaseConversionFailure? Conversion = null,
    string? Detail = null);

/// <summary>
/// What preparing the databases would meet.
/// </summary>
/// <param name="Result">The problem already visible.</param>
/// <param name="PendingConversions">The number of databases to encrypt.</param>
public sealed record DatabaseProtectionAssessment(DatabaseProtectionResult Result, int PendingConversions);

/// <summary>
/// The conversion that is about to run.
/// </summary>
/// <param name="Current">Its number, starting at 1.</param>
/// <param name="Total">The number of conversions.</param>
public sealed record DatabaseConversionProgress(int Current, int Total);
