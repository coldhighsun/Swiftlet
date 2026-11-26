using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Decides for every database file whether it is encrypted, converts plain ones, and never removes or overwrites data it
/// cannot read.
/// </summary>
/// <param name="dataDirectory">The application data directory.</param>
/// <param name="secrets">The store holding the key.</param>
/// <param name="keychainAvailable">Whether the store is protected by the system; without that nothing is encrypted.</param>
/// <param name="migrator">Converts plain databases; the SQLite one when omitted.</param>
/// <param name="time">The clock naming renamed files; the system clock when omitted.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class DatabaseProtection(
    string dataDirectory,
    ISecretStore secrets,
    bool keychainAvailable,
    IDatabaseEncryptionMigrator? migrator = null,
    TimeProvider? time = null,
    ILogger<DatabaseProtection>? logger = null) : IDatabaseProtection, IDatabaseConnectionStrings
{
    /// <summary>
    /// One database file and what it turned out to be.
    /// </summary>
    /// <param name="Path">The full path.</param>
    /// <param name="AccountId">The account, or <see langword="null"/> for the global store.</param>
    /// <param name="State">The state.</param>
    private sealed record DatabaseFile(string Path, Guid? AccountId, DatabaseFileState State);

    /// <summary>
    /// Everything learned by looking at the key and the files.
    /// </summary>
    /// <param name="Result">The problem found.</param>
    /// <param name="Key">The usable key, if any.</param>
    /// <param name="Files">The database files; empty when they could not be examined.</param>
    private sealed record Inspection(DatabaseProtectionResult Result, string? Key, IReadOnlyList<DatabaseFile> Files);

    /// <summary>
    /// The full path of the global store.
    /// </summary>
    private readonly string _globalPath = Path.GetFullPath(Path.Combine(dataDirectory, "swiftlet.db"));

    /// <summary>
    /// Whether each known database file is encrypted.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _encrypted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads and creates the key.
    /// </summary>
    private readonly DatabaseKeyProvider _keys = new(secrets);

    /// <summary>
    /// Converts plain databases.
    /// </summary>
    private readonly IDatabaseEncryptionMigrator _migrator = migrator ?? new DatabaseEncryptionMigrator();

    /// <summary>
    /// The verified key of this run, if any.
    /// </summary>
    private string? _key;

    /// <summary>
    /// Whether all databases may be used.
    /// </summary>
    private bool _ready;

    /// <summary>
    /// Whether the global store may be used although a problem remains.
    /// </summary>
    private bool _globalUsable;

    /// <summary>
    /// The problem of the last preparation.
    /// </summary>
    private DatabaseProtectionProblem _lastProblem;

    /// <inheritdoc />
    public DatabaseProtectionStatus Status
    {
        get
        {
            if (!keychainAvailable)
            {
                return DatabaseProtectionStatus.NotEncryptedNoKeychain;
            }

            return _encrypted.Any(entry => !entry.Value && File.Exists(entry.Key))
                ? DatabaseProtectionStatus.PartlyEncrypted
                : DatabaseProtectionStatus.Encrypted;
        }
    }

    /// <inheritdoc />
    public string GetConnectionString(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var path = Path.GetFullPath(databasePath);
        if (!_ready && !(_globalUsable && string.Equals(path, _globalPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The databases have not been prepared.");
        }

        bool encrypted;
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
        {
            // The caller is about to create the file; what is chosen here has to hold for every later connection.
            encrypted = _key is not null;
            _encrypted[path] = encrypted;
        }
        else if (!_encrypted.TryGetValue(path, out encrypted))
        {
            throw new InvalidOperationException($"The state of the database {file.Name} is unknown.");
        }

        return SqliteConnectionStrings.Build(databasePath, encrypted ? _key : null);
    }

    /// <inheritdoc />
    public async Task<DatabaseProtectionAssessment> AssessAsync(CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(cancellationToken);
        var pending = keychainAvailable ? inspection.Files.Count(f => f.State == DatabaseFileState.Plain) : 0;
        return new DatabaseProtectionAssessment(inspection.Result, pending);
    }

    /// <inheritdoc />
    public async Task<DatabaseProtectionResult> PrepareAsync(
        IProgress<DatabaseConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _ready = false;
        _globalUsable = false;

        FileStream held;
        try
        {
            held = AcquireLock();
        }
        catch (IOException ex)
        {
            // Another instance holds the lock; it creates the key and converts, so this one must not.
            logger?.LogWarning(ex, "The database preparation lock is taken");
            return Finish(new DatabaseProtectionResult(DatabaseProtectionProblem.Busy, Detail: ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            // The data directory cannot be written; the user has to see why.
            logger?.LogError(ex, "The database preparation lock cannot be created");
            return Finish(new DatabaseProtectionResult(DatabaseProtectionProblem.DatabaseError, Detail: ex.Message));
        }

        await using (held)
        {
            return Finish(await PrepareLockedAsync(progress, cancellationToken));
        }
    }

    /// <inheritdoc />
    public void ContinueUnencrypted()
    {
        if (_lastProblem != DatabaseProtectionProblem.ConversionFailed)
        {
            throw new InvalidOperationException("Only a failed conversion can be postponed.");
        }

        logger?.LogWarning("Continuing with databases that are not encrypted");
        _ready = true;
    }

    /// <inheritdoc />
    public async Task ResetAllAsync(CancellationToken cancellationToken = default)
    {
        if (!keychainAvailable)
        {
            throw new InvalidOperationException("Nothing can be reset without a system keychain.");
        }

        await using var held = AcquireLock();
        var inspection = await InspectForResetAsync(cancellationToken);
        var globalLost = inspection.Files.Any(f => f.AccountId is null && f.State == DatabaseFileState.Unreadable);
        var stamp = Stamp();
        foreach (var file in inspection.Files)
        {
            // Without the global store the account databases belong to nobody; otherwise only unreadable ones are in the way.
            if (globalLost || file.State == DatabaseFileState.Unreadable)
            {
                PutAside(file.Path, stamp);
            }
        }

        // A key that can still be read is kept: it may be the one that opens the renamed files.
        if (inspection.Key is null)
        {
            await _keys.CreateAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task ResetAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var held = AcquireLock();
        var inspection = await InspectForResetAsync(cancellationToken);
        var stamp = Stamp();
        foreach (var file in inspection.Files.Where(f => f.AccountId == accountId))
        {
            PutAside(file.Path, stamp);
        }
    }

    /// <summary>
    /// Remembers a preparation result.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <returns>The same result.</returns>
    private DatabaseProtectionResult Finish(DatabaseProtectionResult result)
    {
        _lastProblem = result.Problem;
        if (result.Problem == DatabaseProtectionProblem.None)
        {
            _ready = true;
        }

        logger?.LogInformation("Database preparation finished: {Problem}", result.Problem);
        return result;
    }

    /// <summary>
    /// Prepares the databases while the lock is held.
    /// </summary>
    /// <param name="progress">Reports each conversion.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    private async Task<DatabaseProtectionResult> PrepareLockedAsync(
        IProgress<DatabaseConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(cancellationToken);
        _key = inspection.Key;
        _encrypted.Clear();
        foreach (var file in inspection.Files.Where(f => f.State is DatabaseFileState.Plain or DatabaseFileState.Encrypted))
        {
            _encrypted[file.Path] = file.State == DatabaseFileState.Encrypted;
        }

        if (inspection.Result.Problem != DatabaseProtectionProblem.None)
        {
            // The account's name is read from the global store before the user decides.
            _globalUsable = inspection.Result.Problem == DatabaseProtectionProblem.AccountUnreadable;
            return inspection.Result;
        }

        if (!keychainAvailable)
        {
            return inspection.Result;
        }

        if (_key is null)
        {
            try
            {
                _key = await _keys.CreateAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keychain tools fail in many ways; in every case the user can only fix the keychain and retry.
                logger?.LogError(ex, "Could not create the database key");
                return new DatabaseProtectionResult(DatabaseProtectionProblem.KeyStoreUnavailable, Detail: ex.Message);
            }
        }

        var plain = inspection.Files.Where(f => f.State == DatabaseFileState.Plain).ToList();
        for (var index = 0; index < plain.Count; index++)
        {
            progress?.Report(new DatabaseConversionProgress(index + 1, plain.Count));
            var file = plain[index];
            logger?.LogInformation("Encrypting the database {File}", Path.GetFileName(file.Path));
            try
            {
                await _migrator.EncryptAsync(file.Path, _key, cancellationToken);
                _encrypted[file.Path] = true;
            }
            catch (DatabaseConversionException ex)
            {
                logger?.LogError(ex, "Could not encrypt the database {File}", Path.GetFileName(file.Path));
                return await AfterFailedConversionAsync(file, ex, cancellationToken);
            }
        }

        return new DatabaseProtectionResult(DatabaseProtectionProblem.None);
    }

    /// <summary>
    /// Looks at what a failed conversion left behind before offering to go on without encryption.
    /// </summary>
    /// <param name="file">The database.</param>
    /// <param name="failure">The failure.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    private async Task<DatabaseProtectionResult> AfterFailedConversionAsync(
        DatabaseFile file,
        DatabaseConversionException failure,
        CancellationToken cancellationToken)
    {
        try
        {
            // A late step may have failed after the file was already rewritten.
            var state = await DatabaseProbe.ProbeAsync(file.Path, _key, cancellationToken);
            if (state == DatabaseFileState.Unreadable)
            {
                return new DatabaseProtectionResult(DatabaseProtectionProblem.DatabaseError, file.AccountId, failure.Failure, failure.Message);
            }

            _encrypted[file.Path] = state == DatabaseFileState.Encrypted;
        }
        catch (Exception ex) when (ex is SqliteException { SqliteErrorCode: 5 or 6 })
        {
            // Still held by whoever blocked the conversion, so the file is unchanged and its recorded state stands.
            logger?.LogWarning(ex, "The database {File} is still locked", Path.GetFileName(file.Path));
        }

        _globalUsable = true;
        return new DatabaseProtectionResult(
            DatabaseProtectionProblem.ConversionFailed, file.AccountId, failure.Failure, failure.Message);
    }

    /// <summary>
    /// Reads the key and probes every database file, changing nothing but applying interrupted writes.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was found.</returns>
    private async Task<Inspection> InspectAsync(CancellationToken cancellationToken)
    {
        string? key = null;
        var damaged = false;
        if (keychainAvailable)
        {
            try
            {
                key = await _keys.ReadAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
            {
                // The stored value can never be used, for example after copying the data to another user or computer.
                logger?.LogError(ex, "The stored database key cannot be used");
                damaged = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keychain tools fail in many ways (locked, no session); nothing may be concluded about the key.
                logger?.LogError(ex, "Could not read the database key");
                return Failed(DatabaseProtectionProblem.KeyStoreUnavailable, ex.Message);
            }
        }

        var files = new List<DatabaseFile>();
        try
        {
            foreach (var (path, accountId) in DatabasePaths())
            {
                var state = await DatabaseProbe.ProbeAsync(path, key, cancellationToken);
                if (state != DatabaseFileState.Missing)
                {
                    files.Add(new DatabaseFile(path, accountId, state));
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Locked or failing files say nothing about the key, so they are reported as they are.
            logger?.LogError(ex, "Could not examine the databases");
            return Failed(DatabaseProtectionProblem.DatabaseError, ex.Message);
        }

        var unreadable = files.Where(f => f.State == DatabaseFileState.Unreadable).ToList();
        var result = new DatabaseProtectionResult(DatabaseProtectionProblem.None);
        if (!keychainAvailable)
        {
            if (unreadable.Count > 0)
            {
                result = new DatabaseProtectionResult(DatabaseProtectionProblem.KeyStoreUnavailable);
            }
        }
        else if (damaged || (unreadable.Count > 0 && (key is null || unreadable.Any(f => f.AccountId is null))))
        {
            result = new DatabaseProtectionResult(DatabaseProtectionProblem.KeyLost);
        }
        else if (unreadable.Count > 0)
        {
            result = new DatabaseProtectionResult(DatabaseProtectionProblem.AccountUnreadable, unreadable[0].AccountId);
        }

        return new Inspection(result, key, files);

        static Inspection Failed(DatabaseProtectionProblem problem, string detail)
        {
            return new Inspection(new DatabaseProtectionResult(problem, Detail: detail), null, []);
        }
    }

    /// <summary>
    /// Inspects before a reset, which is only safe when both the keychain and the files answered.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was found.</returns>
    private async Task<Inspection> InspectForResetAsync(CancellationToken cancellationToken)
    {
        // Connections kept for reuse would keep the files open (and locked on Windows).
        SqliteConnection.ClearAllPools();
        _ready = false;
        _globalUsable = false;

        var inspection = await InspectAsync(cancellationToken);
        return inspection.Result.Problem is DatabaseProtectionProblem.KeyStoreUnavailable or DatabaseProtectionProblem.DatabaseError
            ? throw new InvalidOperationException($"The data cannot be reset now: {inspection.Result.Detail ?? "the keychain is unavailable"}")
            : inspection;
    }

    /// <summary>
    /// Lists the database files, the global store first.
    /// </summary>
    /// <returns>Each path with its account.</returns>
    private IEnumerable<(string Path, Guid? AccountId)> DatabasePaths()
    {
        yield return (_globalPath, null);

        var accounts = Path.GetFullPath(Path.Combine(dataDirectory, "accounts"));
        if (!Directory.Exists(accounts))
        {
            yield break;
        }

        foreach (var path in Directory.GetFiles(accounts, "*.db").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id))
            {
                yield return (path, id);
            }
        }
    }

    /// <summary>
    /// Takes the lock that keeps two instances from creating keys or converting at once.
    /// </summary>
    /// <returns>The held lock.</returns>
    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(dataDirectory);
        return new FileStream(
            Path.Combine(dataDirectory, "encryption.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// Gets the time stamp used in the names of renamed files.
    /// </summary>
    /// <returns>The stamp.</returns>
    private string Stamp()
    {
        return (time ?? TimeProvider.System).GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Renames a database and its side files so that renaming them back restores the database.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="stamp">The time stamp.</param>
    private void PutAside(string path, string stamp)
    {
        var target = $"{path}.unreadable-{stamp}";
        for (var number = 2; File.Exists(target); number++)
        {
            target = $"{path}.unreadable-{stamp}-{number}";
        }

        logger?.LogWarning("Putting aside the database {File} as {Target}", Path.GetFileName(path), Path.GetFileName(target));
        foreach (var suffix in new[] { "-wal", "-shm", "-journal", string.Empty })
        {
            if (File.Exists(path + suffix))
            {
                File.Move(path + suffix, target + suffix);
            }
        }

        _encrypted.TryRemove(path, out _);
    }
}
