using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;
using Swiftlet.Infrastructure.Sync;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AccountDatabaseMaintenance"/>, the update trigger and <see cref="DatabaseMaintenanceWorker"/>
/// against real database files.
/// </summary>
public sealed class DatabaseMaintenanceTests : IAsyncDisposable
{
    /// <summary>
    /// The update trigger as earlier versions created it.
    /// </summary>
    private const string OldTrigger =
        """
        CREATE TRIGGER Messages_au AFTER UPDATE ON Messages BEGIN
            INSERT INTO MessageSearch(MessageSearch, rowid, Subject, FromName, FromAddress, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.BodyText);
            INSERT INTO MessageSearch(rowid, Subject, FromName, FromAddress, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.BodyText);
        END;
        """;

    /// <summary>
    /// The temporary directory holding the account databases of one test.
    /// </summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}", "accounts");

    /// <summary>
    /// The factory of the account database files.
    /// </summary>
    private readonly AccountDbContextFactory _factory;

    /// <summary>
    /// The account used by the tests.
    /// </summary>
    private readonly MailAccount _account = TestData.Account("me@example.com");

    /// <summary>
    /// Maintenance with limits small enough for test databases.
    /// </summary>
    private readonly AccountDatabaseMaintenance _maintenance = new() { MinFreeBytes = 64 * 1024, HtmlBatchSize = 2 };

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseMaintenanceTests"/> class.
    /// </summary>
    public DatabaseMaintenanceTests()
    {
        _factory = new AccountDbContextFactory(_directory);
    }

    /// <summary>
    /// Verifies a new database has the restricted trigger and owes no maintenance.
    /// </summary>
    [Fact]
    public async Task Initialize_NewDatabase_HasRestrictedTriggerAndNoPendingTasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await _factory.CreateAsync(_account.Id, ct);

        (await TriggerSqlAsync(context, ct)).Should().Contain("AFTER UPDATE OF Subject, FromName, FromAddress, BodyText");
        (await TasksAsync(context, ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies the trigger of an earlier version is replaced once, recording the pending maintenance.
    /// </summary>
    [Fact]
    public async Task Initialize_OldTrigger_ReplacesItAndRecordsPendingTasks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await _factory.CreateAsync(_account.Id, ct);
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER Messages_au;", ct);
        await context.Database.ExecuteSqlRawAsync(OldTrigger, ct);

        await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);
        var upgraded = await TasksAsync(context, ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA user_version = 0;", ct);
        await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);

        (await TriggerSqlAsync(context, ct)).Should().Contain("AFTER UPDATE OF ");
        upgraded.Should().Be((int)(AccountDatabaseTasks.CompressHtml | AccountDatabaseTasks.Compact));
        (await TasksAsync(context, ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies changing the read and starred state leaves the full-text index untouched.
    /// </summary>
    [Fact]
    public async Task Update_FlagsOnly_DoesNotTouchSearchIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(3, ct);
        await using var context = await _factory.CreateAsync(_account.Id, ct);
        var before = await IndexContentAsync(context, ct);

        await context.Messages.ExecuteUpdateAsync(
            s => s.SetProperty(m => m.IsRead, true).SetProperty(m => m.IsFlagged, true),
            ct);

        (await IndexContentAsync(context, ct)).Should().Be(before);
    }

    /// <summary>
    /// Verifies changing an indexed column still updates the index.
    /// </summary>
    [Fact]
    public async Task Update_Subject_ReindexesMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(1, ct);
        await using var context = await _factory.CreateAsync(_account.Id, ct);

        await context.Database.ExecuteSqlRawAsync("UPDATE Messages SET Subject = 'quarterly report'", ct);

        (await SearchAsync("quarterly", ct)).Should().ContainSingle();
        (await SearchAsync("subject0000", ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a database with little free space is left alone.
    /// </summary>
    [Fact]
    public async Task Compact_LittleFreeSpace_DoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(5, ct);
        await using var context = await _factory.CreateAsync(_account.Id, ct);

        var result = await _maintenance.CompactIfNeededAsync(context, ct);

        result.Should().BeNull();
    }

    /// <summary>
    /// Verifies free space below the default 10 MB does not trigger compacting, however large its share.
    /// </summary>
    [Fact]
    public async Task Compact_FreeSpaceBelowByteLimit_DoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(200, ct);
        await DeleteEveryOtherAndFirstHalfAsync(ct);
        await using var context = await _factory.CreateAsync(_account.Id, ct);

        var result = await new AccountDatabaseMaintenance().CompactIfNeededAsync(context, ct);

        result.Should().BeNull();
    }

    /// <summary>
    /// Verifies a database with much free space shrinks, and search still returns the right messages although the
    /// deletions left gaps in the row numbers.
    /// </summary>
    [Fact]
    public async Task Compact_MuchFreeSpace_ShrinksFileAndKeepsSearchCorrect()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(200, ct);
        var kept = await DeleteEveryOtherAndFirstHalfAsync(ct);
        var path = Path.Combine(_directory, $"{_account.Id:D}.db");
        CompactionResult? result;
        await using (var context = await _factory.CreateAsync(_account.Id, ct))
        {
            result = await _maintenance.CompactIfNeededAsync(context, ct);

            (await context.Database.SqlQueryRaw<int>("SELECT freelist_count AS Value FROM pragma_freelist_count").SingleAsync(ct))
                .Should().Be(0);
        }

        result.Should().NotBeNull();
        result!.BytesAfter.Should().BeLessThan(result.BytesBefore / 2);
        new FileInfo(path).Length.Should().Be(result.BytesAfter);
        foreach (var i in new[] { kept[0], kept[^1] })
        {
            (await SearchAsync($"subject{i:D4}", ct)).Should().ContainSingle().Which.Uid.Should().Be((uint)i + 1);
            (await SearchAsync($"body{i:D4}", ct)).Should().ContainSingle().Which.Uid.Should().Be((uint)i + 1);
        }

        (await SearchAsync("subject0000", ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a pending compaction runs regardless of the free space and is then cleared.
    /// </summary>
    [Fact]
    public async Task Compact_Pending_RunsAndClearsTask()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(3, ct);
        await using var context = await _factory.CreateAsync(_account.Id, ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA user_version = 2;", ct);

        var result = await _maintenance.CompactIfNeededAsync(context, ct);

        result.Should().NotBeNull();
        result!.IndexRepaired.Should().BeFalse();
        (await TasksAsync(context, ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies a cancelled run changes nothing and leaves the work pending.
    /// </summary>
    [Fact]
    public async Task Compact_Cancelled_ThrowsAndKeepsTask()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await _factory.CreateAsync(_account.Id, ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA user_version = 2;", ct);

        var act = async () => await _maintenance.CompactIfNeededAsync(context, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await TasksAsync(context, ct)).Should().Be(2);
    }

    /// <summary>
    /// Verifies an index that lost its mapping because the rows were renumbered is detected and rebuilt.
    /// </summary>
    [Fact]
    public async Task VerifySearchIndex_RenumberedRows_RebuildsIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(3, ct);
        await using var context = await _factory.CreateAsync(_account.Id, ct);
        await context.Database.ExecuteSqlRawAsync("UPDATE Messages SET rowid = rowid + 1000", ct);
        (await SearchAsync("subject0001", ct)).Should().BeEmpty();
        await context.Database.OpenConnectionAsync(ct);
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var repaired = AccountDatabaseMaintenance.VerifySearchIndex(connection, ct);
        var repairedAgain = AccountDatabaseMaintenance.VerifySearchIndex(connection, ct);

        repaired.Should().BeTrue();
        repairedAgain.Should().BeFalse();
        (await SearchAsync("subject0001", ct)).Should().ContainSingle().Which.Uid.Should().Be(2u);
    }

    /// <summary>
    /// Verifies the plain-text bodies are compressed batch by batch without changing them, and the task is then cleared.
    /// </summary>
    [Fact]
    public async Task CompressHtml_PendingPlainRows_CompressesAllAndClearsTask()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(5, ct);
        var html = string.Concat(Enumerable.Repeat("<p>周报 weekly</p>", 100));
        await using var context = await _factory.CreateAsync(_account.Id, ct);
        await context.Database.ExecuteSqlAsync($"UPDATE Messages SET BodyHtml = {html}", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA user_version = 3;", ct);

        var batches = 0;
        long? row = 0;
        while (row is { } after)
        {
            row = await _maintenance.CompressHtmlBatchAsync(context, after, ct);
            batches++;
        }

        batches.Should().Be(4);
        var types = await context.Database.SqlQueryRaw<string>("SELECT typeof(BodyHtml) AS Value FROM Messages").ToListAsync(ct);
        types.Should().HaveCount(5).And.OnlyContain(t => t == "blob");
        var bodies = await context.Messages.AsNoTracking().Select(m => m.BodyHtml).ToListAsync(ct);
        bodies.Should().OnlyContain(b => b == html);
        (await TasksAsync(context, ct)).Should().Be((int)AccountDatabaseTasks.Compact);
    }

    /// <summary>
    /// Verifies nothing is rewritten when compression is not pending.
    /// </summary>
    [Fact]
    public async Task CompressHtml_NotPending_LeavesRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(1, ct);
        await using var context = await _factory.CreateAsync(_account.Id, ct);
        await context.Database.ExecuteSqlRawAsync("UPDATE Messages SET BodyHtml = 'plain'", ct);

        var next = await _maintenance.CompressHtmlBatchAsync(context, 0, ct);

        next.Should().BeNull();
        (await context.Database.SqlQueryRaw<string>("SELECT typeof(BodyHtml) AS Value FROM Messages").SingleAsync(ct))
            .Should().Be("text");
    }

    /// <summary>
    /// Verifies the worker waits after startup, then carries out the pending work of every account.
    /// </summary>
    [Fact]
    public async Task Worker_AfterInitialDelay_MaintainsPendingDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(3, ct);
        await using (var context = await _factory.CreateAsync(_account.Id, ct))
        {
            await context.Database.ExecuteSqlRawAsync("UPDATE Messages SET BodyHtml = 'plain'", ct);
            await context.Database.ExecuteSqlRawAsync("PRAGMA user_version = 3;", ct);
        }

        var time = new VirtualTimeProvider();
        await using var provider = await BuildProviderAsync(time, ct);
        var worker = provider.GetRequiredService<DatabaseMaintenanceWorker>();
        await worker.StartAsync(ct);
        try
        {
            await WatcherHarness.WaitUntilAsync(() => time.PendingTimerCount == 1, ct);
            await using var context = await _factory.CreateAsync(_account.Id, ct);
            (await TasksAsync(context, ct)).Should().Be(3);

            time.Advance(DatabaseMaintenanceWorker.InitialDelay);
            await WatcherHarness.WaitUntilAsync(() => time.PendingTimerCount == 1, ct);

            (await TasksAsync(context, ct)).Should().Be(0);
        }
        finally
        {
            await worker.StopAsync(ct);
        }
    }

    /// <summary>
    /// Verifies no database file is created for an account that no longer exists.
    /// </summary>
    [Fact]
    public async Task Worker_RemovedAccount_IsNotOpened()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = await BuildProviderAsync(new VirtualTimeProvider(), ct);
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<GlobalDbContext>().Accounts.ExecuteDeleteAsync(ct);
        }

        await provider.GetRequiredService<DatabaseMaintenanceWorker>().RunPassAsync(ct);

        Directory.Exists(_directory).Should().BeFalse();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        var root = Path.GetDirectoryName(_directory);
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads the definition of the update trigger.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The SQL.</returns>
    private static async Task<string> TriggerSqlAsync(AccountDbContext context, CancellationToken cancellationToken)
    {
        return await context.Database
            .SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE name = 'Messages_au'")
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the pending maintenance tasks.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stored value.</returns>
    private static async Task<int> TasksAsync(AccountDbContext context, CancellationToken cancellationToken)
    {
        return await context.Database
            .SqlQueryRaw<int>("SELECT user_version AS Value FROM pragma_user_version")
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the stored blocks of the full-text index as one string.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The blocks in hexadecimal.</returns>
    private static async Task<string> IndexContentAsync(AccountDbContext context, CancellationToken cancellationToken)
    {
        return await context.Database
            .SqlQueryRaw<string>("SELECT group_concat(id || ':' || hex(block)) AS Value FROM MessageSearch_data")
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Adds messages whose subject and body carry their zero-based number; the UID is the number plus one.
    /// </summary>
    /// <param name="count">The number of messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when they are saved.</returns>
    private async Task SeedAsync(int count, CancellationToken cancellationToken)
    {
        await using var context = await _factory.CreateAsync(_account.Id, cancellationToken);
        var folder = MailFolder.Create(_account.Id, "INBOX", "Inbox", FolderRole.Inbox);
        context.Folders.Add(folder);
        context.Messages.AddRange(Enumerable.Range(0, count).Select(i => MailMessage.Create(
            _account.Id,
            folder.Id,
            (uint)i + 1,
            $"subject{i:D4}",
            "A",
            "a@example.com",
            "r",
            DateTimeOffset.UnixEpoch.AddMinutes(i),
            $"body{i:D4} {string.Concat(Enumerable.Range(0, 300).Select(n => $"w{i}x{n} "))}")));
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the first half of the messages and every other one of the rest, leaving free pages and row number gaps.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The numbers of the remaining messages.</returns>
    private async Task<List<int>> DeleteEveryOtherAndFirstHalfAsync(CancellationToken cancellationToken)
    {
        await using var context = await _factory.CreateAsync(_account.Id, cancellationToken);
        await context.Messages.Where(m => m.Uid <= 100 || m.Uid % 2 == 0).ExecuteDeleteAsync(cancellationToken);
        return await context.Messages.OrderBy(m => m.Uid).Select(m => (int)m.Uid - 1).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Searches the account through the repository.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matches.</returns>
    private async Task<IReadOnlyList<MailMessage>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        await using var global = await TestDatabase.CreateAsync(cancellationToken);
        await using var contexts = new AccountContextProvider(_factory);
        return await new MessageRepository(global.Context, contexts).SearchAsync(query, _account.Id, 0, 10, cancellationToken);
    }

    /// <summary>
    /// Builds the services of the worker over the test files, with the account registered.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The provider.</returns>
    private async Task<ServiceProvider> BuildProviderAsync(VirtualTimeProvider time, CancellationToken cancellationToken)
    {
        // A named shared-cache database lives as long as a connection to it is open, here the one of the singleton below.
        var source = $"Data Source=global-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var services = new ServiceCollection();
        services.AddSingleton(_ =>
        {
            var connection = new SqliteConnection(source);
            connection.Open();
            return connection;
        });
        services.AddDbContext<GlobalDbContext>((sp, o) =>
        {
            sp.GetRequiredService<SqliteConnection>();
            o.UseSqlite(source);
        });
        services.AddSingleton<IAccountDbContextFactory>(_factory);
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddSingleton<SyncCoordinator>();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(_maintenance);
        services.AddSingleton(sp => new DatabaseMaintenanceWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            _factory,
            _maintenance,
            sp.GetRequiredService<SyncCoordinator>(),
            time,
            NullLogger<DatabaseMaintenanceWorker>.Instance));
        var provider = services.BuildServiceProvider(validateScopes: true);

        await using var scope = provider.CreateAsyncScope();
        var global = scope.ServiceProvider.GetRequiredService<GlobalDbContext>();
        await SqliteSchemaInitializer.InitializeGlobalAsync(global, cancellationToken);
        global.Accounts.Add(_account);
        await global.SaveChangesAsync(cancellationToken);
        return provider;
    }
}
