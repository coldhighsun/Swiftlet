namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// The maintenance work an account database still owes, stored in its <c>PRAGMA user_version</c> so it survives restarts.
/// </summary>
[Flags]
internal enum AccountDatabaseTasks
{
    /// <summary>
    /// Nothing is pending.
    /// </summary>
    None = 0,

    /// <summary>
    /// HTML bodies stored as plain text by an earlier version still need compressing.
    /// </summary>
    CompressHtml = 1,

    /// <summary>
    /// The full-text index needs rebuilding and the file compacting, whatever the share of free pages is.
    /// </summary>
    Compact = 2,
}
