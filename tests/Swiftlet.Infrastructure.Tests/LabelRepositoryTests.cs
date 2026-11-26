using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="LabelRepository"/>.
/// </summary>
public class LabelRepositoryTests
{
    /// <summary>
    /// Verifies name checks ignore case and surrounding whitespace.
    /// </summary>
    [Fact]
    public async Task NameExistsAsync_DifferentCaseAndWhitespace_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = new LabelRepository(db.Context);
        await repository.AddAsync(Label.Create("Work", "#112233"), ct);

        (await repository.NameExistsAsync("  wORk ", null, ct)).Should().BeTrue();
        (await repository.NameExistsAsync("Home", null, ct)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a label does not conflict with itself.
    /// </summary>
    [Fact]
    public async Task NameExistsAsync_ExcludingSelf_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = new LabelRepository(db.Context);
        var label = Label.Create("Work", "#112233");
        await repository.AddAsync(label, ct);

        (await repository.NameExistsAsync("work", label.Id, ct)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies deleting a label removes the join rows but keeps the messages.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_RemovesJoinRowsButKeepsMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new LabelRepository(db.Context);
        var label = Label.Create("Work", "#112233");
        await repository.AddAsync(label, ct);
        var message = MailMessage.Create(account.Id, folder.Id, 1, "s", "A", "a@example.com", "me@example.com", DateTimeOffset.UtcNow, "b");
        message.AddLabel(label);
        var messages = db.CreateMessageRepository();
        await messages.AddAsync(message, ct);
        await messages.SaveLabelsAsync(message, ct);

        await repository.DeleteAsync(label.Id, ct);

        await using var verifyContext = db.CreateVerifier();
        verifyContext.Global.Labels.Should().BeEmpty();
        var loaded = await verifyContext.Messages().GetAsync(account.Id, message.Id, ct);
        loaded.Should().NotBeNull();
        loaded.Labels.Should().BeEmpty();
        var links = await verifyContext.Global.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM MessageLabels")
            .SingleAsync(ct);
        links.Should().Be(0);
    }

    /// <summary>
    /// Verifies labels are returned sorted by name.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_SortedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = new LabelRepository(db.Context);
        await repository.AddAsync(Label.Create("Zeta", "#112233"), ct);
        await repository.AddAsync(Label.Create("alpha", "#112233"), ct);
        await repository.AddAsync(Label.Create("Beta", "#112233"), ct);

        var result = await repository.GetAllAsync(ct);

        result.Select(l => l.Name).Should().Equal("alpha", "Beta", "Zeta");
    }
}
