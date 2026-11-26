namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Brings the databases into their protected state before anything uses them.
/// </summary>
public interface IDatabaseProtection
{
    /// <summary>
    /// Gets how well the data is protected; meaningful once the databases were prepared.
    /// </summary>
    DatabaseProtectionStatus Status { get; }

    /// <summary>
    /// Looks at the databases without writing a key or converting anything.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What preparing would meet.</returns>
    Task<DatabaseProtectionAssessment> AssessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the key if needed and encrypts plain databases.
    /// </summary>
    /// <param name="progress">Reports each conversion before it starts.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome; the databases are usable when its problem is <see cref="DatabaseProtectionProblem.None"/>.</returns>
    Task<DatabaseProtectionResult> PrepareAsync(IProgress<DatabaseConversionProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uses the databases that could not be converted as they are, for this run.
    /// </summary>
    /// <exception cref="InvalidOperationException">The last preparation did not fail to convert.</exception>
    void ContinueUnencrypted();

    /// <summary>
    /// Puts all unreadable data aside, keeping the files under new names.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the files are renamed.</returns>
    Task ResetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts one account's database aside, keeping the files under new names.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the files are renamed.</returns>
    Task ResetAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
}
