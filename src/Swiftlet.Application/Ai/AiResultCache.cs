namespace Swiftlet.Application.Ai;

/// <summary>
/// What a task does with a message.
/// </summary>
public enum AiMessageTask
{
    /// <summary>
    /// Summarize it.
    /// </summary>
    Summarize,

    /// <summary>
    /// Translate it.
    /// </summary>
    Translate,
}

/// <summary>
/// Everything an answer depends on.
/// </summary>
/// <param name="MessageId">The message.</param>
/// <param name="Task">The task.</param>
/// <param name="Language">The language of the answer.</param>
/// <param name="Settings">The settings the request was made with.</param>
public sealed record AiResultKey(Guid MessageId, AiMessageTask Task, AiLanguage Language, AiConfiguration Settings);

/// <summary>
/// A complete answer.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="IsInputTruncated">Whether only the beginning of the message was sent.</param>
/// <param name="IsOutputTruncated">Whether the answer stopped at the output limit.</param>
public sealed record AiResult(string Text, bool IsInputTruncated, bool IsOutputTruncated);

/// <summary>
/// Keeps recent answers in memory, so asking again costs nothing. They are never stored on disk.
/// </summary>
public sealed class AiResultCache
{
    /// <summary>
    /// How many answers are kept.
    /// </summary>
    public const int Capacity = 20;

    /// <summary>
    /// Guards the entries.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The entries, most recently used first.
    /// </summary>
    private readonly LinkedList<(AiResultKey Key, AiResult Result)> _entries = new();

    /// <summary>
    /// Gets a kept answer.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The answer, or <see langword="null"/>.</returns>
    public AiResult? Get(AiResultKey key)
    {
        lock (_gate)
        {
            if (Find(key) is not { } node)
            {
                return null;
            }

            _entries.Remove(node);
            _entries.AddFirst(node);
            return node.Value.Result;
        }
    }

    /// <summary>
    /// Keeps an answer, dropping the least recently used one when full.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="result">The answer.</param>
    public void Set(AiResultKey key, AiResult result)
    {
        lock (_gate)
        {
            if (Find(key) is { } node)
            {
                _entries.Remove(node);
            }

            _entries.AddFirst((key, result));
            if (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }
    }

    /// <summary>
    /// Finds an entry; call it while holding <see cref="_gate"/>.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The entry, if present.</returns>
    private LinkedListNode<(AiResultKey Key, AiResult Result)>? Find(AiResultKey key)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (node.Value.Key == key)
            {
                return node;
            }
        }

        return null;
    }
}
