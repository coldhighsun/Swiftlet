using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DatabaseKeyProvider"/>.
/// </summary>
public sealed class DatabaseKeyProviderTests : IDisposable
{
    /// <summary>
    /// The temporary directory holding the name file.
    /// </summary>
    private readonly EncryptionTestDirectory _directory = new();

    /// <summary>
    /// Gets the name file of the test.
    /// </summary>
    private string NameFile => _directory.File("database.key-name");

    /// <summary>
    /// Creates the object under test.
    /// </summary>
    /// <param name="store">The secret store.</param>
    /// <returns>The provider.</returns>
    private DatabaseKeyProvider Create(ISecretStore store)
    {
        return new DatabaseKeyProvider(store, NameFile);
    }

    /// <summary>
    /// A secret store that accepts writes and keeps nothing.
    /// </summary>
    private sealed class DroppingSecretStore : ISecretStore
    {
        /// <inheritdoc />
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        /// <inheritdoc />
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Verifies a new installation has no key.
    /// </summary>
    [Fact]
    public async Task ReadAsync_NothingStored_ReturnsNull()
    {
        (await Create(new InMemorySecretStore()).ReadAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    /// <summary>
    /// Verifies a new key has 32 bytes written as hexadecimal and is found again through the name file.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewKey_StoresSixtyFourHexCharacters()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemorySecretStore();

        var key = await Create(store).CreateAsync(ct);

        key.Should().MatchRegex("^[0-9A-F]{64}$");
        var name = (await File.ReadAllLinesAsync(NameFile, ct)).Should().ContainSingle().Subject;
        name.Should().StartWith("database:key:");
        store.Values[name].Should().Be(key);
        (await Create(store).ReadAsync(ct)).Should().Be(key);
    }

    /// <summary>
    /// Verifies a new key never replaces an earlier one, in the store or in the name file.
    /// </summary>
    [Fact]
    public async Task CreateAsync_Again_KeepsEarlierKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemorySecretStore();

        var first = await Create(store).CreateAsync(ct);
        var second = await Create(store).CreateAsync(ct);

        second.Should().NotBe(first);
        store.Values.Values.Should().BeEquivalentTo([first, second]);
        var names = await File.ReadAllLinesAsync(NameFile, ct);
        names.Should().HaveCount(2);
        store.Values[names[0]].Should().Be(first);
        (await Create(store).ReadAsync(ct)).Should().Be(second);
    }

    /// <summary>
    /// Verifies a damaged value is reported and left as it is.
    /// </summary>
    /// <param name="value">The stored value.</param>
    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("ZZ112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF")]
    public async Task ReadAsync_MalformedValue_Throws(string value)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemorySecretStore();
        await File.WriteAllTextAsync(NameFile, "database:key:test", ct);
        store.Values["database:key:test"] = value;

        var act = async () => await Create(store).ReadAsync(ct);

        await act.Should().ThrowAsync<InvalidDataException>();
        store.Values["database:key:test"].Should().Be(value);
    }

    /// <summary>
    /// Verifies a damaged name file is reported rather than read as "no key".
    /// </summary>
    /// <param name="content">The file content.</param>
    [Theory]
    [InlineData("")]
    [InlineData("account:1:oauth")]
    public async Task ReadAsync_MalformedNameFile_Throws(string content)
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(NameFile, content, ct);

        var act = async () => await Create(new InMemorySecretStore()).ReadAsync(ct);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    /// <summary>
    /// Verifies a key that the store did not keep is never handed out or recorded.
    /// </summary>
    [Fact]
    public async Task CreateAsync_StoreDropsValue_Throws()
    {
        var act = async () => await Create(new DroppingSecretStore()).CreateAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(NameFile).Should().BeFalse();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _directory.Dispose();
    }
}
