using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Application.Abstractions;
using Swiftlet.Infrastructure.Persistence;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="RemoteImageRuleStore"/> against an in-memory SQLite global store.
/// </summary>
public class RemoteImageRuleStoreTests
{
    /// <summary>
    /// A global store with the schema, and the means to create stores (as after an application restart) over it.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>
        /// The open connection that keeps the in-memory database alive.
        /// </summary>
        private readonly SqliteConnection _connection;

        /// <summary>
        /// The service provider that hands out scoped global contexts.
        /// </summary>
        private readonly ServiceProvider _provider;

        /// <summary>
        /// Initializes a new instance of the <see cref="Harness"/> class.
        /// </summary>
        /// <param name="connection">The open connection.</param>
        /// <param name="provider">The service provider.</param>
        private Harness(SqliteConnection connection, ServiceProvider provider)
        {
            _connection = connection;
            _provider = provider;
        }

        /// <summary>
        /// Creates the database with its schema.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The ready harness.</returns>
        public static async Task<Harness> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(cancellationToken);
            var services = new ServiceCollection();
            services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connection));
            var provider = services.BuildServiceProvider(validateScopes: true);
            await using (var scope = provider.CreateAsyncScope())
            {
                await SqliteSchemaInitializer.InitializeGlobalAsync(scope.ServiceProvider.GetRequiredService<GlobalDbContext>(), cancellationToken);
            }

            return new Harness(connection, provider);
        }

        /// <summary>
        /// Creates a store with an empty in-memory copy, like the one a restarted application starts with.
        /// </summary>
        /// <returns>The new store; its rules are not loaded yet.</returns>
        public RemoteImageRuleStore CreateStore()
        {
            return new RemoteImageRuleStore(_provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        }

        /// <summary>
        /// Runs a SQL statement that returns one integer.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The value.</returns>
        public async Task<int> ScalarAsync(string sql, CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<GlobalDbContext>();
#pragma warning disable EF1002
            return await context.Database.SqlQueryRaw<int>(sql).SingleAsync(cancellationToken);
#pragma warning restore EF1002
        }

        /// <summary>
        /// Runs a SQL statement without a result.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the statement ran.</returns>
        public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            await using var scope = _provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<GlobalDbContext>();
#pragma warning disable EF1002
            await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
#pragma warning restore EF1002
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies an added rule is saved in the database, normalized, and allows its sender.
    /// </summary>
    [Fact]
    public async Task AddAsync_NewRule_SavesNormalizedRuleAndAllowsSender()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();

        var added = await store.AddAsync("News@Shop.COM", ct);

        added.Should().BeTrue();
        store.IsAllowed("news@shop.com").Should().BeTrue();
        (await harness.ScalarAsync("SELECT COUNT(*) AS Value FROM RemoteImageRules WHERE Rule = 'news@shop.com' AND AddedAt > 0", ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies adding an existing rule changes nothing and reports it.
    /// </summary>
    [Fact]
    public async Task AddAsync_Duplicate_ReturnsFalseAndKeepsOneRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();
        await store.AddAsync("shop.com", ct);

        var added = await store.AddAsync("SHOP.com", ct);

        added.Should().BeFalse();
        (await harness.ScalarAsync("SELECT COUNT(*) AS Value FROM RemoteImageRules", ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies an invalid rule is rejected before anything is saved.
    /// </summary>
    [Fact]
    public async Task AddAsync_InvalidRule_ThrowsAndSavesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();

        var act = async () => await store.AddAsync("not a rule", ct);

        await act.Should().ThrowAsync<ArgumentException>();
        (await harness.ScalarAsync("SELECT COUNT(*) AS Value FROM RemoteImageRules", ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies an address rule matches only that exact sender.
    /// </summary>
    [Fact]
    public async Task IsAllowed_AddressRule_MatchesOnlyThatAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();
        await store.AddAsync("news@shop.com", ct);

        store.IsAllowed("news@shop.com").Should().BeTrue();
        store.IsAllowed("NEWS@Shop.com").Should().BeTrue();
        store.IsAllowed("other@shop.com").Should().BeFalse();
        store.IsAllowed("news@mail.shop.com").Should().BeFalse();
    }

    /// <summary>
    /// Verifies a domain rule matches the domain and its subdomains but not lookalike domains.
    /// </summary>
    [Fact]
    public async Task IsAllowed_DomainRule_MatchesDomainAndSubdomainsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();
        await store.AddAsync("shop.com", ct);

        store.IsAllowed("a@shop.com").Should().BeTrue();
        store.IsAllowed("a@mail.shop.com").Should().BeTrue();
        store.IsAllowed("a@deep.mail.shop.com").Should().BeTrue();
        store.IsAllowed("a@evilshop.com").Should().BeFalse();
        store.IsAllowed("a@shop.com.evil.net").Should().BeFalse();
        store.IsAllowed("a@other.org").Should().BeFalse();
    }

    /// <summary>
    /// Verifies unusable sender addresses are never allowed.
    /// </summary>
    /// <param name="address">The sender address.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown@invalid")]
    [InlineData("garbage")]
    public async Task IsAllowed_UnusableSender_ReturnsFalse(string? address)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();
        await store.AddAsync("shop.com", ct);

        store.IsAllowed(address).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a new store (a restart) loads the saved rules from the database.
    /// </summary>
    [Fact]
    public async Task LoadAsync_SavedRules_AreAllowedAfterRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using (var first = harness.CreateStore())
        {
            await first.AddAsync("news@shop.com", ct);
            await first.AddAsync("promo.example.org", ct);
        }

        using var restarted = harness.CreateStore();
        restarted.IsAllowed("news@shop.com").Should().BeFalse("nothing is loaded yet");
        await restarted.LoadAsync(ct);

        restarted.IsAllowed("news@shop.com").Should().BeTrue();
        restarted.IsAllowed("x@promo.example.org").Should().BeTrue();
        restarted.Search(null, 10).TotalMatches.Should().Be(2);
    }

    /// <summary>
    /// Verifies removing a rule deletes it from the database and from memory.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ExistingRule_RemovesItEverywhere()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();
        await store.AddAsync("shop.com", ct);
        await store.AddAsync("other.org", ct);

        var removed = await store.RemoveAsync("Shop.com", ct);

        removed.Should().BeTrue();
        store.IsAllowed("a@shop.com").Should().BeFalse();
        store.IsAllowed("a@other.org").Should().BeTrue();
        (await harness.ScalarAsync("SELECT COUNT(*) AS Value FROM RemoteImageRules", ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies removing an unknown rule has no effect.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_UnknownRule_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();
        await store.AddAsync("shop.com", ct);

        var removed = await store.RemoveAsync("other.org", ct);

        removed.Should().BeFalse();
        store.IsAllowed("a@shop.com").Should().BeTrue();
    }

    /// <summary>
    /// Verifies a failed database write leaves the in-memory rules unchanged.
    /// </summary>
    [Fact]
    public async Task AddAsync_DatabaseFails_RuleIsNotAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();
        await harness.ExecuteAsync("DROP TABLE RemoteImageRules", ct);

        var act = async () => await store.AddAsync("shop.com", ct);

        await act.Should().ThrowAsync<Exception>();
        store.IsAllowed("a@shop.com").Should().BeFalse();
        store.Search(null, 10).TotalMatches.Should().Be(0);
    }

    /// <summary>
    /// Verifies the same rule added concurrently is saved once and reported as added exactly once.
    /// </summary>
    [Fact]
    public async Task AddAsync_ConcurrentSameRule_AddsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.AddAsync("shop.com", ct)));

        results.Count(r => r).Should().Be(1);
        (await harness.ScalarAsync("SELECT COUNT(*) AS Value FROM RemoteImageRules", ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies search filters case-insensitively, sorts alphabetically and caps the result while counting all matches.
    /// </summary>
    [Fact]
    public async Task Search_FilterAndTake_ReturnsSortedPageAndTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        using var store = harness.CreateStore();
        foreach (var rule in new[] { "zeta.com", "alpha.com", "News@Shop.com", "beta.org", "shop.net" })
        {
            await store.AddAsync(rule, ct);
        }

        var all = store.Search(null, 3);
        var shops = store.Search("SHOP", 10);
        var none = store.Search("nothing", 10);

        all.Rules.Should().Equal("alpha.com", "beta.org", "news@shop.com");
        all.TotalMatches.Should().Be(5);
        shops.Rules.Should().Equal("news@shop.com", "shop.net");
        shops.TotalMatches.Should().Be(2);
        none.Rules.Should().BeEmpty();
        none.TotalMatches.Should().Be(0);
    }
}
