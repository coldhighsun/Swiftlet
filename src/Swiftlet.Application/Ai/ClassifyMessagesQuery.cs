using System.Runtime.CompilerServices;
using System.Text;
using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Ai;

/// <summary>
/// A message to classify.
/// </summary>
/// <param name="AccountId">The account that owns it.</param>
/// <param name="MessageId">The message identifier.</param>
public sealed record MessageReference(Guid AccountId, Guid MessageId);

/// <summary>
/// How the classification of a message ended.
/// </summary>
public enum ClassificationStatus
{
    /// <summary>
    /// There is something new to apply.
    /// </summary>
    Suggested,

    /// <summary>
    /// Nothing would change, or the answer could not be read, or the model declined.
    /// </summary>
    NoSuggestion,

    /// <summary>
    /// The request for this message failed.
    /// </summary>
    Failed,
}

/// <summary>
/// The result for one message. Only changes are suggested: labels it lacks and a priority it does not have.
/// </summary>
/// <param name="Message">The message.</param>
/// <param name="Status">How it ended.</param>
/// <param name="LabelIds">The suggested labels.</param>
/// <param name="Priority">The suggested priority, if any.</param>
/// <param name="Failure">Why the request failed.</param>
public sealed record MessageClassification(
    MessageReference Message,
    ClassificationStatus Status,
    IReadOnlyList<Guid> LabelIds,
    MessagePriority? Priority = null,
    AiFailure? Failure = null);

/// <summary>
/// Asks for labels and a priority for messages, one request per message, so the text of one cannot influence the result
/// of another. Nothing is changed.
/// </summary>
/// <param name="Messages">The messages; only the first <see cref="MaxMessages"/> are classified.</param>
public sealed record ClassifyMessagesQuery(IReadOnlyList<MessageReference> Messages)
    : IStreamQuery<MessageClassification>, ILogDetails
{
    /// <summary>
    /// The largest number of messages of one request.
    /// </summary>
    public const int MaxMessages = 20;

    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"{Messages.Count} messages";
    }
}

/// <summary>
/// Handles <see cref="ClassifyMessagesQuery"/>; results arrive as they are ready.
/// </summary>
/// <param name="answers">The connection to the service.</param>
/// <param name="messages">The message repository.</param>
/// <param name="labels">The label repository.</param>
public sealed class ClassifyMessagesQueryHandler(
    AiAnswers answers,
    IMessageRepository messages,
    ILabelRepository labels)
    : IStreamQueryHandler<ClassifyMessagesQuery, MessageClassification>
{
    /// <summary>
    /// How many requests run at the same time.
    /// </summary>
    private const int Concurrency = 2;

    /// <summary>
    /// The most body text sent per message; the beginning is enough to classify it.
    /// </summary>
    private const int MaxBodyCharacters = 4_000;

    /// <inheritdoc />
    public IAsyncEnumerable<MessageClassification> Handle(ClassifyMessagesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return StreamAsync(query, cancellationToken);
    }

    /// <summary>
    /// Classifies the messages.
    /// </summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The results.</returns>
    private async IAsyncEnumerable<MessageClassification> StreamAsync(
        ClassifyMessagesQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var target = answers.RequireTarget();
        var existing = await labels.GetAllAsync(cancellationToken);

        // Read one after the other, because they share a database context; only the requests overlap.
        var loaded = new List<(MessageReference Reference, MailMessage? Message)>();
        foreach (var reference in query.Messages.Take(ClassifyMessagesQuery.MaxMessages))
        {
            loaded.Add((reference, await messages.GetAsync(reference.AccountId, reference.MessageId, cancellationToken)));
        }

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new RequestSlots(Concurrency);
        var tasks = loaded
            .Select(item => ClassifyAsync(target, item.Reference, item.Message, existing, slots, running.Token))
            .ToList();
        try
        {
            await foreach (var finished in Task.WhenEach(tasks).WithCancellation(cancellationToken))
            {
                yield return await finished;
            }
        }
        finally
        {
            // After a failure that concerns every message, or when the caller stops, the others are not left running.
            await running.CancelAsync();
            // As a plain task: the results are not needed and the failures were already reported.
            await ((Task)Task.WhenAll(tasks)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Classifies one message once a slot is free.
    /// </summary>
    /// <param name="target">The service.</param>
    /// <param name="reference">The message identifiers.</param>
    /// <param name="message">The message, or <see langword="null"/> when it is gone.</param>
    /// <param name="existing">The existing labels.</param>
    /// <param name="slots">Limits the requests running at once and stops them after a general failure.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    /// <exception cref="AiException">The service cannot be used at all.</exception>
    private async Task<MessageClassification> ClassifyAsync(
        AiTarget target,
        MessageReference reference,
        MailMessage? message,
        IReadOnlyList<Label> existing,
        RequestSlots slots,
        CancellationToken cancellationToken)
    {
        if (message is null)
        {
            return new MessageClassification(reference, ClassificationStatus.NoSuggestion, []);
        }

        await slots.Free.WaitAsync(cancellationToken);
        try
        {
            // The caller may not have seen the failure yet, so cancellation alone does not stop the waiting requests.
            // Whichever of the failed tasks it sees first, it gets the same reason.
            if (slots.GeneralFailure is { } earlier)
            {
                throw new AiException(earlier.Failure, earlier.StatusCode);
            }

            var content = AiContentBuilder.Build(
                message, Math.Min(target.Configuration.MaxInputCharacters, MaxBodyCharacters));
            var instructions = AiPrompts.Classify([.. existing.Select(l => l.Name)], content.Tag);
            var answer = new StringBuilder();
            await foreach (var part in answers.AskAsync(target, instructions, content.Text, cancellationToken))
            {
                if (part.Kind == AiOutputKind.Refused)
                {
                    return new MessageClassification(reference, ClassificationStatus.NoSuggestion, []);
                }

                answer.Append(part.Text);
            }

            var (ids, priority) = ClassificationParser.Parse(answer.ToString(), existing);
            var added = ids.Where(id => message.Labels.All(l => l.Id != id)).ToList();
            if (priority == (message.AiPriority ?? MessagePriority.Normal))
            {
                priority = null;
            }

            var status = added.Count > 0 || priority is not null
                ? ClassificationStatus.Suggested
                : ClassificationStatus.NoSuggestion;
            return new MessageClassification(reference, status, added, priority);
        }
        catch (AiException ex) when (ex.Failure is AiFailure.Rejected or AiFailure.Timeout or AiFailure.Unavailable or AiFailure.Unknown)
        {
            // May concern this message only, for example its size; the others are still tried.
            return new MessageClassification(reference, ClassificationStatus.Failed, [], Failure: ex.Failure);
        }
        catch (AiException ex)
        {
            // Concerns every message; remembered before the slot is freed, so no further request is sent.
            slots.GeneralFailure = ex;
            throw;
        }
        finally
        {
            slots.Free.Release();
        }
    }

    /// <summary>
    /// What the requests of one query share.
    /// </summary>
    /// <param name="count">How many requests may run at once.</param>
    private sealed class RequestSlots(int count) : IDisposable
    {
        /// <summary>
        /// The failure that concerns every message, once there is one.
        /// </summary>
        private volatile AiException? _generalFailure;

        /// <summary>
        /// Gets the free slots.
        /// </summary>
        public SemaphoreSlim Free { get; } = new(count);

        /// <summary>
        /// Gets or sets the failure that concerns every message.
        /// </summary>
        public AiException? GeneralFailure
        {
            get => _generalFailure;
            set => _generalFailure = value;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Free.Dispose();
        }
    }
}
