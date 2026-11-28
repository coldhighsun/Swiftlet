using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Messages;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the automatic cleanup of the Trash folder.
/// </summary>
public sealed class TrashCleanupServiceTests
{
    [Fact]
    public async Task ProcessBatch_FirstLook_DeletesNothingAndStoresTheCheckpoint()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.PutAsync(10, ct);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().BeNull();
        s.Server.DeleteCalls.Should().BeEmpty();
        await using var verifier = s.Db.CreateVerifier();
        var folders = await verifier.Folders().GetByAccountAsync(s.Account.Id, ct);
        folders.Single(f => f.Role == FolderRole.Trash).TrashCheckpoints
            .Should().Equal(new UidCheckpoint(10, s.Clock.Now));
    }

    [Fact]
    public async Task ProcessBatch_RetentionPassed_DeletesExpiredMessagesPermanently()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var old = await s.PutAsync(10, ct);

        // A message the server holds but that was never downloaded.
        s.Server.Messages["Trash"].Add(TestData.Remote(4));
        await s.ProcessAsync(ct);
        s.Clock.Now = s.Clock.Now.AddDays(7);
        var fresh = await s.PutAsync(11, ct);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().Be(new TrashCleanupResult("Trash", 2, 0));
        var call = s.Server.DeleteCalls.Should().ContainSingle().Which;
        call.Folder.Should().Be("Trash");
        call.Uids.Should().Equal(4u, 10u);
        call.Destination.Should().BeNull();
        (await s.FindAsync(old.Id, ct)).Should().BeNull();
        (await s.FindAsync(fresh.Id, ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessBatch_BeforeTheRetention_DeletesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.PutAsync(10, ct);
        await s.ProcessAsync(ct);
        s.Clock.Now = s.Clock.Now.AddDays(6);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().BeNull();
        s.Server.DeleteCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessBatch_MessageAwaitingDeletionOrMove_IsLeftAlone(bool move)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var leaving = await s.PutAsync(10, ct);
        await s.PutAsync(11, ct);
        await s.ProcessAsync(ct);
        s.Clock.Now = s.Clock.Now.AddDays(7);
        if (move)
        {
            await s.Messages.MarkMovePendingAsync(s.Account.Id, [leaving.Id], s.Inbox.Id, ct);
        }
        else
        {
            await s.Messages.MarkDeletePendingAsync(s.Account.Id, [leaving.Id], ct);
        }

        // Act
        await s.ProcessAsync(ct);

        // Assert
        s.Server.DeleteCalls.Should().ContainSingle().Which.Uids.Should().Equal(11u);
        (await s.FindAsync(leaving.Id, ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessBatch_ManyExpired_WorksInBatches()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Server.Messages["Trash"] = [.. Enumerable.Range(1, 150).Select(i => TestData.Remote((uint)i))];
        await s.ProcessAsync(ct);
        s.Clock.Now = s.Clock.Now.AddDays(7);

        // Act
        var first = await s.ProcessAsync(ct);
        var second = await s.ProcessAsync(ct);
        var third = await s.ProcessAsync(ct);

        // Assert
        first.Should().Be(new TrashCleanupResult("Trash", 100, 50));
        second.Should().Be(new TrashCleanupResult("Trash", 50, 0));
        third.Should().BeNull();
    }

    [Fact]
    public async Task ProcessBatch_ServerFails_KeepsTheCacheAndTriesAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var message = await s.PutAsync(10, ct);
        await s.ProcessAsync(ct);
        s.Clock.Now = s.Clock.Now.AddDays(7);
        s.Server.DeleteFailures.Enqueue(new IOException("connection lost"));

        // Act
        var failed = () => s.ProcessAsync(ct);
        await failed.Should().ThrowAsync<IOException>();
        var kept = await s.FindAsync(message.Id, ct);
        var retried = await s.ProcessAsync(ct);

        // Assert
        kept.Should().NotBeNull();
        retried!.Deleted.Should().Be(1);
    }

    [Fact]
    public async Task ProcessBatch_UidValidityChangedOnServer_DoesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.PutAsync(10, ct);
        await s.ProcessAsync(ct);
        s.Clock.Now = s.Clock.Now.AddDays(7);
        s.Server.UidValidity["Trash"] = 2;

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().BeNull();
        s.Server.DeleteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessBatch_TrashNeverSynchronized_DoesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct, synchronized: false);
        s.Server.Messages["Trash"] = [TestData.Remote(10)];

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().BeNull();
        s.Server.RangeCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessBatch_NoTrashFolder_DoesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct, withTrash: false);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().BeNull();
        s.Server.RangeCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessBatch_CleanupOff_DoesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Account.ChangeTrashRetention(null, s.Clock.Now);
        await s.PutAsync(10, ct);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().BeNull();
        s.Server.RangeCalls.Should().BeEmpty();
    }

    /// <summary>
    /// An account that keeps deleted mail for a week, with its repositories and a scripted server.
    /// </summary>
    private sealed class Scenario : IAsyncDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Scenario"/> class.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="account">The account.</param>
        /// <param name="inbox">The INBOX.</param>
        private Scenario(TestDatabase db, MailAccount account, MailFolder inbox)
        {
            Db = db;
            Account = account;
            Inbox = inbox;
            Messages = db.CreateMessageRepository();
            Folders = db.CreateFolderRepository();
            Service = new TrashCleanupService(Folders, Messages, Clock);
        }

        /// <summary>
        /// Gets the database.
        /// </summary>
        public TestDatabase Db { get; }

        /// <summary>
        /// Gets the account.
        /// </summary>
        public MailAccount Account { get; }

        /// <summary>
        /// Gets the INBOX.
        /// </summary>
        public MailFolder Inbox { get; }

        /// <summary>
        /// Gets the Trash, if the scenario has one.
        /// </summary>
        public MailFolder? Trash { get; private set; }

        /// <summary>
        /// Gets the message repository.
        /// </summary>
        public MessageRepository Messages { get; }

        /// <summary>
        /// Gets the folder repository.
        /// </summary>
        public FolderRepository Folders { get; }

        /// <summary>
        /// Gets the clock.
        /// </summary>
        public ManualTimeProvider Clock { get; } = new();

        /// <summary>
        /// Gets the service under test.
        /// </summary>
        public TrashCleanupService Service { get; }

        /// <summary>
        /// Gets the scripted server.
        /// </summary>
        public FakeImapSession Server { get; } = new();

        /// <summary>
        /// Creates a scenario.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="withTrash">Whether the account has a Trash.</param>
        /// <param name="synchronized">Whether the Trash was synchronized before.</param>
        /// <returns>The scenario.</returns>
        public static async Task<Scenario> CreateAsync(
            CancellationToken cancellationToken,
            bool withTrash = true,
            bool synchronized = true)
        {
            var db = await TestDatabase.CreateAsync(cancellationToken);
            var (account, inbox) = await db.SeedAccountAsync(cancellationToken);
            var scenario = new Scenario(db, account, inbox);
            account.ChangeTrashRetention(7, scenario.Clock.Now);
            if (withTrash)
            {
                var trash = MailFolder.Create(account.Id, "Trash", "Trash", FolderRole.Trash);
                if (synchronized)
                {
                    trash.UpdateUidValidity(1);
                }

                await scenario.Folders.AddAsync(trash, cancellationToken);
                await scenario.Folders.SaveChangesAsync(cancellationToken);
                scenario.Trash = trash;
            }

            return scenario;
        }

        /// <summary>
        /// Puts a message into the Trash on the server and in the cache.
        /// </summary>
        /// <param name="uid">The UID.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The cached message.</returns>
        public async Task<MailMessage> PutAsync(uint uid, CancellationToken cancellationToken)
        {
            var message = MailMessage.Create(
                Account.Id, Trash!.Id, uid, "s", "A", "a@example.com", "r", DateTimeOffset.UnixEpoch.AddMinutes(uid), "b", null, null);
            await Messages.AddAsync(message, cancellationToken);
            if (!Server.Messages.TryGetValue("Trash", out var list))
            {
                list = [];
                Server.Messages["Trash"] = list;
            }

            list.Add(TestData.Remote(uid));
            return message;
        }

        /// <summary>
        /// Runs one cleanup batch.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The result.</returns>
        public Task<TrashCleanupResult?> ProcessAsync(CancellationToken cancellationToken)
        {
            return Service.ProcessBatchAsync(Server, Account, cancellationToken);
        }

        /// <summary>
        /// Looks a message up with fresh contexts.
        /// </summary>
        /// <param name="id">The message identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The stored message, if any.</returns>
        public async Task<MailMessage?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            await using var verifier = Db.CreateVerifier();
            return await verifier.Messages().GetAsync(Account.Id, id, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return Db.DisposeAsync();
        }
    }
}
