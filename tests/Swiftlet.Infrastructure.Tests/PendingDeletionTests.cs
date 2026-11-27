using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the delete and move commands and <see cref="PendingDeletionService"/> against real SQLite repositories and a scripted IMAP session.
/// </summary>
public class PendingDeletionTests
{
    /// <summary>
    /// Verifies deleting messages hides them from every view at once without contacting the server.
    /// </summary>
    [Fact]
    public async Task DeleteMessages_Selection_HidesThemFromEveryViewAndSignals()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var gone = await s.AddAsync(s.Inbox, 1, ct, "needle one");
        var kept = await s.AddAsync(s.Inbox, 2, ct, "needle two");
        var label = Label.Create("Work", "#E53935");
        await s.Db.CreateLabelRepository().AddAsync(label, ct);
        gone.AddLabel(label);
        await s.Messages.SaveLabelsAsync(gone, ct);
        var signal = new DeletionSignal();

        // Act
        var marked = await new DeleteMessagesCommandHandler(s.Messages, signal)
            .Handle(new DeleteMessagesCommand([new MessageKey(s.Account.Id, gone.Id)]), ct);

        // Assert
        marked.Should().Be(1);
        await signal.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        await using var verifier = s.Db.CreateVerifier();
        var repository = verifier.Messages();
        (await repository.GetByFolderAsync(s.Account.Id, s.Inbox.Id, 0, 10, ct)).Select(m => m.Id).Should().Equal(kept.Id);
        (await repository.GetUnifiedInboxAsync(0, 10, ct)).Select(m => m.Id).Should().Equal(kept.Id);
        (await repository.GetByLabelAsync(label.Id, 0, 10, ct)).Should().BeEmpty();
        (await repository.SearchAsync("needle", null, 0, 10, ct)).Select(m => m.Id).Should().Equal(kept.Id);
        (await repository.SearchAsync("ne", null, 0, 10, ct)).Select(m => m.Id).Should().Equal(kept.Id);
        (await repository.GetMaxUidAsync(s.Account.Id, s.Inbox.Id, ct)).Should().Be(2u, "a hidden message must not be downloaded again");
        s.Factory.ConnectCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies the All Mail copy of a message becomes searchable when the other copy is being deleted.
    /// </summary>
    [Fact]
    public async Task Search_OtherCopyPendingDeletion_ShowsTheAllMailCopy()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var allMail = await s.AddFolderAsync("All", FolderRole.AllMail, ct);
        var inboxCopy = await s.AddAsync(s.Inbox, 1, ct, "needle", "<same@x>");
        var allCopy = await s.AddAsync(allMail, 1, ct, "needle", "<same@x>");

        // Act
        await s.Messages.MarkDeletePendingAsync(s.Account.Id, [inboxCopy.Id], ct);
        var found = await s.Messages.SearchAsync("needle", s.Account.Id, 0, 10, ct);

        // Assert
        found.Select(m => m.Id).Should().Equal(allCopy.Id);
    }

    /// <summary>
    /// Verifies one batch moves all pending messages of a folder to Trash with a single server command.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_PendingMessages_MovesThemInOneCommandAndRemovesRows()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var trash = await s.AddFolderAsync("Trash", FolderRole.Trash, ct);
        var first = await s.AddAsync(s.Inbox, 5, ct);
        var second = await s.AddAsync(s.Inbox, 6, ct);
        await s.MarkAsync(ct, first, second);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().Be(new DeletionBatchResult("INBOX", 2, 0));
        var call = s.Server.DeleteCalls.Should().ContainSingle().Which;
        call.Folder.Should().Be("INBOX");
        call.Uids.Should().Equal(5u, 6u);
        call.Destination.Should().Be(trash.FullName);
        (await s.FindAsync(first.Id, ct)).Should().BeNull("the Trash was never synchronized, so the next sync downloads it");
        (await s.ProcessAsync(ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies deleting inside Trash, or without a Trash folder, is permanent.
    /// </summary>
    /// <param name="inTrash">Whether the message sits in Trash; otherwise the account has no Trash folder.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessBatch_InTrashOrWithoutTrash_DeletesPermanently(bool inTrash)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var folder = inTrash ? await s.AddFolderAsync("Trash", FolderRole.Trash, ct) : s.Inbox;
        var message = await s.AddAsync(folder, 7, ct);
        await s.MarkAsync(ct, message);

        // Act
        await s.ProcessAsync(ct);

        // Assert
        s.Server.DeleteCalls.Should().ContainSingle().Which.Destination.Should().BeNull();
        s.Server.RangeCalls.Should().BeEmpty();
        (await s.FindAsync(message.Id, ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies a large selection is split into batches.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_MoreThanBatchSize_ProcessesInBatches()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var added = new List<MailMessage>();
        for (uint uid = 1; uid <= PendingDeletionService.BatchSize + 5; uid++)
        {
            added.Add(await s.AddAsync(s.Inbox, uid, ct));
        }

        await s.MarkAsync(ct, [.. added]);

        // Act
        var first = await s.ProcessAsync(ct);
        var second = await s.ProcessAsync(ct);

        // Assert
        first!.Remaining.Should().Be(5);
        second!.Remaining.Should().Be(0);
        s.Server.DeleteCalls.Select(c => c.Uids.Length).Should().Equal(PendingDeletionService.BatchSize, 5);
    }

    /// <summary>
    /// Verifies moved rows join a complete cached Trash with their identity and attachments, and no sync downloads them again.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_TrashSyncedAndContiguous_RehomesRowsAndSyncDownloadsNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var trash = await s.AddSyncedTrashAsync(ct);
        var first = await s.AddAsync(s.Inbox, 5, ct, attachment: "keep.pdf");
        var second = await s.AddAsync(s.Inbox, 6, ct);
        await s.MarkAsync(ct, first, second);

        // Act
        await s.ProcessAsync(ct);
        s.Server.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        s.Server.Folders.Add(TestData.Folder("Trash", FolderRole.Trash));
        var sync = await s.CreateSync().SyncAsync(s.Account.Id, ct);

        // Assert
        sync.NewMessages.Should().Be(0);
        await using var verifier = s.Db.CreateVerifier();
        var inTrash = await verifier.Messages().GetByFolderAsync(s.Account.Id, trash.Id, 0, 10, ct);
        inTrash.Select(m => m.Uid).Should().BeEquivalentTo([10u, 11u, 12u]);
        var moved = inTrash.Single(m => m.Id == first.Id);
        moved.IsDeletePending.Should().BeFalse();
        moved.Attachments.Should().ContainSingle().Which.FileName.Should().Be("keep.pdf");
    }

    /// <summary>
    /// Verifies a message another client put into Trash in between stops the re-homing there, so the next sync fills the hole.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_UnknownMessageInTrash_RemovesRowsSoNoHoleRemains()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var trash = await s.AddSyncedTrashAsync(ct);
        var message = await s.AddAsync(s.Inbox, 5, ct);
        s.Server.Messages["Trash"].Add(TestData.Remote(11));
        await s.MarkAsync(ct, message);

        // Act
        await s.ProcessAsync(ct);

        // Assert
        (await s.FindAsync(message.Id, ct)).Should().BeNull();
        (await s.Messages.GetMaxUidAsync(s.Account.Id, trash.Id, ct)).Should().Be(10u);
    }

    /// <summary>
    /// Verifies a server without new UIDs, or a Trash whose UIDVALIDITY changed, leaves the work to the next sync.
    /// </summary>
    /// <param name="reportsUid">Whether the server reports the new UID.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessBatch_TrashCannotBeTrusted_RemovesRow(bool reportsUid)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.AddSyncedTrashAsync(ct);
        var message = await s.AddAsync(s.Inbox, 5, ct);
        s.Server.MoveReportsNewUid = reportsUid;
        if (reportsUid)
        {
            s.Server.UidValidity["Trash"] = 77;
        }

        await s.MarkAsync(ct, message);

        // Act
        await s.ProcessAsync(ct);

        // Assert
        (await s.FindAsync(message.Id, ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies a lost connection is thrown to the caller and leaves the messages pending.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_ConnectionLost_ThrowsAndKeepsPending()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.MarkAsync(ct, await s.AddAsync(s.Inbox, 5, ct));
        s.Server.DeleteFailure = new IOException("connection reset");

        // Act
        var act = () => s.ProcessAsync(ct);

        // Assert
        await act.Should().ThrowAsync<IOException>();
        (await s.Messages.CountPendingDeletionsAsync(s.Account.Id, ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies a refusal of the server, or a missing server feature, makes the messages visible again and reports why.
    /// </summary>
    /// <param name="unsupported">Whether the failure is a missing server feature.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessBatch_ServerRefuses_ShowsMessagesAgain(bool unsupported)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.MarkAsync(ct, await s.AddAsync(s.Inbox, 5, ct));
        s.Server.DeleteFailure = unsupported ? new NotSupportedException("no UIDPLUS") : new MailOperationRejectedException("read only");

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result!.Error.Should().Be(s.Server.DeleteFailure.Message);
        result.Deleted.Should().Be(0);
        (await s.Messages.GetByFolderAsync(s.Account.Id, s.Inbox.Id, 0, 10, ct)).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies a changed server UIDVALIDITY blocks the delete so a stale UID cannot hit another message.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_UidValidityChanged_NeverCallsDeleteAndShowsMessagesAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Inbox.UpdateUidValidity(100);
        await s.Folders.SaveChangesAsync(ct);
        await s.MarkAsync(ct, await s.AddAsync(s.Inbox, 5, ct));
        s.Server.UidValidity["INBOX"] = 200;

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result!.Error.Should().NotBeNull();
        s.Server.DeleteCalls.Should().BeEmpty();
        (await s.Messages.CountPendingDeletionsAsync(s.Account.Id, ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies emptying a folder also deletes history that was never downloaded, keeps later mail, and walks from the oldest message up.
    /// </summary>
    [Fact]
    public async Task EmptyFolder_UncachedHistoryAndNewMail_DeletesUpToTheRequestOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.AddFolderAsync("Trash", FolderRole.Trash, ct);
        s.Server.Messages["INBOX"] = [.. Enumerable.Range(1, 150).Select(i => TestData.Remote((uint)i))];
        var newest = await s.AddAsync(s.Inbox, 150, ct);
        var older = await s.AddAsync(s.Inbox, 149, ct);

        // Act
        var queued = await s.EmptyAsync(s.Inbox, ct);
        s.Server.Messages["INBOX"].Add(TestData.Remote(151));
        var first = await s.ProcessAsync(ct);
        var second = await s.ProcessAsync(ct);

        // Assert
        queued.Should().BeTrue();
        first!.Deleted.Should().Be(100);
        first.Remaining.Should().Be(50);
        second!.Deleted.Should().Be(50);
        s.Server.DeleteCalls[0].Uids[0].Should().Be(1u);
        s.Server.Messages["INBOX"].Select(m => m.Uid).Should().Equal(151u);
        (await s.FindAsync(newest.Id, ct)).Should().BeNull();
        (await s.FindAsync(older.Id, ct)).Should().BeNull();
        await using var verifier = s.Db.CreateVerifier();
        (await verifier.Folders().GetAsync(s.Account.Id, s.Inbox.Id, ct))!.EmptyThroughUid.Should().BeNull();
        (await s.ProcessAsync(ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies a request whose messages are already gone from the server still completes and removes the cached rows.
    /// </summary>
    [Fact]
    public async Task EmptyFolder_ServerAlreadyEmpty_RemovesRowsAndCompletes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var message = await s.AddAsync(s.Inbox, 3, ct);
        await s.EmptyAsync(s.Inbox, ct);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().Be(new DeletionBatchResult("INBOX", 0, 0));
        s.Server.DeleteCalls.Should().BeEmpty();
        (await s.FindAsync(message.Id, ct)).Should().BeNull();
        (await s.Service.HasWorkAsync(s.Account.Id, ct)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a refused empty request is abandoned and the messages reappear.
    /// </summary>
    [Fact]
    public async Task EmptyFolder_ServerRefuses_AbandonsTheRequest()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Server.Messages["INBOX"] = [TestData.Remote(3)];
        await s.AddAsync(s.Inbox, 3, ct);
        await s.EmptyAsync(s.Inbox, ct);
        s.Server.DeleteFailure = new MailOperationRejectedException("read only");

        // Act
        await s.ProcessAsync(ct);

        // Assert
        (await s.Service.HasWorkAsync(s.Account.Id, ct)).Should().BeFalse();
        (await s.Messages.GetByFolderAsync(s.Account.Id, s.Inbox.Id, 0, 10, ct)).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies a folder without cached messages has nothing to delete.
    /// </summary>
    [Fact]
    public async Task EmptyFolder_NothingCached_ReturnsFalse()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        // Act
        var queued = await s.EmptyAsync(s.Inbox, ct);

        // Assert
        queued.Should().BeFalse();
        (await s.Service.HasWorkAsync(s.Account.Id, ct)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies All Mail cannot be emptied.
    /// </summary>
    [Fact]
    public async Task EmptyFolder_AllMail_Throws()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var allMail = await s.AddFolderAsync("All", FolderRole.AllMail, ct);
        await s.AddAsync(allMail, 1, ct);

        // Act
        var act = () => s.EmptyAsync(allMail, ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await s.Messages.CountPendingDeletionsAsync(s.Account.Id, ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies the history backfill leaves a folder alone while it is being emptied.
    /// </summary>
    [Fact]
    public async Task Backfill_FolderBeingEmptied_IsSkipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Inbox.UpdateUidValidity(1);
        await s.Folders.SaveChangesAsync(ct);
        await s.AddAsync(s.Inbox, 30, ct);
        s.Server.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(30)];
        await s.EmptyAsync(s.Inbox, ct);
        var backfill = new MailBackfillService(
            s.Folders,
            s.Messages,
            new MailSyncOptions(),
            new FakeSyncSettings(),
            NullLogger<MailBackfillService>.Instance);

        // Act
        var result = await backfill.BackfillBatchAsync(s.Server, s.Account.Id, s.Inbox.Id, ct);

        // Assert
        result.Outcome.Should().Be(BackfillOutcome.Skipped);
        s.Server.FetchOlderCalls.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies moving hides the messages at once, skips those already in the target and wakes the background worker.
    /// </summary>
    [Fact]
    public async Task MoveMessages_Selection_HidesThemSkipsThoseInTargetAndSignals()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var archive = await s.AddFolderAsync("Archive", FolderRole.Archive, ct);
        var moved = await s.AddAsync(s.Inbox, 1, ct);
        var already = await s.AddAsync(archive, 2, ct);
        var signal = new DeletionSignal();

        // Act
        var marked = await new MoveMessagesCommandHandler(s.Folders, s.Messages, signal)
            .Handle(new MoveMessagesCommand(s.Account.Id, [moved.Id, already.Id], archive.Id), ct);

        // Assert
        marked.Should().Be(1);
        await signal.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        (await s.Messages.GetByFolderAsync(s.Account.Id, s.Inbox.Id, 0, 10, ct)).Should().BeEmpty();
        (await s.Messages.GetByFolderAsync(s.Account.Id, archive.Id, 0, 10, ct)).Select(m => m.Id).Should().Equal(already.Id);
        (await s.FindAsync(moved.Id, ct))!.PendingMoveFolderId.Should().Be(archive.Id);
        s.Factory.ConnectCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies a move to a folder the account does not have is rejected and hides nothing.
    /// </summary>
    [Fact]
    public async Task MoveMessages_UnknownTarget_Throws()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var message = await s.AddAsync(s.Inbox, 1, ct);

        // Act
        var act = async () => await new MoveMessagesCommandHandler(s.Folders, s.Messages, new DeletionSignal())
            .Handle(new MoveMessagesCommand(s.Account.Id, [message.Id], Guid.NewGuid()), ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await s.Messages.CountPendingDeletionsAsync(s.Account.Id, ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies a move reaches the server and the row joins a complete cached target with its identity.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_PendingMove_MovesToTargetAndRehomesRow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.AddFolderAsync("Trash", FolderRole.Trash, ct);
        var archive = await s.AddFolderAsync("Archive", FolderRole.Archive, ct);
        archive.UpdateUidValidity(1);
        await s.Folders.SaveChangesAsync(ct);
        await s.AddAsync(archive, 10, ct);
        s.Server.Messages["Archive"] = [TestData.Remote(10)];
        var message = await s.AddAsync(s.Inbox, 5, ct, attachment: "keep.pdf");
        await s.MarkMoveAsync(archive.Id, ct, message);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result.Should().Be(new DeletionBatchResult("INBOX", 1, 0));
        s.Server.DeleteCalls.Should().ContainSingle().Which.Destination.Should().Be("Archive");
        var stored = (await s.FindAsync(message.Id, ct))!;
        stored.FolderId.Should().Be(archive.Id);
        stored.Uid.Should().Be(11u);
        stored.IsDeletePending.Should().BeFalse();
        stored.PendingMoveFolderId.Should().BeNull();
        stored.Attachments.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies moving out of Trash moves the message instead of deleting it for good; a target never synchronized
    /// gets the message with its next sync.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_MoveOutOfTrash_MovesInsteadOfDeleting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var trash = await s.AddFolderAsync("Trash", FolderRole.Trash, ct);
        var message = await s.AddAsync(trash, 7, ct);
        await s.MarkMoveAsync(s.Inbox.Id, ct, message);

        // Act
        await s.ProcessAsync(ct);

        // Assert
        s.Server.DeleteCalls.Should().ContainSingle().Which.Destination.Should().Be("INBOX");
        s.Server.Messages["INBOX"].Should().ContainSingle();
        (await s.FindAsync(message.Id, ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies deletions and moves to different folders of one source folder are sent as separate server commands.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_SeveralDestinations_UsesOneCommandPerDestination()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.AddFolderAsync("Trash", FolderRole.Trash, ct);
        var archive = await s.AddFolderAsync("Archive", FolderRole.Archive, ct);
        var work = await s.AddFolderAsync("Work", FolderRole.Other, ct);
        await s.MarkAsync(ct, await s.AddAsync(s.Inbox, 1, ct));
        await s.MarkMoveAsync(archive.Id, ct, await s.AddAsync(s.Inbox, 2, ct), await s.AddAsync(s.Inbox, 4, ct));
        await s.MarkMoveAsync(work.Id, ct, await s.AddAsync(s.Inbox, 3, ct));

        // Act
        while (await s.ProcessAsync(ct) is not null)
        {
        }

        // Assert
        s.Server.DeleteCalls.Select(c => $"{c.Destination}:{string.Join(',', c.Uids)}")
            .Should().BeEquivalentTo(["Trash:1", "Archive:2,4", "Work:3"]);
    }

    /// <summary>
    /// Verifies a refused move shows only its own messages again and leaves other moves pending.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_MoveRefused_RestoresOnlyThatMove()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var archive = await s.AddFolderAsync("Archive", FolderRole.Archive, ct);
        var work = await s.AddFolderAsync("Work", FolderRole.Other, ct);
        await s.MarkMoveAsync(archive.Id, ct, await s.AddAsync(s.Inbox, 1, ct));
        await s.MarkMoveAsync(work.Id, ct, await s.AddAsync(s.Inbox, 2, ct));
        s.Server.DeleteFailures.Enqueue(new MailOperationRejectedException("read only"));

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result!.Error.Should().Be("read only");
        result.Remaining.Should().Be(1);
        (await s.Messages.GetByFolderAsync(s.Account.Id, s.Inbox.Id, 0, 10, ct)).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies a move whose target folder vanished never reaches the server, and never turns into a deletion.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_MoveTargetGone_ShowsMessagesAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        await s.AddFolderAsync("Trash", FolderRole.Trash, ct);
        var message = await s.AddAsync(s.Inbox, 5, ct);
        await s.MarkMoveAsync(Guid.NewGuid(), ct, message);

        // Act
        var result = await s.ProcessAsync(ct);

        // Assert
        result!.Error.Should().NotBeNull();
        s.Server.DeleteCalls.Should().BeEmpty();
        var stored = (await s.FindAsync(message.Id, ct))!;
        stored.IsDeletePending.Should().BeFalse();
        stored.PendingMoveFolderId.Should().BeNull();
    }

    /// <summary>
    /// Verifies a deletion replaces a pending move, and a late move never replaces a pending deletion.
    /// </summary>
    /// <param name="emptyFolder">Whether the deletion empties the folder; otherwise it deletes the message.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_PendingMove_DeletionWins(bool emptyFolder)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        var archive = await s.AddFolderAsync("Archive", FolderRole.Archive, ct);
        var message = await s.AddAsync(s.Inbox, 5, ct);
        await s.MarkMoveAsync(archive.Id, ct, message);

        // Act
        if (emptyFolder)
        {
            await s.EmptyAsync(s.Inbox, ct);
        }
        else
        {
            await s.Messages.MarkDeletePendingAsync(s.Account.Id, [message.Id], ct);
        }

        var moved = await s.Messages.MarkMovePendingAsync(s.Account.Id, [message.Id], archive.Id, ct);

        // Assert
        moved.Should().Be(0);
        var stored = (await s.FindAsync(message.Id, ct))!;
        stored.IsDeletePending.Should().BeTrue();
        stored.PendingMoveFolderId.Should().BeNull();
    }

    /// <summary>
    /// An account with an INBOX, its repositories and a scripted server.
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
            Service = new PendingDeletionService(Folders, Messages, NullLogger<PendingDeletionService>.Instance);
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
        /// Gets the message repository.
        /// </summary>
        public MessageRepository Messages { get; }

        /// <summary>
        /// Gets the folder repository.
        /// </summary>
        public FolderRepository Folders { get; }

        /// <summary>
        /// Gets the service under test.
        /// </summary>
        public PendingDeletionService Service { get; }

        /// <summary>
        /// Gets the connection factory.
        /// </summary>
        public FakeImapSessionFactory Factory { get; } = new();

        /// <summary>
        /// Gets the scripted server.
        /// </summary>
        public FakeImapSession Server => Factory.Session;

        /// <summary>
        /// Creates a scenario.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scenario.</returns>
        public static async Task<Scenario> CreateAsync(CancellationToken cancellationToken)
        {
            var db = await TestDatabase.CreateAsync(cancellationToken);
            var (account, inbox) = await db.SeedAccountAsync(cancellationToken);
            return new Scenario(db, account, inbox);
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
        /// Adds a Trash that was synchronized and caches the one message (UID 10) the server holds there.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The folder.</returns>
        public async Task<MailFolder> AddSyncedTrashAsync(CancellationToken cancellationToken)
        {
            var trash = await AddFolderAsync("Trash", FolderRole.Trash, cancellationToken);
            trash.UpdateUidValidity(1);
            await Folders.SaveChangesAsync(cancellationToken);
            await AddAsync(trash, 10, cancellationToken);
            Server.Messages["Trash"] = [TestData.Remote(10)];
            return trash;
        }

        /// <summary>
        /// Caches a message; a message of the INBOX or Trash is expected on the server only where a test puts it there.
        /// </summary>
        /// <param name="folder">The folder.</param>
        /// <param name="uid">The UID.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="subject">The subject.</param>
        /// <param name="messageId">The Message-ID.</param>
        /// <param name="attachment">The name of an attachment to add.</param>
        /// <returns>The message.</returns>
        public async Task<MailMessage> AddAsync(
            MailFolder folder,
            uint uid,
            CancellationToken cancellationToken,
            string subject = "s",
            string? messageId = null,
            string? attachment = null)
        {
            var message = MailMessage.Create(
                Account.Id, folder.Id, uid, subject, "A", "a@example.com", "r", DateTimeOffset.UnixEpoch.AddMinutes(uid), "b", null, messageId);
            if (attachment is not null)
            {
                message.AddAttachment(attachment, "application/pdf", 9);
            }

            await Messages.AddAsync(message, cancellationToken);
            return message;
        }

        /// <summary>
        /// Marks messages for deletion and puts them on the server.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="messages">The messages.</param>
        /// <returns>A task that completes when they are marked.</returns>
        public async Task MarkAsync(CancellationToken cancellationToken, params MailMessage[] messages)
        {
            await PutOnServerAsync(messages, cancellationToken);
            await Messages.MarkDeletePendingAsync(Account.Id, [.. messages.Select(m => m.Id)], cancellationToken);
        }

        /// <summary>
        /// Marks messages for a move and puts them on the server.
        /// </summary>
        /// <param name="targetFolderId">The folder to move them to.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="messages">The messages.</param>
        /// <returns>A task that completes when they are marked.</returns>
        public async Task MarkMoveAsync(Guid targetFolderId, CancellationToken cancellationToken, params MailMessage[] messages)
        {
            await PutOnServerAsync(messages, cancellationToken);
            await Messages.MarkMovePendingAsync(Account.Id, [.. messages.Select(m => m.Id)], targetFolderId, cancellationToken);
        }

        /// <summary>
        /// Puts cached messages on the scripted server.
        /// </summary>
        /// <param name="messages">The messages.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the server holds them.</returns>
        private async Task PutOnServerAsync(MailMessage[] messages, CancellationToken cancellationToken)
        {
            var folders = await Folders.GetByAccountAsync(Account.Id, cancellationToken);
            foreach (var message in messages)
            {
                var name = folders.Single(f => f.Id == message.FolderId).FullName;
                if (!Server.Messages.TryGetValue(name, out var list))
                {
                    list = [];
                    Server.Messages[name] = list;
                }

                list.Add(TestData.Remote(message.Uid));
            }
        }

        /// <summary>
        /// Requests that a folder be emptied.
        /// </summary>
        /// <param name="folder">The folder.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Whether anything was queued.</returns>
        public async Task<bool> EmptyAsync(MailFolder folder, CancellationToken cancellationToken)
        {
            return await new EmptyFolderCommandHandler(Folders, Messages, new DeletionSignal())
                .Handle(new EmptyFolderCommand(Account.Id, folder.Id), cancellationToken);
        }

        /// <summary>
        /// Runs one deletion batch.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The result.</returns>
        public Task<DeletionBatchResult?> ProcessAsync(CancellationToken cancellationToken)
        {
            return Service.ProcessBatchAsync(Server, Account.Id, cancellationToken);
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

        /// <summary>
        /// Creates a regular sync over the scenario's repositories.
        /// </summary>
        /// <returns>The sync service.</returns>
        public MailSyncService CreateSync()
        {
            return new MailSyncService(
                Db.CreateAccountRepository(),
                Folders,
                Messages,
                Factory,
                new FakeSyncSettings(),
                new MailSyncOptions(),
                NullLogger<MailSyncService>.Instance);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return Db.DisposeAsync();
        }
    }
}
