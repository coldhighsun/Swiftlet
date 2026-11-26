using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;
using Swiftlet.Infrastructure.Security;
using Swiftlet.Infrastructure.Sync;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DataProtectionSecretStore"/> and <see cref="AccountSyncWorker"/>.
/// </summary>
public class SecretStoreAndWorkerTests
{
    /// <summary>
    /// Verifies a secret round-trips, is not stored in clear text, and survives a new store instance.
    /// </summary>
    [Fact]
    public async Task SetAsync_Secret_RoundTripsEncryptedAndPersistsAcrossInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");
        try
        {
            using (var store = new DataProtectionSecretStore(directory))
            {
                await store.SetAsync("account:1:password", "s3cret-value", ct);
                (await store.GetAsync("account:1:password", ct)).Should().Be("s3cret-value");
            }

            var files = Directory.GetFiles(Path.Combine(directory, "secrets"));
            files.Should().ContainSingle();
            (await File.ReadAllTextAsync(files[0], ct)).Should().NotContain("s3cret-value");

            using var reopened = new DataProtectionSecretStore(directory);
            (await reopened.GetAsync("account:1:password", ct)).Should().Be("s3cret-value");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies missing secrets read as null and removal deletes a secret.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ExistingSecret_MakesItUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");
        try
        {
            using var store = new DataProtectionSecretStore(directory);
            (await store.GetAsync("missing", ct)).Should().BeNull();
            await store.SetAsync("k", "v", ct);

            await store.RemoveAsync("k", ct);

            (await store.GetAsync("k", ct)).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies the worker syncs due accounts through Mediator and respects each account's interval.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_DueAccounts_SyncsThroughMediatorOncePerInterval()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1)];
        var clock = new ManualTimeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IImapSessionFactory>(factory);
        services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<InMemoryAccountDbContextFactory>();
        services.AddSingleton<IAccountDbContextFactory>(sp => sp.GetRequiredService<InMemoryAccountDbContextFactory>());
        services.AddScoped<AccountContextProvider>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        Guid accountId;
        await using (var setup = provider.CreateAsyncScope())
        {
            var context = setup.ServiceProvider.GetRequiredService<GlobalDbContext>();
            await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);
            var account = TestData.Account("me@example.com");
            account.ChangeSyncInterval(TimeSpan.FromMinutes(10));
            await setup.ServiceProvider.GetRequiredService<IAccountRepository>().AddAsync(account, ct);
            accountId = account.Id;
        }

        var worker = new AccountSyncWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<SyncSchedule>(),
            clock,
            NullLogger<AccountSyncWorker>.Instance);

        await worker.RunOnceAsync(ct);
        clock.Now = clock.Now.AddMinutes(9);
        await worker.RunOnceAsync(ct);
        factory.ConnectCount.Should().Be(1);

        clock.Now = clock.Now.AddMinutes(1);
        await worker.RunOnceAsync(ct);
        factory.ConnectCount.Should().Be(2);

        await using var verify = provider.CreateAsyncScope();
        var folders = await verify.ServiceProvider.GetRequiredService<IFolderRepository>().GetByAccountAsync(accountId, ct);
        var cached = await verify.ServiceProvider.GetRequiredService<IMessageRepository>().GetByFolderAsync(accountId, folders.Single().Id, 0, 10, ct);
        cached.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies a failing account does not stop the worker and is not retried before its interval elapses.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_SyncFails_DoesNotThrowAndBacksOff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        var factory = new FakeImapSessionFactory { Failure = new IOException("network down") };
        var clock = new ManualTimeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IImapSessionFactory>(factory);
        services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<InMemoryAccountDbContextFactory>();
        services.AddSingleton<IAccountDbContextFactory>(sp => sp.GetRequiredService<InMemoryAccountDbContextFactory>());
        services.AddScoped<AccountContextProvider>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using (var setup = provider.CreateAsyncScope())
        {
            await SqliteSchemaInitializer.InitializeGlobalAsync(setup.ServiceProvider.GetRequiredService<GlobalDbContext>(), ct);
            await setup.ServiceProvider.GetRequiredService<IAccountRepository>().AddAsync(TestData.Account("me@example.com"), ct);
        }

        var worker = new AccountSyncWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<SyncSchedule>(),
            clock,
            NullLogger<AccountSyncWorker>.Instance);

        await worker.RunOnceAsync(ct);
        await worker.RunOnceAsync(ct);

        factory.ConnectCount.Should().Be(1);
    }

    /// <summary>
    /// Verifies an account deleted mid-sync (stale insert) does not poison the sync of the next due account.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_FirstAccountDeletedMidSync_SecondAccountStillSyncs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1)];
        var clock = new ManualTimeProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IImapSessionFactory>(factory);
        services.AddDbContext<GlobalDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<InMemoryAccountDbContextFactory>();
        services.AddSingleton<IAccountDbContextFactory>(sp => sp.GetRequiredService<InMemoryAccountDbContextFactory>());
        services.AddScoped<AccountContextProvider>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IFolderRepository, FolderRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        Guid firstId;
        Guid secondId;
        await using (var setup = provider.CreateAsyncScope())
        {
            await SqliteSchemaInitializer.InitializeGlobalAsync(setup.ServiceProvider.GetRequiredService<GlobalDbContext>(), ct);
            var repository = setup.ServiceProvider.GetRequiredService<IAccountRepository>();
            await repository.AddAsync(TestData.Account("first@example.com"), ct);
            await repository.AddAsync(TestData.Account("second@example.com"), ct);
            var all = await repository.GetAllAsync(ct);
            firstId = all[0].Id;
            secondId = all[1].Id;
        }

        var deleted = false;
        factory.Session.OnListFolders = () =>
        {
            if (deleted)
            {
                return;
            }

            deleted = true;
            // The hook cannot be async, so block on the delete through a separate scope (its own DbContext).
            var other = provider.CreateAsyncScope();
            try
            {
                other.ServiceProvider.GetRequiredService<IAccountRepository>().DeleteAsync(firstId).GetAwaiter().GetResult();
            }
            finally
            {
                other.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        };
        var worker = new AccountSyncWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<SyncSchedule>(),
            clock,
            NullLogger<AccountSyncWorker>.Instance);

        await worker.RunOnceAsync(ct);

        deleted.Should().BeTrue();
        await using var verify = provider.CreateAsyncScope();
        var folders = await verify.ServiceProvider.GetRequiredService<IFolderRepository>().GetByAccountAsync(secondId, ct);
        folders.Should().ContainSingle();
        var cached = await verify.ServiceProvider.GetRequiredService<IMessageRepository>().GetByFolderAsync(secondId, folders[0].Id, 0, 10, ct);
        cached.Should().ContainSingle();
    }
}
