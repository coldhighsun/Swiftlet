namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Remembers the last observed message count and UIDNEXT per folder, so changes that arrive outside an IDLE wait are not missed.
/// </summary>
internal sealed class FolderChangeTracker
{
    /// <summary>
    /// The last observed state per folder full name.
    /// </summary>
    private readonly Dictionary<string, (int Count, uint UidNext)> _baselines = new(StringComparer.Ordinal);

    /// <summary>
    /// Records the current state of a folder and reports whether it differs from the previous observation.
    /// The first observation of a folder counts as a change, because nothing is known about what happened before it.
    /// UIDNEXT is compared only when both the baseline and the current value are known (not 0), so a baseline recorded
    /// after an IDLE (where MailKit does not refresh UIDNEXT) is compared by message count only, once.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="count">The current message count.</param>
    /// <param name="uidNext">The current UIDNEXT, or 0 when unknown.</param>
    /// <returns><see langword="true"/> on the first observation of the folder or when it differs from the previous one.</returns>
    public bool Observe(string folderFullName, int count, uint uidNext)
    {
        var changed = !_baselines.TryGetValue(folderFullName, out var previous)
            || previous.Count != count
            || (previous.UidNext != 0 && uidNext != 0 && previous.UidNext != uidNext);
        _baselines[folderFullName] = (count, uidNext);
        return changed;
    }
}
