using System.Collections.Concurrent;

namespace Swiftlet.Application.Logging;

/// <summary>
/// Remembers the readable names behind identifiers, so log lines can name the mailbox, folder and label they are about.
/// It is filled by the repositories as they read and add rows.
/// </summary>
public sealed class LogNames
{
    /// <summary>
    /// The number of identifier characters shown for something whose name is not known.
    /// </summary>
    private const int ShortIdLength = 8;

    /// <summary>
    /// The email addresses by account identifier.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _accounts = new();

    /// <summary>
    /// The full folder names by folder identifier.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _folders = new();

    /// <summary>
    /// The label names by label identifier.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _labels = new();

    /// <summary>
    /// Remembers the email address of an account.
    /// </summary>
    /// <param name="id">The account identifier.</param>
    /// <param name="emailAddress">The email address.</param>
    public void RememberAccount(Guid id, string emailAddress)
    {
        _accounts[id] = emailAddress;
    }

    /// <summary>
    /// Remembers the full name of a folder.
    /// </summary>
    /// <param name="id">The folder identifier.</param>
    /// <param name="fullName">The full folder name.</param>
    public void RememberFolder(Guid id, string fullName)
    {
        _folders[id] = fullName;
    }

    /// <summary>
    /// Remembers the name of a label.
    /// </summary>
    /// <param name="id">The label identifier.</param>
    /// <param name="name">The label name.</param>
    public void RememberLabel(Guid id, string name)
    {
        _labels[id] = name;
    }

    /// <summary>
    /// Names an account.
    /// </summary>
    /// <param name="id">The account identifier.</param>
    /// <returns>The email address, or the start of the identifier if it is not known.</returns>
    public string Account(Guid id)
    {
        return _accounts.TryGetValue(id, out var name) ? name : ShortId(id);
    }

    /// <summary>
    /// Names a folder.
    /// </summary>
    /// <param name="id">The folder identifier.</param>
    /// <returns>The full folder name, or the start of the identifier if it is not known.</returns>
    public string Folder(Guid id)
    {
        return _folders.TryGetValue(id, out var name) ? name : ShortId(id);
    }

    /// <summary>
    /// Names a label.
    /// </summary>
    /// <param name="id">The label identifier.</param>
    /// <returns>The label name, or the start of the identifier if it is not known.</returns>
    public string Label(Guid id)
    {
        return _labels.TryGetValue(id, out var name) ? name : ShortId(id);
    }

    /// <summary>
    /// Shortens an identifier for display.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <returns>Its first characters.</returns>
    private static string ShortId(Guid id)
    {
        return id.ToString("N")[..ShortIdLength];
    }
}
