using System.Text;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Application.Contacts;

/// <summary>
/// Reads and writes contacts as CSV, recognizing the column names of Outlook and Google exports in English and Chinese.
/// </summary>
public static class CsvContactFormat
{
    /// <summary>
    /// Separates several values in one cell of a Google export.
    /// </summary>
    private const string GoogleValueSeparator = " ::: ";

    /// <summary>
    /// Characters that make a spreadsheet program treat a cell as a formula and that a name or note should not start with.
    /// </summary>
    private static readonly char[] FormulaStarts = ['=', '@', '\t', '\r'];

    /// <summary>
    /// The cell delimiters that are recognized.
    /// </summary>
    private static readonly char[] Delimiters = [',', ';', '\t'];

    /// <summary>
    /// Column names of a full name.
    /// </summary>
    private static readonly string[] NameColumns = ["name", "display name", "full name", "姓名", "显示名称", "名称"];

    /// <summary>
    /// Column names of a given name.
    /// </summary>
    private static readonly string[] FirstNameColumns = ["first name", "given name", "名", "名字"];

    /// <summary>
    /// Column names of a middle name.
    /// </summary>
    private static readonly string[] MiddleNameColumns = ["middle name", "additional name", "中间名"];

    /// <summary>
    /// Column names of a family name.
    /// </summary>
    private static readonly string[] LastNameColumns = ["last name", "family name", "surname", "姓", "姓氏"];

    /// <summary>
    /// Column names of a company.
    /// </summary>
    private static readonly string[] CompanyColumns =
        ["company", "organization", "organization name", "organization 1 - name", "公司", "单位"];

    /// <summary>
    /// Column names of a note.
    /// </summary>
    private static readonly string[] NoteColumns = ["notes", "note", "备注", "附注"];

    /// <summary>
    /// Reads the rows of a file whose first row names the columns.
    /// </summary>
    /// <param name="text">The file text.</param>
    /// <returns>One entry per non-empty row, with whatever the row has (possibly no email address).</returns>
    /// <exception cref="FormatException">No column holds email addresses.</exception>
    public static IReadOnlyList<ContactDetails> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var rows = ParseRows(text, DetectDelimiter(text));
        if (rows.Count == 0)
        {
            throw new FormatException("The file is empty.");
        }

        var headers = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var emails = Columns(headers, h => ContainsAny(h, "mail", "邮件", "邮箱") && !IsDescriptionColumn(h));
        if (emails.Count == 0)
        {
            throw new FormatException("The file has no email column.");
        }

        var phones = Columns(headers, h => ContainsAny(h, "phone", "mobile", "电话", "手机") && !IsDescriptionColumn(h));
        var names = Columns(headers, NameColumns.Contains);
        var first = Columns(headers, FirstNameColumns.Contains);
        var middle = Columns(headers, MiddleNameColumns.Contains);
        var last = Columns(headers, LastNameColumns.Contains);
        var companies = Columns(headers, CompanyColumns.Contains);
        var notes = Columns(headers, NoteColumns.Contains);

        var result = new List<ContactDetails>();
        foreach (var row in rows.Skip(1))
        {
            if (row.TrueForAll(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var firstName = string.Join(' ', new[] { First(row, first), First(row, middle) }.Where(p => p.Length > 0));
            var lastName = First(row, last);
            if (firstName.Length == 0 && lastName.Length == 0)
            {
                (firstName, lastName) = PersonName.Split(First(row, names));
            }

            result.Add(new ContactDetails(
                firstName, lastName, All(row, emails), All(row, phones), First(row, companies), First(row, notes)));
        }

        return result;
    }

    /// <summary>
    /// Writes contacts as comma-separated text.
    /// </summary>
    /// <param name="contacts">The contacts.</param>
    /// <returns>The file text, without byte order mark.</returns>
    public static string Write(IReadOnlyCollection<ContactDetails> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);

        var emailCount = Math.Max(1, contacts.Select(c => c.Emails.Count).DefaultIfEmpty().Max());
        var phoneCount = Math.Max(1, contacts.Select(c => c.Phones.Count).DefaultIfEmpty().Max());
        var result = new StringBuilder();

        AppendRow(
            result,
            [
                "First Name",
                "Last Name",
                .. Enumerable.Range(1, emailCount).Select(i => i == 1 ? "E-mail Address" : $"E-mail {i} Address"),
                .. Enumerable.Range(1, phoneCount).Select(i => i == 1 ? "Phone" : $"Phone {i}"),
                "Company",
                "Notes",
            ]);
        foreach (var contact in contacts)
        {
            AppendRow(
                result,
                [
                    contact.FirstName,
                    contact.LastName,
                    .. Enumerable.Range(0, emailCount).Select(i => i < contact.Emails.Count ? contact.Emails[i] : string.Empty),
                    .. Enumerable.Range(0, phoneCount).Select(i => i < contact.Phones.Count ? contact.Phones[i] : string.Empty),
                    contact.Company,
                    contact.Note,
                ]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Determines whether a column describes another column (its type or label) instead of holding a value.
    /// </summary>
    /// <param name="header">The lower-case column name.</param>
    /// <returns><see langword="true"/> for descriptive columns.</returns>
    private static bool IsDescriptionColumn(string header)
    {
        return ContainsAny(header, "type", "label", "display name", "phonetic", "类型", "显示名称", "标签");
    }

    /// <summary>
    /// Determines whether a text contains any of the given parts.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="parts">The parts looked for.</param>
    /// <returns><see langword="true"/> if one is contained.</returns>
    private static bool ContainsAny(string value, params ReadOnlySpan<string> parts)
    {
        foreach (var part in parts)
        {
            if (value.Contains(part, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the positions of the columns matching a rule.
    /// </summary>
    /// <param name="headers">The column names.</param>
    /// <param name="matches">The rule.</param>
    /// <returns>The positions.</returns>
    private static List<int> Columns(List<string> headers, Func<string, bool> matches)
    {
        return Enumerable.Range(0, headers.Count).Where(i => matches(headers[i])).ToList();
    }

    /// <summary>
    /// Gets all values of a row in the given columns.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="columns">The column positions.</param>
    /// <returns>The non-empty values.</returns>
    private static List<string> All(List<string> row, List<int> columns)
    {
        return columns
            .Where(i => i < row.Count)
            .SelectMany(i => row[i].Split(GoogleValueSeparator, StringSplitOptions.None))
            .Select(Unguard)
            .Where(v => v.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Gets the first value of a row in the given columns.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="columns">The column positions.</param>
    /// <returns>The value, or an empty text.</returns>
    private static string First(List<string> row, List<int> columns)
    {
        var values = All(row, columns);
        return values.Count > 0 ? values[0] : string.Empty;
    }

    /// <summary>
    /// Removes the apostrophe added on export in front of a formula-like value, and surrounding white space.
    /// </summary>
    /// <param name="value">The cell value.</param>
    /// <returns>The value.</returns>
    private static string Unguard(string value)
    {
        var text = value.Length > 1 && value[0] == '\'' && FormulaStarts.Contains(value[1]) ? value[1..] : value;
        return text.Trim();
    }

    /// <summary>
    /// Appends one quoted row.
    /// </summary>
    /// <param name="result">The file being written.</param>
    /// <param name="cells">The cell values.</param>
    private static void AppendRow(StringBuilder result, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            var value = cells[i];
            if (value.Length > 0 && FormulaStarts.Contains(value[0]))
            {
                value = $"'{value}";
            }

            if (i > 0)
            {
                result.Append(',');
            }

            result.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        result.Append("\r\n");
    }

    /// <summary>
    /// Picks the delimiter that occurs most often in the first line.
    /// </summary>
    /// <param name="text">The file text.</param>
    /// <returns>A comma, semicolon or tab.</returns>
    private static char DetectDelimiter(string text)
    {
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        var firstLine = end < 0 ? text : text[..end];
        return Delimiters.MaxBy(d => firstLine.Count(c => c == d));
    }

    /// <summary>
    /// Splits the text into rows of cells; quoted cells may contain delimiters, line breaks and doubled quotes.
    /// </summary>
    /// <param name="text">The file text.</param>
    /// <param name="delimiter">The cell delimiter.</param>
    /// <returns>The rows.</returns>
    private static List<List<string>> ParseRows(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                EndCell();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRow();
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            EndRow();
        }

        return rows;

        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
        }

        void EndRow()
        {
            EndCell();
            rows.Add(row);
            row = [];
        }
    }
}
