using System.Text;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.Formatting;

/// <summary>
/// Builds the text shown when the pointer rests on the tray icon.
/// </summary>
public static class TraySummary
{
    /// <summary>
    /// The longest tooltip Windows shows; longer texts are cut off by the system.
    /// </summary>
    public const int MaxLength = 127;

    /// <summary>
    /// Describes the mailboxes: the unread total first, then one line per account. Accounts that no longer fit are
    /// summed up in a last line.
    /// </summary>
    /// <param name="accounts">The address and unread inbox count of every account, in the order shown.</param>
    /// <param name="localizer">The localizer.</param>
    /// <returns>The text, at most <see cref="MaxLength"/> characters.</returns>
    public static string Format(IReadOnlyList<(string Address, int Unread)> accounts, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(localizer);

        if (accounts.Count == 0)
        {
            return Truncate(localizer["Tray_SummaryNoAccounts"]);
        }

        var total = accounts.Sum(a => a.Unread);
        var text = new StringBuilder(total > 0
            ? localizer.Format("Tray_SummaryUnread", total)
            : localizer["Tray_SummaryNoUnread"]);
        for (var i = 0; i < accounts.Count; i++)
        {
            var line = localizer.Format("Tray_SummaryAccount", accounts[i].Address, accounts[i].Unread);
            var after = accounts.Count - i - 1;

            // Room is kept for the line that counts the accounts left out, in case the next one does not fit.
            var reserved = after > 0 ? 1 + localizer.Format("Tray_SummaryMore", after).Length : 0;
            if (text.Length + 1 + line.Length + reserved > MaxLength)
            {
                text.Append('\n').Append(localizer.Format("Tray_SummaryMore", accounts.Count - i));
                break;
            }

            text.Append('\n').Append(line);
        }

        return Truncate(text.ToString());
    }

    /// <summary>
    /// Cuts a text to the longest tooltip.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text, at most <see cref="MaxLength"/> characters.</returns>
    private static string Truncate(string text)
    {
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }
}
