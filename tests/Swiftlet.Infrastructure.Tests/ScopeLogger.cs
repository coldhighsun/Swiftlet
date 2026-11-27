using Microsoft.Extensions.Logging;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// A logger that keeps what was written together with the account and folder tags in effect, following the async flow like
/// the real logger does.
/// </summary>
/// <typeparam name="T">The category type.</typeparam>
internal sealed class ScopeLogger<T> : ILogger<T>
{
    /// <summary>
    /// The tags of the current async flow.
    /// </summary>
    private readonly AsyncLocal<string?> _tags = new();

    /// <summary>
    /// Gets the entries written so far.
    /// </summary>
    public List<(string Message, string Tags)> Entries { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        var previous = _tags.Value;
        var values = state as IEnumerable<KeyValuePair<string, object>> ?? [];
        _tags.Value = string.Concat(values.Select(v => v.Value));
        return new Scope(_tags, previous);
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((formatter(state, exception), _tags.Value ?? string.Empty));
        }
    }

    /// <summary>
    /// Restores the previous tags when disposed.
    /// </summary>
    /// <param name="tags">The tags of the flow.</param>
    /// <param name="previous">The value to restore.</param>
    private sealed class Scope(AsyncLocal<string?> tags, string? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose()
        {
            tags.Value = previous;
        }
    }
}
