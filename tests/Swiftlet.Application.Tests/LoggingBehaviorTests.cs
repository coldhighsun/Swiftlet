using AwesomeAssertions;
using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Queries;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="LoggingBehavior{TMessage, TResponse}"/>.
/// </summary>
public class LoggingBehaviorTests
{
    /// <summary>
    /// A command that carries a secret.
    /// </summary>
    /// <param name="Password">The secret.</param>
    private sealed record SecretCommand(string Password) : ICommand<int>;

    /// <summary>
    /// A query that carries a secret.
    /// </summary>
    /// <param name="Text">The text.</param>
    private sealed record SecretQuery(string Text) : IQuery<int>;

    /// <summary>
    /// Verifies a request about a folder is tagged with the account and folder names and describes its page.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_FolderRequest_TagsAccountAndFolderAndAddsDetails()
    {
        var names = new LogNames();
        var query = new GetMessagesQuery(Guid.NewGuid(), Guid.NewGuid(), 50, 25);
        names.RememberAccount(query.AccountId, "me@example.com");
        names.RememberFolder(query.FolderId, "INBOX");
        var logger = new ListLogger<LoggingBehavior<GetMessagesQuery, IReadOnlyList<MailMessage>>>();
        var behavior = new LoggingBehavior<GetMessagesQuery, IReadOnlyList<MailMessage>>(logger, names);

        await behavior.Handle(query, (_, _) => new ValueTask<IReadOnlyList<MailMessage>>(new List<MailMessage>()), TestContext.Current.CancellationToken);

        logger.Tags.Should().ContainSingle().Which.Should().Be(" [me@example.com] [INBOX]");
        logger.Entries.Single().Message.Should().Contain("GetMessagesQuery (skip 50, take 25)").And.EndWith("(0 items)");
    }

    /// <summary>
    /// Verifies a request about an account unknown by name is tagged with the start of its identifier.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_UnknownAccount_TagsShortIdentifier()
    {
        var query = new GetFoldersQuery(Guid.Parse("0123456789abcdef0123456789abcdef"));
        var logger = new ListLogger<LoggingBehavior<GetFoldersQuery, int>>();
        var behavior = new LoggingBehavior<GetFoldersQuery, int>(logger);

        await behavior.Handle(query, (_, _) => new ValueTask<int>(1), TestContext.Current.CancellationToken);

        logger.Tags.Should().ContainSingle().Which.Should().Be(" [01234567]");
    }

    /// <summary>
    /// Verifies a request about no account clears the tags of an outer scope and reports the number of results.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_GlobalRequest_ClearsOuterTagsAndCountsResults()
    {
        var logger = new ListLogger<LoggingBehavior<GetAccountsQuery, IReadOnlyList<MailAccount>>>();
        var behavior = new LoggingBehavior<GetAccountsQuery, IReadOnlyList<MailAccount>>(logger);

        using var outer = logger.BeginMailScope("other@example.com", "Sent");
        await behavior.Handle(new GetAccountsQuery(), (_, _) => new ValueTask<IReadOnlyList<MailAccount>>(new List<MailAccount>()), TestContext.Current.CancellationToken);

        logger.Tags.Should().ContainSingle().Which.Should().BeEmpty();
        logger.Entries.Single().Message.Should().EndWith("(0 items)");
    }

    /// <summary>
    /// Verifies a search logs the length of the search text and which conditions are set, but nothing that was entered.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_Search_LogsLengthWithoutText()
    {
        var logger = new ListLogger<LoggingBehavior<SearchMessagesQuery, int>>();
        var behavior = new LoggingBehavior<SearchMessagesQuery, int>(logger);

        var criteria = new MessageSearchCriteria { Text = "hunter2", Participants = "secret@x.com, boss", Subject = "salary", UnreadOnly = true };

        await behavior.Handle(new SearchMessagesQuery(criteria, 0, 50), (_, _) => new ValueTask<int>(1), TestContext.Current.CancellationToken);

        logger.Entries.Single().Message.Should().Contain("all accounts, 7 characters, conditions: participants/subject/unread")
            .And.NotContain("hunter2").And.NotContain("secret").And.NotContain("boss").And.NotContain("salary");
    }

    /// <summary>
    /// Verifies a successful command is logged at Information with its type name and without its contents.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_CommandSucceeds_LogsNameAtInformationWithoutContents()
    {
        var logger = new ListLogger<LoggingBehavior<SecretCommand, int>>();
        var behavior = new LoggingBehavior<SecretCommand, int>(logger);

        var result = await behavior.Handle(new SecretCommand("hunter2"), (_, _) => new ValueTask<int>(7), TestContext.Current.CancellationToken);

        result.Should().Be(7);
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("SecretCommand").And.Contain("succeeded");
        entry.Message.Should().NotContain("hunter2");
    }

    /// <summary>
    /// Verifies a query is logged at Debug.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_QuerySucceeds_LogsAtDebug()
    {
        var logger = new ListLogger<LoggingBehavior<SecretQuery, int>>();
        var behavior = new LoggingBehavior<SecretQuery, int>(logger);

        await behavior.Handle(new SecretQuery("hunter2"), (_, _) => new ValueTask<int>(1), TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Message.Should().NotContain("hunter2");
    }

    /// <summary>
    /// Verifies a failure is logged as an error and rethrown.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_HandlerThrows_LogsErrorAndRethrows()
    {
        var logger = new ListLogger<LoggingBehavior<SecretCommand, int>>();
        var behavior = new LoggingBehavior<SecretCommand, int>(logger);
        var boom = new InvalidOperationException("boom");

        var act = async () => await behavior.Handle(new SecretCommand("x"), (_, _) => throw boom, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(boom);
    }

    /// <summary>
    /// Verifies a cancellation is not logged as an error and is rethrown.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_Cancelled_IsNotAnErrorAndRethrows()
    {
        var logger = new ListLogger<LoggingBehavior<SecretCommand, int>>();
        var behavior = new LoggingBehavior<SecretCommand, int>(logger);

        var act = async () => await behavior.Handle(new SecretCommand("x"), (_, _) => throw new OperationCanceledException(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().ContainSingle().Which.Level.Should().NotBe(LogLevel.Error);
    }
}
