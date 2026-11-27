using Microsoft.Extensions.Logging;

namespace Swiftlet.Application.Tests;

/// <summary>
/// A logger that keeps what was written so tests can assert on it.
/// </summary>
/// <typeparam name="T">The category type.</typeparam>
internal sealed class ListLogger<T> : ILogger<T>
{
    /// <summary>
    /// Gets the entries written so far.
    /// </summary>
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    /// <summary>
    /// Gets the account and folder tags in effect when each entry was written, in the order of <see cref="Entries"/>.
    /// </summary>
    public List<string> Tags { get; } = [];

    /// <summary>
    /// Gets the open scopes, the innermost on top.
    /// </summary>
    private readonly Stack<string> _scopes = new();

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        var values = state as IEnumerable<KeyValuePair<string, object>> ?? [];
        _scopes.Push(string.Concat(values.Select(v => v.Value)));
        return new Scope(_scopes);
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception), exception));
        Tags.Add(_scopes.TryPeek(out var tags) ? tags : string.Empty);
    }

    /// <summary>
    /// Closes the innermost scope when disposed.
    /// </summary>
    /// <param name="scopes">The open scopes.</param>
    private sealed class Scope(Stack<string> scopes) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose()
        {
            scopes.Pop();
        }
    }
}
