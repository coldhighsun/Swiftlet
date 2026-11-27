using Avalonia.Controls;
using Swiftlet.Desktop.ViewModels.Compose;

namespace Swiftlet.Desktop.Controls;

/// <summary>
/// A text box for a list of recipients that suggests addresses for the entry being typed at the end of the list.
/// </summary>
public sealed class RecipientBox : AutoCompleteBox
{
    /// <summary>
    /// The text the current matches were computed for.
    /// </summary>
    private string? _matchedText;

    /// <summary>
    /// The suggestions matching <see cref="_matchedText"/>.
    /// </summary>
    private HashSet<object> _matches = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="RecipientBox"/> class.
    /// </summary>
    public RecipientBox()
    {
        FilterMode = AutoCompleteFilterMode.Custom;
        ItemFilter = IsMatch;
        ItemSelector = (text, item) => item is RecipientSuggestion suggestion
            ? RecipientSuggestions.Apply(text, suggestion)
            : text ?? string.Empty;
        MinimumPrefixLength = 1;
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(AutoCompleteBox);

    /// <inheritdoc />
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        // New suggestions make the remembered matches stale; forget them before the base class filters again.
        if (change.Property == ItemsSourceProperty)
        {
            _matchedText = null;
        }

        base.OnPropertyChanged(change);
    }

    /// <summary>
    /// Decides whether a suggestion is shown for the typed text.
    /// </summary>
    /// <param name="search">The whole text of the box.</param>
    /// <param name="item">The suggestion.</param>
    /// <returns><see langword="true"/> if it is shown.</returns>
    private bool IsMatch(string? search, object? item)
    {
        if (item is null)
        {
            return false;
        }

        if (_matchedText != search)
        {
            _matchedText = search;
            _matches = [.. RecipientSuggestions.Find(search, ItemsSource?.OfType<RecipientSuggestion>() ?? [])];
        }

        return _matches.Contains(item);
    }
}
