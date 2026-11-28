using AwesomeAssertions;
using Swiftlet.Application.Messages;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for marking every downloaded message of a folder as read.
/// </summary>
public class MarkFolderReadTests
{
    /// <summary>
    /// Verifies only the visible unread messages of the folder change, and they are queued for the server.
    /// </summary>
    [Fact]
    public async Task Handle_UnreadMessages_MarksOnlyThatFolderAndQueuesPush()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var folders = db.CreateFolderRepository();
        var other = MailFolder.Create(account.Id, "Other", "Other", FolderRole.Other);
        await folders.AddAsync(other, ct);
        await folders.SaveChangesAsync(ct);
        var repository = db.CreateMessageRepository();
        var unread = MailMessage.Create(account.Id, folder.Id, 1, "s", "A", "a@example.com", "r", DateTimeOffset.UnixEpoch, "b");
        var read = MailMessage.Create(account.Id, folder.Id, 2, "s", "A", "a@example.com", "r", DateTimeOffset.UnixEpoch, "b", isRead: true);
        var deleting = MailMessage.Create(account.Id, folder.Id, 3, "s", "A", "a@example.com", "r", DateTimeOffset.UnixEpoch, "b");
        var elsewhere = MailMessage.Create(account.Id, other.Id, 1, "s", "A", "a@example.com", "r", DateTimeOffset.UnixEpoch, "b");
        foreach (var message in new[] { unread, read, deleting, elsewhere })
        {
            await repository.AddAsync(message, ct);
        }

        await repository.MarkDeletePendingAsync(account.Id, [deleting.Id], ct);
        var signal = new FlagPushSignal();

        var marked = await new MarkFolderReadCommandHandler(repository, signal)
            .Handle(new MarkFolderReadCommand(account.Id, folder.Id), ct);

        marked.Should().Be(1);
        (await signal.ReadAsync(ct)).Should().Be(account.Id);
        await using var verify = db.CreateVerifier();
        var states = await verify.Messages().GetFlagStatesAsync(account.Id, folder.Id, ct);
        states.Select(s => (s.Uid, s.IsRead, s.IsFlagSyncPending))
            .Should().Equal((1u, true, true), (2u, true, false), (3u, false, false));
        (await verify.Messages().GetFlagStatesAsync(account.Id, other.Id, ct)).Single().IsRead.Should().BeFalse();
    }

    /// <summary>
    /// Verifies nothing is queued for the server when the folder has no unread message.
    /// </summary>
    [Fact]
    public async Task Handle_NothingUnread_DoesNotSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var signal = new FlagPushSignal();

        var marked = await new MarkFolderReadCommandHandler(db.CreateMessageRepository(), signal)
            .Handle(new MarkFolderReadCommand(account.Id, folder.Id), ct);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        marked.Should().Be(0);
        var read = async () => await signal.ReadAsync(cancelled.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
    }
}
