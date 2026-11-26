using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Application.Abstractions;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DatabaseProtection"/> against real files and an in-memory secret store.
/// </summary>
public sealed class DatabaseProtectionTests : IDisposable
{
    /// <summary>
    /// A secret store whose reads fail.
    /// </summary>
    /// <param name="failure">The failure reads throw.</param>
    private sealed class ThrowingSecretStore(Exception failure) : ISecretStore
    {
        /// <inheritdoc />
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromException<string?>(failure);
        }

        /// <inheritdoc />
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return Task.FromException(new InvalidOperationException("Nothing may be written."));
        }

        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A keychain that, while locked, answers that nothing is stored, but still accepts writes.
    /// </summary>
    /// <param name="inner">The store behind it.</param>
    private sealed class ForgetfulSecretStore(InMemorySecretStore inner) : ISecretStore
    {
        /// <inheritdoc />
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            return key.Length > 0 && Written.Contains(key)
                ? inner.GetAsync(key, cancellationToken)
                : Task.FromResult<string?>(null);
        }

        /// <summary>
        /// Gets the names written through this instance.
        /// </summary>
        public HashSet<string> Written { get; } = [];

        /// <inheritdoc />
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Written.Add(key);
            return inner.SetAsync(key, value, cancellationToken);
        }

        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            return inner.RemoveAsync(key, cancellationToken);
        }
    }

    /// <summary>
    /// A migrator that always fails.
    /// </summary>
    private sealed class FailingMigrator : IDatabaseEncryptionMigrator
    {
        /// <inheritdoc />
        public Task EncryptAsync(string path, string keyHex, CancellationToken cancellationToken = default)
        {
            return Task.FromException(new DatabaseConversionException(DatabaseConversionFailure.InUse, "busy"));
        }
    }

    /// <summary>
    /// Collects progress reports synchronously.
    /// </summary>
    private sealed class ProgressList : IProgress<DatabaseConversionProgress>
    {
        /// <summary>
        /// Gets the reports.
        /// </summary>
        public List<DatabaseConversionProgress> Reports { get; } = [];

        /// <inheritdoc />
        public void Report(DatabaseConversionProgress value)
        {
            Reports.Add(value);
        }
    }

    /// <summary>
    /// The data directory of one test.
    /// </summary>
    private readonly EncryptionTestDirectory _directory = new();

    /// <summary>
    /// The secret store of one test.
    /// </summary>
    private readonly InMemorySecretStore _secrets = new();

    /// <summary>
    /// The first account of a test; sorts before <see cref="_second"/>.
    /// </summary>
    private readonly Guid _first = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// The second account of a test.
    /// </summary>
    private readonly Guid _second = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Gets the global database path.
    /// </summary>
    private string Global => _directory.File("swiftlet.db");

    /// <summary>
    /// Gets the path of an account database.
    /// </summary>
    /// <param name="id">The account.</param>
    /// <returns>The path.</returns>
    private string Account(Guid id)
    {
        return _directory.File(Path.Combine("accounts", $"{id:D}.db"));
    }

    /// <summary>
    /// Creates the object under test.
    /// </summary>
    /// <param name="keychain">Whether a system keychain exists.</param>
    /// <param name="secrets">The secret store; the in-memory one when omitted.</param>
    /// <param name="migrator">The migrator; the real one when omitted.</param>
    /// <returns>The protection.</returns>
    private DatabaseProtection Create(bool keychain = true, ISecretStore? secrets = null, IDatabaseEncryptionMigrator? migrator = null)
    {
        return new DatabaseProtection(_directory.Root, secrets ?? _secrets, keychain, migrator);
    }

    /// <summary>
    /// Stores a key as an earlier run would have.
    /// </summary>
    /// <param name="key">The key.</param>
    private void StoreKey(string key = EncryptionTestDirectory.Key)
    {
        File.WriteAllText(_directory.File("database.key-name"), "database:key:test");
        _secrets.Values["database:key:test"] = key;
    }

    /// <summary>
    /// Gets the key the application currently uses.
    /// </summary>
    /// <returns>The key.</returns>
    private string StoredKey()
    {
        return _secrets.Values[File.ReadAllLines(_directory.File("database.key-name"))[^1]];
    }

    /// <summary>
    /// Verifies a new installation gets a key and encrypted databases.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_EmptyDirectory_CreatesKeyAndNewDatabasesAreEncrypted()
    {
        var protection = Create();

        var result = await protection.PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.None);
        var key = StoredKey();
        protection.GetConnectionString(Global).Should().Contain($"raw:{key}");
        protection.GetConnectionString(Account(_first)).Should().Contain($"raw:{key}");
        protection.Status.Should().Be(DatabaseProtectionStatus.Encrypted);
    }

    /// <summary>
    /// Verifies existing plain databases are converted, the global one first.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_PlainDatabases_EncryptsGlobalFirstAndReportsProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        EncryptionTestDirectory.CreateDatabase(Global, null);
        EncryptionTestDirectory.CreateDatabase(Account(_first), null);
        EncryptionTestDirectory.CreateDatabase(Account(_second), null);
        var progress = new ProgressList();
        var protection = Create();

        var result = await protection.PrepareAsync(progress, ct);

        result.Problem.Should().Be(DatabaseProtectionProblem.None);
        progress.Reports.Should().Equal(new DatabaseConversionProgress(1, 3), new DatabaseConversionProgress(2, 3), new DatabaseConversionProgress(3, 3));
        var key = StoredKey();
        foreach (var path in new[] { Global, Account(_first), Account(_second) })
        {
            (await DatabaseProbe.ProbeAsync(path, key, ct)).Should().Be(DatabaseFileState.Encrypted);
            protection.GetConnectionString(path).Should().Contain("raw:");
        }
    }

    /// <summary>
    /// Verifies the read-only check counts the work without doing any.
    /// </summary>
    [Fact]
    public async Task AssessAsync_PlainDatabases_ReportsPendingWithoutWritingKey()
    {
        EncryptionTestDirectory.CreateDatabase(Global, null);
        EncryptionTestDirectory.CreateDatabase(Account(_first), null);

        var assessment = await Create().AssessAsync(TestContext.Current.CancellationToken);

        assessment.Result.Problem.Should().Be(DatabaseProtectionProblem.None);
        assessment.PendingConversions.Should().Be(2);
        _secrets.Values.Should().BeEmpty();
        EncryptionTestDirectory.Scalar(Global, null, "SELECT COUNT(*) FROM Items").Should().Be(1L);
    }

    /// <summary>
    /// Verifies nothing is encrypted without a system keychain.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_NoKeychain_LeavesPlainAndWritesNoKey()
    {
        EncryptionTestDirectory.CreateDatabase(Global, null);
        var protection = Create(keychain: false);

        var result = await protection.PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.None);
        _secrets.Values.Should().BeEmpty();
        protection.GetConnectionString(Global).Should().NotContain("Password");
        protection.GetConnectionString(Account(_first)).Should().NotContain("Password");
        protection.Status.Should().Be(DatabaseProtectionStatus.NotEncryptedNoKeychain);
    }

    /// <summary>
    /// Verifies a missing keychain in front of encrypted data is not mistaken for a lost key.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_NoKeychainWithEncryptedDatabase_ReportsKeyStoreUnavailable()
    {
        EncryptionTestDirectory.CreateDatabase(Global, EncryptionTestDirectory.Key);

        var result = await Create(keychain: false).PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.KeyStoreMissing);
    }

    /// <summary>
    /// Verifies a missing key is reported and no new one hides the loss.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_KeyMissing_ReportsKeyLostAndStoresNothing()
    {
        EncryptionTestDirectory.CreateDatabase(Global, EncryptionTestDirectory.Key);

        var result = await Create().PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.KeyLost);
        _secrets.Values.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a key file copied from another user or computer counts as lost, so it can be reset.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_KeyCannotBeDecrypted_ReportsKeyLost()
    {
        // The name file makes the application ask the store.
        StoreKey();
        var protection = Create(secrets: new ThrowingSecretStore(new CryptographicException("wrong user")));

        var result = await protection.PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.KeyLost);
    }

    /// <summary>
    /// Verifies a keychain that fails to answer can only be retried.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_KeyStoreThrows_ReportsKeyStoreUnavailable()
    {
        // The name file makes the application ask the store.
        StoreKey();
        var protection = Create(secrets: new ThrowingSecretStore(new InvalidOperationException("locked")));

        var result = await protection.PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.KeyStoreUnavailable);
        result.Detail.Should().Be("locked");
    }

    /// <summary>
    /// Verifies one unreadable account database is named while the global store stays usable.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_WrongKeyForOneAccount_ReportsThatAccount()
    {
        StoreKey();
        EncryptionTestDirectory.CreateDatabase(Global, EncryptionTestDirectory.Key);
        EncryptionTestDirectory.CreateDatabase(Account(_first), EncryptionTestDirectory.Key);
        EncryptionTestDirectory.CreateDatabase(Account(_second), EncryptionTestDirectory.OtherKey);
        var protection = Create();

        var result = await protection.PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.AccountUnreadable);
        result.AccountId.Should().Be(_second);
        protection.GetConnectionString(Global).Should().Contain("raw:");
        var act = () => protection.GetConnectionString(Account(_first));
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies a failed conversion can be postponed: the file opens plain, new files are still encrypted.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_ConversionFails_ContinueUnencryptedOpensThatFilePlain()
    {
        EncryptionTestDirectory.CreateDatabase(Global, null);
        var protection = Create(migrator: new FailingMigrator());

        var result = await protection.PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.ConversionFailed);
        result.Conversion.Should().Be(DatabaseConversionFailure.InUse);

        protection.ContinueUnencrypted();

        protection.GetConnectionString(Global).Should().NotContain("Password");
        protection.GetConnectionString(Account(_first)).Should().Contain("raw:");
        protection.Status.Should().Be(DatabaseProtectionStatus.PartlyEncrypted);
    }

    /// <summary>
    /// Verifies a second instance preparing at the same time neither creates a key nor converts.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_LockHeld_ReportsBusyAndStoresNothing()
    {
        await using var held = new FileStream(_directory.File("encryption.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var result = await Create().PrepareAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Problem.Should().Be(DatabaseProtectionProblem.Busy);
        _secrets.Values.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a reset after a lost key keeps the old files under new names and starts over.
    /// </summary>
    [Fact]
    public async Task ResetAllAsync_KeyLost_RenamesFilesAndCreatesKey()
    {
        var ct = TestContext.Current.CancellationToken;
        EncryptionTestDirectory.CreateDatabase(Global, EncryptionTestDirectory.Key);
        EncryptionTestDirectory.CreateDatabase(Account(_first), EncryptionTestDirectory.Key);
        var original = await File.ReadAllBytesAsync(Global, ct);
        var protection = Create();
        await protection.PrepareAsync(cancellationToken: ct);

        await protection.ResetAllAsync(ct);

        File.Exists(Global).Should().BeFalse();
        File.Exists(Account(_first)).Should().BeFalse();
        var kept = Directory.GetFiles(_directory.Root, "swiftlet.db.unreadable-*").Should().ContainSingle().Subject;
        (await File.ReadAllBytesAsync(kept, ct)).Should().Equal(original);
        Directory.GetFiles(_directory.File("accounts"), "*.unreadable-*").Should().ContainSingle();
        StoredKey().Should().NotBeEmpty();
        (await protection.PrepareAsync(cancellationToken: ct)).Problem.Should().Be(DatabaseProtectionProblem.None);
    }

    /// <summary>
    /// Verifies a key that is still readable survives a reset.
    /// </summary>
    [Fact]
    public async Task ResetAllAsync_GlobalDoesNotFitKey_KeepsKey()
    {
        var ct = TestContext.Current.CancellationToken;
        StoreKey();
        EncryptionTestDirectory.CreateDatabase(Global, EncryptionTestDirectory.OtherKey);

        await Create().ResetAllAsync(ct);

        File.Exists(Global).Should().BeFalse();
        StoredKey().Should().Be(EncryptionTestDirectory.Key);
    }

    /// <summary>
    /// Verifies a keychain that wrongly reports no key does not lose it through a reset.
    /// </summary>
    [Fact]
    public async Task ResetAllAsync_KeyReadsAsMissing_DoesNotOverwriteStoredKey()
    {
        var ct = TestContext.Current.CancellationToken;
        StoreKey();
        EncryptionTestDirectory.CreateDatabase(Global, EncryptionTestDirectory.Key);
        var protection = Create(secrets: new ForgetfulSecretStore(_secrets));
        (await protection.PrepareAsync(cancellationToken: ct)).Problem.Should().Be(DatabaseProtectionProblem.KeyLost);

        await protection.ResetAllAsync(ct);

        _secrets.Values["database:key:test"].Should().Be(EncryptionTestDirectory.Key);
        (await File.ReadAllLinesAsync(_directory.File("database.key-name"), ct)).Should().Contain("database:key:test").And.HaveCount(2);
    }

    /// <summary>
    /// Verifies nothing is renamed while the keychain cannot be asked.
    /// </summary>
    [Fact]
    public async Task ResetAllAsync_KeyStoreUnavailable_ThrowsAndKeepsFiles()
    {
        // The name file makes the application ask the store.
        StoreKey();
        EncryptionTestDirectory.CreateDatabase(Global, EncryptionTestDirectory.Key);
        var protection = Create(secrets: new ThrowingSecretStore(new InvalidOperationException("locked")));

        var act = async () => await protection.ResetAllAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Global).Should().BeTrue();
    }

    /// <summary>
    /// Verifies resetting one account leaves everything else in place.
    /// </summary>
    [Fact]
    public async Task ResetAccountAsync_RenamesOnlyThatDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        StoreKey();
        EncryptionTestDirectory.CreateDatabase(Global, EncryptionTestDirectory.Key);
        EncryptionTestDirectory.CreateDatabase(Account(_first), EncryptionTestDirectory.Key);
        EncryptionTestDirectory.CreateDatabase(Account(_second), EncryptionTestDirectory.OtherKey);
        var protection = Create();

        await protection.ResetAccountAsync(_second, ct);

        File.Exists(Account(_second)).Should().BeFalse();
        File.Exists(Account(_first)).Should().BeTrue();
        File.Exists(Global).Should().BeTrue();
        (await protection.PrepareAsync(cancellationToken: ct)).Problem.Should().Be(DatabaseProtectionProblem.None);
    }

    /// <summary>
    /// Verifies no connection string is handed out before the databases are known.
    /// </summary>
    [Fact]
    public void GetConnectionString_BeforePrepare_Throws()
    {
        var act = () => Create().GetConnectionString(Global);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies continuing unencrypted is only possible after a failed conversion.
    /// </summary>
    [Fact]
    public void ContinueUnencrypted_WithoutFailure_Throws()
    {
        var act = () => Create().ContinueUnencrypted();

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies the account factory creates encrypted databases when given the connection strings.
    /// </summary>
    [Fact]
    public async Task Factory_WithConnectionStrings_CreatesEncryptedAccountDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var protection = Create();
        await protection.PrepareAsync(cancellationToken: ct);
        using var factory = new AccountDbContextFactory(_directory.File("accounts"), connections: protection);

        await using (var context = await factory.CreateAsync(_first, ct))
        {
            (await context.Folders.CountAsync(ct)).Should().Be(0);
        }

        // A second context of the same file must get the same connection string.
        await using (var context = await factory.CreateAsync(_first, ct))
        {
            (await context.Folders.CountAsync(ct)).Should().Be(0);
        }

        (await DatabaseProbe.ProbeAsync(Account(_first), StoredKey(), ct))
            .Should().Be(DatabaseFileState.Encrypted);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _directory.Dispose();
    }
}
