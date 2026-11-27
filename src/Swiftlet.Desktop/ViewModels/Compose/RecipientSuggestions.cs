using Swiftlet.Application.Contacts;

namespace Swiftlet.Desktop.ViewModels.Compose;

/// <summary>
/// An address offered while recipients are typed.
/// </summary>
/// <param name="Name">The name; empty when unknown.</param>
/// <param name="Address">The address.</param>
/// <param name="Company">The company; empty when unknown.</param>
public sealed record RecipientSuggestion(string Name, string Address, string Company = "")
{
    /// <summary>
    /// Gets the text of the first line of the suggestion.
    /// </summary>
    public string Title => Name.Length > 0 ? Name : Address;

    /// <summary>
    /// Gets a value indicating whether the address is shown below the title.
    /// </summary>
    public bool HasName => Name.Length > 0;

    /// <inheritdoc />
    public override string ToString() => RecipientList.Format(Name, Address);
}

/// <summary>
/// Chooses the suggestions for a recipient list being typed. Only its last entry is completed.
/// </summary>
public static class RecipientSuggestions
{
    /// <summary>
    /// The most suggestions shown at once.
    /// </summary>
    public const int MaxShown = 8;

    /// <summary>
    /// Lists all suggestions of an address book: every address of every contact first, then the collected addresses.
    /// </summary>
    /// <param name="book">The address book.</param>
    /// <returns>The suggestions in the order they are offered.</returns>
    public static IReadOnlyList<RecipientSuggestion> From(AddressBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        return
        [
            .. book.Contacts.SelectMany(c => c.Emails.Select(e => new RecipientSuggestion(c.DisplayName, e, c.Company))),
            .. book.Collected.Select(a => new RecipientSuggestion(a.Name, a.Address)),
        ];
    }

    /// <summary>
    /// Gets the suggestions whose name, address or company contain the text of the last entry, leaving out addresses the
    /// list already has.
    /// </summary>
    /// <param name="text">The recipient list as typed.</param>
    /// <param name="all">All suggestions in the order they are offered.</param>
    /// <returns>At most <see cref="MaxShown"/> suggestions; none while the last entry is empty.</returns>
    public static IReadOnlyList<RecipientSuggestion> Find(string? text, IEnumerable<RecipientSuggestion> all)
    {
        ArgumentNullException.ThrowIfNull(all);

        var value = text ?? string.Empty;
        var start = RecipientList.LastEntryStart(value);
        var typed = value[start..].Trim().Trim('"', '<', '>');
        if (typed.Length == 0)
        {
            return [];
        }

        var used = RecipientList.Split(value[..start])
            .Select(e => RecipientList.Parse(e).Address)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all
            .Where(s => !used.Contains(s.Address)
                && (Contains(s.Name, typed) || Contains(s.Address, typed) || Contains(s.Company, typed)))
            .Take(MaxShown)
            .ToList();
    }

    /// <summary>
    /// Replaces the last entry of a list with a suggestion and starts the next entry.
    /// </summary>
    /// <param name="text">The recipient list as typed.</param>
    /// <param name="suggestion">The chosen suggestion.</param>
    /// <returns>The new text.</returns>
    public static string Apply(string? text, RecipientSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        var value = text ?? string.Empty;
        var head = value[..RecipientList.LastEntryStart(value)].TrimEnd(' ');
        if (head.Length > 0 && head[^1] is not ('\n' or '\r'))
        {
            head += " ";
        }

        return $"{head}{suggestion}, ";
    }

    /// <summary>
    /// Determines whether a text contains the typed text, ignoring case.
    /// </summary>
    /// <param name="value">The text searched.</param>
    /// <param name="typed">The typed text.</param>
    /// <returns><see langword="true"/> if contained.</returns>
    private static bool Contains(string value, string typed) => value.Contains(typed, StringComparison.CurrentCultureIgnoreCase);
}
