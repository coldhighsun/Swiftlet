using System.Text;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Application.Contacts;

/// <summary>
/// Reads vCard 2.1, 3.0 and 4.0 files and writes version 3.0. Only the fields of <see cref="ContactDetails"/> are used.
/// </summary>
public static class VCardFormat
{
    /// <summary>
    /// The maximum number of bytes of a written line.
    /// </summary>
    private const int MaxLineBytes = 75;

    /// <summary>
    /// Reads the cards of a file.
    /// </summary>
    /// <param name="text">The file text.</param>
    /// <returns>One entry per card, with whatever the card has (possibly no email address).</returns>
    /// <exception cref="FormatException">The text contains no card.</exception>
    public static IReadOnlyList<ContactDetails> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var cards = new List<ContactDetails>();
        CardBuilder? card = null;
        var depth = 0;
        foreach (var line in Unfold(text))
        {
            var colon = FindValueSeparator(line);
            if (colon < 0)
            {
                continue;
            }

            var head = line[..colon].Split(';');
            var name = head[0][(head[0].LastIndexOf('.') + 1)..].Trim().ToUpperInvariant();
            var value = line[(colon + 1)..];

            if (name == "BEGIN" && IsCard(value))
            {
                // A card can contain another one (the AGENT property); only the outer card counts.
                if (depth++ == 0)
                {
                    card = new CardBuilder();
                }
            }
            else if (name == "END" && IsCard(value))
            {
                if (depth > 0 && --depth == 0 && card is not null)
                {
                    cards.Add(card.Build());
                    card = null;
                }
            }
            else if (depth == 1)
            {
                card?.Add(name, DecodeValue(head, value));
            }
        }

        return cards.Count > 0 ? cards : throw new FormatException("The file contains no vCard.");
    }

    /// <summary>
    /// Writes contacts as vCard 3.0.
    /// </summary>
    /// <param name="contacts">The contacts.</param>
    /// <returns>The file text.</returns>
    public static string Write(IEnumerable<ContactDetails> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);

        var result = new StringBuilder();
        foreach (var contact in contacts)
        {
            var name = PersonName.Compose(contact.FirstName, contact.LastName);
            if (name.Length == 0 && contact.Emails.Count > 0)
            {
                name = contact.Emails[0];
            }

            AppendLine(result, "BEGIN:VCARD");
            AppendLine(result, "VERSION:3.0");
            AppendLine(result, $"FN:{Escape(name)}");
            AppendLine(result, $"N:{Escape(contact.LastName)};{Escape(contact.FirstName)};;;");
            foreach (var email in contact.Emails)
            {
                AppendLine(result, $"EMAIL;TYPE=INTERNET:{Escape(email)}");
            }

            foreach (var phone in contact.Phones)
            {
                AppendLine(result, $"TEL:{Escape(phone)}");
            }

            if (contact.Company.Length > 0)
            {
                AppendLine(result, $"ORG:{Escape(contact.Company)}");
            }

            if (contact.Note.Length > 0)
            {
                AppendLine(result, $"NOTE:{Escape(contact.Note)}");
            }

            AppendLine(result, "END:VCARD");
        }

        return result.ToString();
    }

    /// <summary>
    /// Determines whether a BEGIN or END value names a card.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> for VCARD.</returns>
    private static bool IsCard(string value) => value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Joins folded lines and the soft line breaks of quoted-printable values.
    /// </summary>
    /// <param name="text">The file text.</param>
    /// <returns>The logical lines.</returns>
    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var softBreak = false;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (softBreak)
            {
                current.Length--;
                current.Append(line);
            }
            else if (line.Length > 0 && line[0] is ' ' or '\t' && current.Length > 0)
            {
                current.Append(line, 1, line.Length - 1);
            }
            else
            {
                Flush();
                current.Append(line);
            }

            softBreak = current.Length > 0
                && current[^1] == '='
                && current.ToString().Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
        }

        Flush();
        return lines;

        void Flush()
        {
            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>
    /// Finds the colon between the property (with parameters) and its value, ignoring quoted parameter values.
    /// </summary>
    /// <param name="line">The logical line.</param>
    /// <returns>The position, or -1.</returns>
    private static int FindValueSeparator(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Decodes a quoted-printable value with its declared charset; other values are returned unchanged.
    /// </summary>
    /// <param name="head">The property name and parameters.</param>
    /// <param name="value">The raw value.</param>
    /// <returns>The value text, still escaped.</returns>
    private static string DecodeValue(string[] head, string value)
    {
        if (!head.Skip(1).Any(p => p.EndsWith("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)))
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && char.IsAsciiHexDigit(value[i + 1]) && char.IsAsciiHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.FromHexString(value.AsSpan(i + 1, 2))[0]);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value, i, 1));
            }
        }

        var charset = head
            .Skip(1)
            .FirstOrDefault(p => p.StartsWith("CHARSET=", StringComparison.OrdinalIgnoreCase))?["CHARSET=".Length..];
        return FindEncoding(charset).GetString([.. bytes]);
    }

    /// <summary>
    /// Gets the encoding of a charset name.
    /// </summary>
    /// <param name="charset">The name, or <see langword="null"/>.</param>
    /// <returns>The encoding; UTF-8 when the name is missing or unknown.</returns>
    private static Encoding FindEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset) || charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(charset) ?? Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            // An unknown charset name must not fail the whole import.
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Splits a structured value on unescaped semicolons and removes the escapes of each component.
    /// </summary>
    /// <param name="value">The escaped value.</param>
    /// <returns>The trimmed components; at least one.</returns>
    private static List<string> Components(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                current.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else if (value[i] == ';')
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(value[i]);
            }
        }

        parts.Add(current.ToString().Trim());
        return parts;
    }

    /// <summary>
    /// Removes the escapes of a plain text value, keeping semicolons.
    /// </summary>
    /// <param name="value">The escaped value.</param>
    /// <returns>The text.</returns>
    private static string Unescape(string value) => string.Join(';', Components(value)).Trim();

    /// <summary>
    /// Escapes a text value.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The escaped text.</returns>
    private static string Escape(string value)
    {
        return value
            .ReplaceLineEndings("\n")
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Appends a line, folding it without splitting a character.
    /// </summary>
    /// <param name="result">The file being written.</param>
    /// <param name="line">The logical line.</param>
    private static void AppendLine(StringBuilder result, string line)
    {
        var bytes = 0;
        Span<char> buffer = stackalloc char[2];
        foreach (var rune in line.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxLineBytes)
            {
                result.Append("\r\n ");
                bytes = 1;
            }

            var length = rune.EncodeToUtf16(buffer);
            result.Append(buffer[..length]);
            bytes += rune.Utf8SequenceLength;
        }

        result.Append("\r\n");
    }

    /// <summary>
    /// Collects the properties of one card.
    /// </summary>
    private sealed class CardBuilder
    {
        /// <summary>
        /// The email addresses.
        /// </summary>
        private readonly List<string> _emails = [];

        /// <summary>
        /// The phone numbers.
        /// </summary>
        private readonly List<string> _phones = [];

        /// <summary>
        /// The formatted name (FN).
        /// </summary>
        private string _formattedName = string.Empty;

        /// <summary>
        /// The given name of the structured name (N), followed by any additional names.
        /// </summary>
        private string _firstName = string.Empty;

        /// <summary>
        /// The family name of the structured name (N).
        /// </summary>
        private string _lastName = string.Empty;

        /// <summary>
        /// The organization.
        /// </summary>
        private string _company = string.Empty;

        /// <summary>
        /// The note.
        /// </summary>
        private string _note = string.Empty;

        /// <summary>
        /// Takes over one property.
        /// </summary>
        /// <param name="name">The upper-case property name.</param>
        /// <param name="value">The escaped value.</param>
        public void Add(string name, string value)
        {
            switch (name)
            {
                case "FN":
                    _formattedName = Unescape(value);
                    break;
                case "N":
                    var parts = Components(value);
                    _lastName = Part(parts, 0);
                    _firstName = JoinGivenNames(Part(parts, 1), Part(parts, 2));
                    break;
                case "EMAIL":
                    _emails.Add(Unescape(value));
                    break;
                case "TEL":
                    var phone = Unescape(value);
                    _phones.Add(phone.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? phone[4..] : phone);
                    break;
                case "ORG":
                    _company = Components(value)[0];
                    break;
                case "NOTE":
                    _note = Unescape(value);
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// Creates the details of the card.
        /// </summary>
        /// <returns>The details.</returns>
        public ContactDetails Build()
        {
            var (firstName, lastName) = (_firstName, _lastName);
            if (firstName.Length == 0 && lastName.Length == 0 && !_emails.Contains(_formattedName, StringComparer.OrdinalIgnoreCase))
            {
                // Without a structured name the parts are guessed; a card without a name of its own often repeats the
                // address as the formatted name, which is not a name.
                (firstName, lastName) = PersonName.Split(_formattedName);
            }

            return new ContactDetails(firstName, lastName, _emails, _phones, _company, _note);
        }

        /// <summary>
        /// Joins the given and additional names.
        /// </summary>
        /// <param name="given">The given name.</param>
        /// <param name="additional">The additional (middle) name.</param>
        /// <returns>The joined names.</returns>
        private static string JoinGivenNames(string given, string additional)
        {
            return string.Join(' ', new[] { given, additional }.Where(p => p.Length > 0));
        }

        /// <summary>
        /// Gets a component of a structured value.
        /// </summary>
        /// <param name="parts">The components.</param>
        /// <param name="index">The wanted position.</param>
        /// <returns>The component, or an empty text.</returns>
        private static string Part(List<string> parts, int index) => index < parts.Count ? parts[index] : string.Empty;
    }
}
