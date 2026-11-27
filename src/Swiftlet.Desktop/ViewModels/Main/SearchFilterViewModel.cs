using CommunityToolkit.Mvvm.ComponentModel;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// Where a search looks; the values are the positions of the entries of the scope list.
/// </summary>
public enum SearchScope
{
    /// <summary>
    /// The account the tree selection belongs to.
    /// </summary>
    CurrentAccount = 0,

    /// <summary>
    /// The selected folder.
    /// </summary>
    CurrentFolder = 1,

    /// <summary>
    /// Every account.
    /// </summary>
    AllAccounts = 2,
}

/// <summary>
/// The additional search conditions below the search box, hidden until the user asks for them.
/// </summary>
public sealed partial class SearchFilterViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SearchFilterViewModel"/> class without conditions.
    /// </summary>
    public SearchFilterViewModel()
    {
        Reset(null);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the conditions are shown.
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>
    /// Gets or sets the sender condition.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial string From { get; set; }

    /// <summary>
    /// Gets or sets the recipient condition.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial string To { get; set; }

    /// <summary>
    /// Gets or sets the subject condition.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial string Subject { get; set; }

    /// <summary>
    /// Gets or sets the first day searched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters), nameof(HasInvalidDateRange))]
    public partial DateOnly? DateFrom { get; set; }

    /// <summary>
    /// Gets or sets the last day searched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters), nameof(HasInvalidDateRange))]
    public partial DateOnly? DateTo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only messages with attachments are searched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial bool HasAttachments { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only unread messages are searched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial bool UnreadOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only starred messages are searched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial bool FlaggedOnly { get; set; }

    /// <summary>
    /// Gets or sets the position of the chosen entry of the scope list, a <see cref="SearchScope"/> value.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial int ScopeIndex { get; set; }

    /// <summary>
    /// Gets a value indicating whether the tree selection belongs to an account that can be searched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial bool CanSearchAccount { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the tree selection is a folder that can be searched.
    /// </summary>
    [ObservableProperty]
    public partial bool CanSearchFolder { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the first day is after the last one.
    /// </summary>
    public bool HasInvalidDateRange => DateFrom > DateTo;

    /// <summary>
    /// Gets a value indicating whether a condition or a scope other than the default is set, so hidden conditions can be
    /// pointed out.
    /// </summary>
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(From)
        || !string.IsNullOrWhiteSpace(To)
        || !string.IsNullOrWhiteSpace(Subject)
        || DateFrom is not null
        || DateTo is not null
        || HasAttachments
        || UnreadOnly
        || FlaggedOnly
        || ScopeIndex != (int)DefaultScope;

    /// <summary>
    /// Gets the scope used when the user chose none.
    /// </summary>
    private SearchScope DefaultScope => CanSearchAccount ? SearchScope.CurrentAccount : SearchScope.AllAccounts;

    /// <summary>
    /// Removes every condition and offers the scopes of a tree selection; whether the conditions are shown is kept.
    /// </summary>
    /// <param name="node">The selected tree node.</param>
    public void Reset(object? node)
    {
        From = string.Empty;
        To = string.Empty;
        Subject = string.Empty;
        DateFrom = null;
        DateTo = null;
        HasAttachments = false;
        UnreadOnly = false;
        FlaggedOnly = false;
        CanSearchAccount = node is FolderNodeViewModel or AccountNodeViewModel;
        CanSearchFolder = node is FolderNodeViewModel;
        ScopeIndex = (int)DefaultScope;
    }

    /// <summary>
    /// Combines the search text and the entered conditions.
    /// </summary>
    /// <param name="text">The text of the search box.</param>
    /// <param name="node">The selected tree node, which the scope refers to.</param>
    /// <param name="timeZone">The time zone the entered days are in.</param>
    /// <returns>The criteria.</returns>
    public MessageSearchCriteria BuildCriteria(string text, object? node, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(timeZone);
        var scope = (SearchScope)ScopeIndex;
        var folder = node as FolderNodeViewModel;
        var accountId = scope == SearchScope.AllAccounts
            ? null
            : folder?.Account.Id ?? (node as AccountNodeViewModel)?.Account.Id;
        return new MessageSearchCriteria
        {
            Text = text.Trim(),
            From = From.Trim(),
            To = To.Trim(),
            Subject = Subject.Trim(),
            SentFrom = StartOfDay(DateFrom, timeZone),
            // The last day is included, so the search ends where the next day starts.
            SentBefore = DateTo is { } last && last < DateOnly.MaxValue ? StartOfDay(last.AddDays(1), timeZone) : null,
            HasAttachments = HasAttachments,
            UnreadOnly = UnreadOnly,
            FlaggedOnly = FlaggedOnly,
            AccountId = accountId,
            FolderId = scope == SearchScope.CurrentFolder ? folder?.Folder.Id : null,
        };
    }

    /// <summary>
    /// Gets the instant a day starts.
    /// </summary>
    /// <param name="day">The day.</param>
    /// <param name="timeZone">The time zone of the day.</param>
    /// <returns>The instant, or <see langword="null"/> without a day or for the first day there is, which limits nothing.</returns>
    private static DateTimeOffset? StartOfDay(DateOnly? day, TimeZoneInfo timeZone)
    {
        if (day is not { } value || value == DateOnly.MinValue)
        {
            return null;
        }

        var midnight = value.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, timeZone.GetUtcOffset(midnight));
    }
}
