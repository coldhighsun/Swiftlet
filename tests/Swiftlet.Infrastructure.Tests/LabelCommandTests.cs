using AwesomeAssertions;
using Swiftlet.Application.Labels;
using Swiftlet.Application.Queries;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the label commands and the unified inbox and label queries.
/// </summary>
public class LabelCommandTests
{
    /// <summary>
    /// Verifies creating a label whose name differs only by case is rejected.
    /// </summary>
    [Fact]
    public async Task CreateLabel_DuplicateNameDifferentCase_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new CreateLabelCommandHandler(new LabelRepository(db.Context));
        await handler.Handle(new CreateLabelCommand("Work", "#112233"), ct);

        var act = async () => await handler.Handle(new CreateLabelCommand(" work ", "#445566"), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies renaming a label to another label's name is rejected.
    /// </summary>
    [Fact]
    public async Task UpdateLabel_RenameToExistingName_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = new LabelRepository(db.Context);
        var create = new CreateLabelCommandHandler(repository);
        await create.Handle(new CreateLabelCommand("Work", "#112233"), ct);
        var homeId = await create.Handle(new CreateLabelCommand("Home", "#445566"), ct);

        var act = async () => await new UpdateLabelCommandHandler(repository)
            .Handle(new UpdateLabelCommand(homeId, "WORK", "#445566"), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies a label can be updated while keeping its own name.
    /// </summary>
    [Fact]
    public async Task UpdateLabel_KeepOwnName_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = new LabelRepository(db.Context);
        var id = await new CreateLabelCommandHandler(repository).Handle(new CreateLabelCommand("Work", "#112233"), ct);

        await new UpdateLabelCommandHandler(repository).Handle(new UpdateLabelCommand(id, "work", "#AABBCC"), ct);

        await using var verify = db.CreateVerifier();
        var loaded = await verify.Labels().GetAsync(id, ct);
        loaded!.Name.Should().Be("work");
        loaded.Color.Should().Be("#AABBCC");
    }

    /// <summary>
    /// Verifies assigning and unassigning a label persists across contexts.
    /// </summary>
    [Fact]
    public async Task SetMessageLabel_Assign_ThenUnassign_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var messages = db.CreateMessageRepository();
        var labels = new LabelRepository(db.Context);
        var message = MailMessage.Create(account.Id, folder.Id, 1, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        await messages.AddAsync(message, ct);
        var labelId = await new CreateLabelCommandHandler(labels).Handle(new CreateLabelCommand("Work", "#112233"), ct);
        var handler = new SetMessageLabelCommandHandler(messages, labels);

        await handler.Handle(new SetMessageLabelCommand(account.Id, message.Id, labelId, true), ct);
        await using (var verify = db.CreateVerifier())
        {
            var loaded = await verify.Messages().GetAsync(account.Id, message.Id, ct);
            loaded!.Labels.Select(l => l.Id).Should().Equal(labelId);
        }

        await handler.Handle(new SetMessageLabelCommand(account.Id, message.Id, labelId, false), ct);
        await using (var verify = db.CreateVerifier())
        {
            var loaded = await verify.Messages().GetAsync(account.Id, message.Id, ct);
            loaded!.Labels.Should().BeEmpty();
        }
    }

    /// <summary>
    /// Verifies assigning a label to an unknown message fails clearly.
    /// </summary>
    [Fact]
    public async Task SetMessageLabel_UnknownMessage_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var labels = new LabelRepository(db.Context);
        var labelId = await new CreateLabelCommandHandler(labels).Handle(new CreateLabelCommand("Work", "#112233"), ct);
        var handler = new SetMessageLabelCommandHandler(db.CreateMessageRepository(), labels);

        var act = async () => await handler.Handle(new SetMessageLabelCommand(Guid.NewGuid(), Guid.NewGuid(), labelId, true), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies assigning an unknown label fails clearly.
    /// </summary>
    [Fact]
    public async Task SetMessageLabel_UnknownLabel_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var messages = db.CreateMessageRepository();
        var message = MailMessage.Create(account.Id, folder.Id, 1, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        await messages.AddAsync(message, ct);
        var handler = new SetMessageLabelCommandHandler(messages, new LabelRepository(db.Context));

        var act = async () => await handler.Handle(new SetMessageLabelCommand(account.Id, message.Id, Guid.NewGuid(), true), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies deleting a label keeps the messages that carried it.
    /// </summary>
    [Fact]
    public async Task DeleteLabel_MessagesRemain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var messages = db.CreateMessageRepository();
        var labels = new LabelRepository(db.Context);
        var message = MailMessage.Create(account.Id, folder.Id, 1, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        await messages.AddAsync(message, ct);
        var labelId = await new CreateLabelCommandHandler(labels).Handle(new CreateLabelCommand("Work", "#112233"), ct);
        await new SetMessageLabelCommandHandler(messages, labels).Handle(new SetMessageLabelCommand(account.Id, message.Id, labelId, true), ct);

        await new DeleteLabelCommandHandler(labels).Handle(new DeleteLabelCommand(labelId), ct);

        await using var verify = db.CreateVerifier();
        var loaded = await verify.Messages().GetAsync(account.Id, message.Id, ct);
        loaded.Should().NotBeNull();
        loaded.Labels.Should().BeEmpty();
        (await verify.Labels().GetAsync(labelId, ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies the label, unified inbox and by-label queries return repository results.
    /// </summary>
    [Fact]
    public async Task GetUnifiedInbox_And_GetMessagesByLabel_ReturnRepositoryResults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var messages = db.CreateMessageRepository();
        var labels = new LabelRepository(db.Context);
        var first = MailMessage.Create(account.Id, folder.Id, 1, "one", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        var second = MailMessage.Create(account.Id, folder.Id, 2, "two", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        await messages.AddAsync(first, ct);
        await messages.AddAsync(second, ct);
        var labelId = await new CreateLabelCommandHandler(labels).Handle(new CreateLabelCommand("Work", "#112233"), ct);
        await new SetMessageLabelCommandHandler(messages, labels).Handle(new SetMessageLabelCommand(account.Id, first.Id, labelId, true), ct);

        var all = await new GetLabelsQueryHandler(labels).Handle(new GetLabelsQuery(), ct);
        var inbox = await new GetUnifiedInboxQueryHandler(messages).Handle(new GetUnifiedInboxQuery(0, 10), ct);
        var byLabel = await new GetMessagesByLabelQueryHandler(messages).Handle(new GetMessagesByLabelQuery(labelId, 0, 10), ct);

        all.Select(l => l.Name).Should().Equal("Work");
        inbox.Should().HaveCount(2);
        byLabel.Select(m => m.Id).Should().Equal(first.Id);
    }
}
