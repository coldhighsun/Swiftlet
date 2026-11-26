using AwesomeAssertions;
using Mediator;
using Microsoft.Extensions.Logging;

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
