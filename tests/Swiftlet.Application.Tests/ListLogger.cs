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

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
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
    }
}
