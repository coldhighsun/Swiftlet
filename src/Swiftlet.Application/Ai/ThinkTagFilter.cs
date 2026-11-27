using System.Text;

namespace Swiftlet.Application.Ai;

/// <summary>
/// Removes the reasoning some local models write at the start of their answer between <c>&lt;think&gt;</c> tags. Only a
/// block opening the answer is removed, so the same text quoted later, for example in a translation, is kept. The
/// answer arrives in pieces that may split a tag.
/// </summary>
public sealed class ThinkTagFilter
{
    /// <summary>
    /// The opening tag.
    /// </summary>
    private const string Open = "<think>";

    /// <summary>
    /// The closing tag.
    /// </summary>
    private const string Close = "</think>";

    /// <summary>
    /// The text held back until it is known what it is.
    /// </summary>
    private readonly StringBuilder _held = new();

    /// <summary>
    /// Where the filter is in the answer.
    /// </summary>
    private State _state = State.Start;

    /// <summary>
    /// The parts of an answer.
    /// </summary>
    private enum State
    {
        /// <summary>
        /// Nothing visible yet; the answer may still turn out to open with reasoning.
        /// </summary>
        Start,

        /// <summary>
        /// Inside the reasoning.
        /// </summary>
        Reasoning,

        /// <summary>
        /// After the reasoning, before the first visible character.
        /// </summary>
        AfterReasoning,

        /// <summary>
        /// In the answer proper.
        /// </summary>
        Answer,
    }

    /// <summary>
    /// Takes the next piece of the answer.
    /// </summary>
    /// <param name="piece">The piece.</param>
    /// <returns>The text that can be shown now; empty if none.</returns>
    public string Push(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        if (_state == State.Answer)
        {
            return piece;
        }

        _held.Append(piece);
        if (_state == State.Start)
        {
            var start = _held.ToString().TrimStart();
            if (start.Length < Open.Length && Open.StartsWith(start, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (!start.StartsWith(Open, StringComparison.Ordinal))
            {
                return Release(_held.ToString());
            }

            _held.Clear().Append(start.AsSpan(Open.Length));
            _state = State.Reasoning;
        }

        if (_state == State.Reasoning)
        {
            var text = _held.ToString();
            var end = text.IndexOf(Close, StringComparison.Ordinal);
            if (end < 0)
            {
                // Only the tail can still become the closing tag.
                if (_held.Length >= Close.Length)
                {
                    _held.Remove(0, _held.Length - Close.Length + 1);
                }

                return string.Empty;
            }

            _held.Clear().Append(text.AsSpan(end + Close.Length));
            _state = State.AfterReasoning;
        }

        var visible = _held.ToString().TrimStart();
        if (visible.Length == 0)
        {
            _held.Clear();
            return string.Empty;
        }

        return Release(visible);
    }

    /// <summary>
    /// Returns what is still held back once the answer is complete. Unfinished reasoning is dropped.
    /// </summary>
    /// <returns>The remaining text.</returns>
    public string Flush()
    {
        return _state == State.Start ? Release(_held.ToString()) : string.Empty;
    }

    /// <summary>
    /// Switches to the answer proper and hands out a text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The same text.</returns>
    private string Release(string text)
    {
        _state = State.Answer;
        _held.Clear();
        return text;
    }
}
