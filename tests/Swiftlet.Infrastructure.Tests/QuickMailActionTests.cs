using AwesomeAssertions;
using Swiftlet.Application.Messages;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="QuickMailActionCommand"/> and <see cref="GetQuickActionsQuery"/> against real SQLite repositories.
/// </summary>
public class QuickMailActionTests
{
    /// <summary>
    /// Verifies marking read and starring change only the flags and wake the flag push.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="flagged">Whether the message ends up starred.</param>
    [Theory]
    [InlineData(QuickMailAction.MarkRead, false)]
    [InlineData(QuickMailAction.Star, true)]
    public async Task Handle_FlagAction_MarksReadAndStaysInTheFolder(QuickMailAction action, bool flagged)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        var result = await s.ActAsync(action, ct);

        result.Should().Be(QuickMailActionResult.Done);
        var stored = (await s.FindAsync(ct))!;
        stored.IsRead.Should().BeTrue();
        stored.IsFlagged.Should().Be(flagged);
        stored.IsFlagSyncPending.Should().BeTrue();
        stored.IsDeletePending.Should().BeFalse();
        (await s.Flags.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct)).Should().Be(s.Account.Id);
    }

    /// <summary>
    /// Verifies archiving marks the message read and queues the move to the archive folder.
    /// </summary>
    [Fact]
    public async Task Handle_Archive_MarksReadAndQueuesTheMove()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var archive = await s.AddFolderAsync("Archive", FolderRole.Archive, ct);

        var result = await s.ActAsync(QuickMailAction.Archive, ct);

        result.Should().Be(QuickMailActionResult.Done);
        var stored = (await s.FindAsync(ct))!;
        stored.IsRead.Should().BeTrue();
        stored.IsDeletePending.Should().BeTrue();
        stored.PendingMoveFolderId.Should().Be(archive.Id);
        await s.Deletions.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// Verifies deleting marks the message read and queues the move to Trash.
    /// </summary>
    [Fact]
    public async Task Handle_Delete_MarksReadAndQueuesTheDeletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.AddFolderAsync("Trash", FolderRole.Trash, ct);

        var result = await s.ActAsync(QuickMailAction.Delete, ct);

        result.Should().Be(QuickMailActionResult.Done);
        var stored = (await s.FindAsync(ct))!;
        stored.IsRead.Should().BeTrue();
        stored.IsDeletePending.Should().BeTrue();
        stored.PendingMoveFolderId.Should().BeNull();
        await s.Deletions.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// Verifies an action the account cannot carry out changes nothing; in particular nothing is deleted for good.
    /// </summary>
    /// <param name="action">The action.</param>
    [Theory]
    [InlineData(QuickMailAction.Delete)]
    [InlineData(QuickMailAction.Archive)]
    public async Task Handle_NoTargetFolder_IsUnavailableAndChangesNothing(QuickMailAction action)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        var result = await s.ActAsync(action, ct);

        result.Should().Be(QuickMailActionResult.Unavailable);
        var stored = (await s.FindAsync(ct))!;
        stored.IsRead.Should().BeFalse();
        stored.IsDeletePending.Should().BeFalse();
        s.FlagPushRequested().Should().BeFalse();
    }

    /// <summary>
    /// Verifies a message that moved meanwhile is left alone, so a delete cannot remove it from Trash for good.
    /// </summary>
    [Fact]
    public async Task Handle_MessageMovedToTrashMeanwhile_IsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var trash = await s.AddFolderAsync("Trash", FolderRole.Trash, ct);
        await s.Messages.MoveToFolderAsync(s.Account.Id, trash.Id, [(s.Message.Id, 40u)], ct);

        await using var verifier = s.Db.CreateVerifier();
        var result = await s.ActAsync(QuickMailAction.Delete, ct, verifier.Messages());

        result.Should().Be(QuickMailActionResult.Gone);
        var stored = (await s.FindAsync(ct))!;
        stored.IsDeletePending.Should().BeFalse();
        stored.IsRead.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a message already queued for removal is left alone.
    /// </summary>
    [Fact]
    public async Task Handle_PendingMessage_IsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.Messages.MarkDeletePendingAsync(s.Account.Id, [s.Message.Id], ct);

        await using var verifier = s.Db.CreateVerifier();
        var result = await s.ActAsync(QuickMailAction.MarkRead, ct, verifier.Messages());

        result.Should().Be(QuickMailActionResult.Gone);
        s.FlagPushRequested().Should().BeFalse();
    }

    /// <summary>
    /// Verifies a removed message is reported as gone.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownMessage_IsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        var result = await s.Handler(s.Messages).Handle(
            new QuickMailActionCommand(s.Account.Id, Guid.NewGuid(), s.Inbox.Id, QuickMailAction.MarkRead), ct);

        result.Should().Be(QuickMailActionResult.Gone);
    }

    /// <summary>
    /// Verifies the query offers deleting and archiving only where the folders exist.
    /// </summary>
    [Fact]
    public async Task GetQuickActions_FoldersAppear_OffersTheActions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var handler = new GetQuickActionsQueryHandler(s.Db.CreateAccountRepository(), s.Folders);
        var query = new GetQuickActionsQuery(s.Account.Id, s.Inbox.Id);

        var before = await handler.Handle(query, ct);
        var trash = await s.AddFolderAsync("Trash", FolderRole.Trash, ct);
        await s.AddFolderAsync("All", FolderRole.AllMail, ct);
        var after = await handler.Handle(query, ct);
        var inTrash = await handler.Handle(new GetQuickActionsQuery(s.Account.Id, trash.Id), ct);

        before.Should().Be(new QuickActionAvailability(false, false));
        after.Should().Be(new QuickActionAvailability(true, true));
        inTrash.CanDelete.Should().BeFalse("deleting inside Trash is permanent");
    }

    /// <summary>
    /// An account with an INBOX holding one unread message.
    /// </summary>
    private sealed class Scenario : IAsyncDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Scenario"/> class.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="account">The account.</param>
        /// <param name="inbox">The INBOX.</param>
        /// <param name="message">The message.</param>
        private Scenario(TestDatabase db, MailAccount account, MailFolder inbox, MailMessage message)
        {
            Db = db;
            Account = account;
            Inbox = inbox;
            Message = message;
            Messages = db.CreateMessageRepository();
            Folders = db.CreateFolderRepository();
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
        /// Gets the unread message.
        /// </summary>
        public MailMessage Message { get; }

        /// <summary>
        /// Gets the message repository.
        /// </summary>
        public MessageRepository Messages { get; }

        /// <summary>
        /// Gets the folder repository.
        /// </summary>
        public FolderRepository Folders { get; }

        /// <summary>
        /// Gets the signal of the background deletion.
        /// </summary>
        public DeletionSignal Deletions { get; } = new();

        /// <summary>
        /// Gets the signal of the background flag push.
        /// </summary>
        public FlagPushSignal Flags { get; } = new();

        /// <summary>
        /// Creates a scenario.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scenario.</returns>
        public static async Task<Scenario> CreateAsync(CancellationToken cancellationToken)
        {
            var db = await TestDatabase.CreateAsync(cancellationToken);
            var (account, inbox) = await db.SeedAccountAsync(cancellationToken);
            var message = MailMessage.Create(
                account.Id, inbox.Id, 5, "s", "A", "a@example.com", "r", DateTimeOffset.UnixEpoch, "b", null, null);
            await db.CreateMessageRepository().AddAsync(message, cancellationToken);
            return new Scenario(db, account, inbox, message);
        }

        /// <summary>
        /// Adds a folder to the account.
        /// </summary>
        /// <param name="name">The folder name.</param>
        /// <param name="role">The role.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The folder.</returns>
        public async Task<MailFolder> AddFolderAsync(string name, FolderRole role, CancellationToken cancellationToken)
        {
            var folder = MailFolder.Create(Account.Id, name, name, role);
            await Folders.AddAsync(folder, cancellationToken);
            await Folders.SaveChangesAsync(cancellationToken);
            return folder;
        }

        /// <summary>
        /// Creates the handler under test.
        /// </summary>
        /// <param name="messages">The message repository it uses.</param>
        /// <returns>The handler.</returns>
        public QuickMailActionCommandHandler Handler(IMessageRepository messages)
        {
            return new QuickMailActionCommandHandler(Db.CreateAccountRepository(), Folders, messages, Deletions, Flags);
        }

        /// <summary>
        /// Applies an action to the message as it was announced.
        /// </summary>
        /// <param name="action">The action.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="messages">The message repository; the scenario's own when omitted.</param>
        /// <returns>The result.</returns>
        public async Task<QuickMailActionResult> ActAsync(
            QuickMailAction action,
            CancellationToken cancellationToken,
            IMessageRepository? messages = null)
        {
            return await Handler(messages ?? Messages).Handle(
                new QuickMailActionCommand(Account.Id, Message.Id, Inbox.Id, action), cancellationToken);
        }

        /// <summary>
        /// Determines whether the flag push was woken.
        /// </summary>
        /// <returns><see langword="true"/> if a request is waiting.</returns>
        public bool FlagPushRequested()
        {
            using var done = new CancellationTokenSource();
            var request = Flags.ReadAsync(done.Token);
            var requested = request.IsCompleted;
            done.Cancel();
            return requested;
        }

        /// <summary>
        /// Reads the message as stored, through a fresh context.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The message, or <see langword="null"/>.</returns>
        public async Task<MailMessage?> FindAsync(CancellationToken cancellationToken)
        {
            await using var verifier = Db.CreateVerifier();
            return await verifier.Messages().GetAsync(Account.Id, Message.Id, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return Db.DisposeAsync();
        }
    }
}
