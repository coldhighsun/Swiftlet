using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DatabaseKeyProvider"/>.
/// </summary>
public sealed class DatabaseKeyProviderTests
{
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
    /// Verifies an empty store has no key.
    /// </summary>
    [Fact]
    public async Task ReadAsync_NothingStored_ReturnsNull()
    {
        var provider = new DatabaseKeyProvider(new InMemorySecretStore());

        (await provider.ReadAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    /// <summary>
    /// Verifies a new key has 32 bytes written as hexadecimal under the fixed name.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewKey_StoresSixtyFourHexCharacters()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemorySecretStore();
        var provider = new DatabaseKeyProvider(store);

        var key = await provider.CreateAsync(ct);

        key.Should().MatchRegex("^[0-9A-F]{64}$");
        store.Values["database:key"].Should().Be(key);
        (await provider.ReadAsync(ct)).Should().Be(key);
    }

    /// <summary>
    /// Verifies keys are random.
    /// </summary>
    [Fact]
    public async Task CreateAsync_Twice_GeneratesDifferentKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new DatabaseKeyProvider(new InMemorySecretStore());

        var first = await provider.CreateAsync(ct);
        var second = await provider.CreateAsync(ct);

        second.Should().NotBe(first);
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
        var store = new InMemorySecretStore();
        store.Values[DatabaseKeyProvider.SecretKey] = value;

        var act = async () => await new DatabaseKeyProvider(store).ReadAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
        store.Values[DatabaseKeyProvider.SecretKey].Should().Be(value);
    }

    /// <summary>
    /// Verifies a key that the store did not keep is never handed out.
    /// </summary>
    [Fact]
    public async Task CreateAsync_StoreDropsValue_Throws()
    {
        var provider = new DatabaseKeyProvider(new DroppingSecretStore());

        var act = async () => await provider.CreateAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
