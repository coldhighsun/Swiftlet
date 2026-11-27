using System.Buffers;
using System.Text;

namespace Swiftlet.Application.Contacts;

/// <summary>
/// One entry of a recipient list.
/// </summary>
/// <param name="Name">The display name; empty when the entry is a bare address.</param>
/// <param name="Address">The address.</param>
public readonly record struct Recipient(string Name, string Address);

/// <summary>
/// Reads and writes recipient lists as typed by users, for example <c>"Doe, John" &lt;john@example.com&gt;; a@b.com</c>.
/// </summary>
public static class RecipientList
{
    /// <summary>
    /// Characters that require a display name to be quoted.
    /// </summary>
    private static readonly SearchValues<char> SpecialNameCharacters = SearchValues.Create(",;，；<>\"@()[]:\\.");

    /// <summary>
    /// Splits a recipient list on commas, semicolons and line breaks, except inside a quoted display name or between
    /// angle brackets.
    /// </summary>
    /// <param name="text">The list.</param>
    /// <returns>The trimmed, non-empty entries.</returns>
    public static IReadOnlyList<string> Split(string? text)
    {
        var value = text ?? string.Empty;
        var entries = new List<string>();
        var start = 0;
        foreach (var separator in FindSeparators(value))
        {
            Add(entries, value[start..separator]);
            start = separator + 1;
        }

        Add(entries, value[start..]);
        return entries;
    }

    /// <summary>
    /// Gets the position at which the last (possibly unfinished or empty) entry of a list starts.
    /// </summary>
    /// <param name="text">The list.</param>
    /// <returns>The index just after the last separator, or 0.</returns>
    public static int LastEntryStart(string? text)
    {
        var last = -1;
        foreach (var separator in FindSeparators(text ?? string.Empty))
        {
            last = separator;
        }

        return last + 1;
    }

    /// <summary>
    /// Separates the display name and the address of one entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The name and address.</returns>
    public static Recipient Parse(string entry)
    {
        var value = (entry ?? string.Empty).Trim();
        var open = value.LastIndexOf('<');
        if (open < 0 || !value.EndsWith('>'))
        {
            return new Recipient(string.Empty, value);
        }

        var name = value[..open].Trim();
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
        {
            name = Unescape(name[1..^1]);
        }

        return new Recipient(name, value[(open + 1)..^1].Trim());
    }

    /// <summary>
    /// Writes one entry, quoting the display name when it needs to be.
    /// </summary>
    /// <param name="name">The display name; may be empty.</param>
    /// <param name="address">The address.</param>
    /// <returns>The entry.</returns>
    public static string Format(string? name, string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var trimmed = (name ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, address, StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        if (trimmed.AsSpan().ContainsAny(SpecialNameCharacters))
        {
            var escaped = trimmed.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            trimmed = $"\"{escaped}\"";
        }

        return $"{trimmed} <{address}>";
    }

    /// <summary>
    /// Finds the positions of the characters that separate entries.
    /// </summary>
    /// <param name="text">The list.</param>
    /// <returns>The separator positions in order.</returns>
    private static IEnumerable<int> FindSeparators(string text)
    {
        var quoted = false;
        var bracketed = false;
        var entryHasText = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                quoted = bracketed = entryHasText = false;
                yield return i;
                continue;
            }

            if (quoted)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }

                continue;
            }

            if (c is ',' or ';' or '，' or '；' && !bracketed)
            {
                entryHasText = false;
                yield return i;
                continue;
            }

            // Only a quote that opens an entry starts a quoted name; elsewhere it is an ordinary character.
            if (c == '"' && !entryHasText)
            {
                quoted = true;
            }
            else if (c == '<')
            {
                bracketed = true;
            }
            else if (c == '>')
            {
                bracketed = false;
            }

            entryHasText |= !char.IsWhiteSpace(c);
        }
    }

    /// <summary>
    /// Adds a trimmed entry unless it is empty.
    /// </summary>
    /// <param name="entries">The list to add to.</param>
    /// <param name="entry">The raw entry.</param>
    private static void Add(List<string> entries, string entry)
    {
        var trimmed = entry.Trim();
        if (trimmed.Length > 0)
        {
            entries.Add(trimmed);
        }
    }

    /// <summary>
    /// Removes the backslash escapes of a quoted name.
    /// </summary>
    /// <param name="value">The text between the quotes.</param>
    /// <returns>The plain text.</returns>
    private static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
            }

            result.Append(value[i]);
        }

        return result.ToString();
    }
}
