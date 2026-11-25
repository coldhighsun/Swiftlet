using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// An in-memory <see cref="ISecretStore"/>.
/// </summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    /// <summary>
    /// Gets the stored secrets.
    /// </summary>
    public Dictionary<string, string> Values { get; } = [];

    /// <inheritdoc />
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Values.GetValueOrDefault(key));
    }

    /// <inheritdoc />
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A scriptable <see cref="IImapSession"/> that mimics server paging semantics.
/// </summary>
internal sealed class FakeImapSession : IImapSession
{
    /// <summary>
    /// Gets the folders reported by the fake server.
    /// </summary>
    public List<RemoteFolder> Folders { get; } = [];

    /// <summary>
    /// Gets the UIDVALIDITY per folder full name.
    /// </summary>
    public Dictionary<string, uint> UidValidity { get; } = [];

    /// <summary>
    /// Gets the messages per folder full name.
    /// </summary>
    public Dictionary<string, List<RemoteMessage>> Messages { get; } = [];

    /// <summary>
    /// Gets the folders whose fetch should fail.
    /// </summary>
    public HashSet<string> FailingFolders { get; } = [];

    /// <summary>
    /// Gets the recorded fetch calls.
    /// </summary>
    public List<(string Folder, uint AfterUid, int MaxCount)> FetchCalls { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the session was disposed.
    /// </summary>
    public bool Disposed { get; set; }

    /// <summary>
    /// Gets the messages appended to Sent.
    /// </summary>
    public List<byte[]> AppendedToSent { get; } = [];

    /// <summary>
    /// Gets or sets a callback run inside <see cref="ListFoldersAsync"/>, to simulate concurrent changes before any folder is written.
    /// </summary>
    public Action? OnListFolders { get; set; }

    /// <summary>
    /// Gets or sets a callback run inside <see cref="FetchNewMessagesAsync"/> with the folder name, before the messages are returned.
    /// </summary>
    public Action<string>? OnFetchNew { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        OnListFolders?.Invoke();
        return Task.FromResult<IReadOnlyList<RemoteFolder>>(Folders);
    }

    /// <inheritdoc />
    public Task<uint> GetUidValidityAsync(string folderFullName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(UidValidity.GetValueOrDefault(folderFullName, 1u));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteMessage>> FetchNewMessagesAsync(
        string folderFullName,
        uint afterUid,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        FetchCalls.Add((folderFullName, afterUid, maxCount));
        OnFetchNew?.Invoke(folderFullName);
        if (FailingFolders.Contains(folderFullName))
        {
            throw new IOException($"Simulated failure in {folderFullName}.");
        }

        var all = Messages.GetValueOrDefault(folderFullName) ?? [];
        var newer = all.Where(m => m.Uid > afterUid).OrderBy(m => m.Uid);
        var page = afterUid == 0 ? newer.TakeLast(maxCount) : newer.Take(maxCount);
        return Task.FromResult<IReadOnlyList<RemoteMessage>>(page.ToList());
    }

    /// <summary>
    /// Gets the attachment contents keyed by folder, UID and ordinal.
    /// </summary>
    public Dictionary<(string Folder, uint Uid, int Ordinal), byte[]> AttachmentContents { get; } = [];

    /// <inheritdoc />
    public Task<byte[]?> DownloadAttachmentAsync(
        string folderFullName,
        uint uid,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(AttachmentContents.GetValueOrDefault((folderFullName, uid, ordinal)));
    }

    /// <summary>
    /// Gets the recorded delete calls.
    /// </summary>
    public List<(string Folder, uint Uid, string? TrashFolder)> DeleteCalls { get; } = [];

    /// <summary>
    /// Gets or sets an exception to throw from <see cref="DeleteMessageAsync"/>.
    /// </summary>
    public Exception? DeleteFailure { get; set; }

    /// <inheritdoc />
    public Task DeleteMessageAsync(
        string folderFullName,
        uint uid,
        string? trashFolderFullName,
        CancellationToken cancellationToken = default)
    {
        DeleteCalls.Add((folderFullName, uid, trashFolderFullName));
        if (DeleteFailure is not null)
        {
            return Task.FromException(DeleteFailure);
        }

        // Mimic the server: the message leaves the source folder (a move would also add it to Trash, which no test needs).
        Messages.GetValueOrDefault(folderFullName)?.RemoveAll(m => m.Uid == uid);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the simulated server-side flags per folder and UID, seeded lazily from <see cref="Messages"/>.
    /// </summary>
    private Dictionary<(string Folder, uint Uid), (bool IsRead, bool IsFlagged, bool IsDeleted)> ServerFlags { get; } = [];

    /// <summary>
    /// Gets the recorded <see cref="SetFlagsAsync"/> calls.
    /// </summary>
    public List<(string Folder, IReadOnlyList<FlagUpdate> Updates)> SetFlagsCalls { get; } = [];

    /// <summary>
    /// Gets the recorded <see cref="FetchFlagsAsync"/> calls.
    /// </summary>
    public List<(string Folder, uint MinUid)> FetchFlagsCalls { get; } = [];

    /// <summary>
    /// Gets or sets an exception to throw from <see cref="SetFlagsAsync"/>.
    /// </summary>
    public Exception? SetFlagsFailure { get; set; }

    /// <summary>
    /// Gets or sets an exception to throw from <see cref="FetchFlagsAsync"/>.
    /// </summary>
    public Exception? FetchFlagsFailure { get; set; }

    /// <summary>
    /// Gets or sets a callback run inside <see cref="SetFlagsAsync"/> before the server state changes, to simulate concurrent user edits.
    /// </summary>
    public Action<string, IReadOnlyList<FlagUpdate>>? OnSetFlags { get; set; }

    /// <summary>
    /// Gets or sets a callback run inside <see cref="FetchFlagsAsync"/> before the flags are read, to simulate concurrent user edits.
    /// </summary>
    public Action<string, uint>? OnFetchFlags { get; set; }

    /// <summary>
    /// Sets the read and starred flags the simulated server holds for a message.
    /// </summary>
    /// <param name="folder">The folder full name.</param>
    /// <param name="uid">The message UID.</param>
    /// <param name="isRead">Whether the message is seen.</param>
    /// <param name="isFlagged">Whether the message is flagged.</param>
    public void SetServerFlags(string folder, uint uid, bool isRead, bool isFlagged)
    {
        var current = GetServerFlags(folder, uid);
        ServerFlags[(folder, uid)] = (isRead, isFlagged, current.IsDeleted);
    }

    /// <summary>
    /// Flags a message as deleted on the simulated server without removing it.
    /// </summary>
    /// <param name="folder">The folder full name.</param>
    /// <param name="uid">The message UID.</param>
    public void MarkDeletedOnServer(string folder, uint uid)
    {
        var current = GetServerFlags(folder, uid);
        ServerFlags[(folder, uid)] = (current.IsRead, current.IsFlagged, true);
    }

    /// <summary>
    /// Removes a message from the simulated server entirely.
    /// </summary>
    /// <param name="folder">The folder full name.</param>
    /// <param name="uid">The message UID.</param>
    public void RemoveFromServer(string folder, uint uid)
    {
        Messages.GetValueOrDefault(folder)?.RemoveAll(m => m.Uid == uid);
        ServerFlags.Remove((folder, uid));
    }

    /// <summary>
    /// Gets the flags the simulated server holds for a message, seeding them from the message when first read.
    /// </summary>
    /// <param name="folder">The folder full name.</param>
    /// <param name="uid">The message UID.</param>
    /// <returns>The current flags.</returns>
    public (bool IsRead, bool IsFlagged, bool IsDeleted) GetServerFlags(string folder, uint uid)
    {
        if (ServerFlags.TryGetValue((folder, uid), out var flags))
        {
            return flags;
        }

        var message = Messages.GetValueOrDefault(folder)?.FirstOrDefault(m => m.Uid == uid);
        flags = (message?.IsRead ?? false, false, false);
        ServerFlags[(folder, uid)] = flags;
        return flags;
    }

    /// <inheritdoc />
    public Task SetFlagsAsync(
        string folderFullName,
        IReadOnlyList<FlagUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        SetFlagsCalls.Add((folderFullName, updates));
        OnSetFlags?.Invoke(folderFullName, updates);
        if (SetFlagsFailure is not null)
        {
            return Task.FromException(SetFlagsFailure);
        }

        var existing = Messages.GetValueOrDefault(folderFullName) ?? [];
        foreach (var update in updates)
        {
            // A real server ignores UIDs that no longer exist.
            if (existing.Any(m => m.Uid == update.Uid))
            {
                SetServerFlags(folderFullName, update.Uid, update.IsRead, update.IsFlagged);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFlagState>> FetchFlagsAsync(
        string folderFullName,
        uint minUid,
        CancellationToken cancellationToken = default)
    {
        FetchFlagsCalls.Add((folderFullName, minUid));
        OnFetchFlags?.Invoke(folderFullName, minUid);
        if (FetchFlagsFailure is not null)
        {
            return Task.FromException<IReadOnlyList<RemoteFlagState>>(FetchFlagsFailure);
        }

        var states = (Messages.GetValueOrDefault(folderFullName) ?? [])
            .Where(m => m.Uid >= minUid)
            .OrderBy(m => m.Uid)
            .Select(m =>
            {
                var flags = GetServerFlags(folderFullName, m.Uid);
                return new RemoteFlagState(m.Uid, flags.IsRead, flags.IsFlagged, flags.IsDeleted);
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<RemoteFlagState>>(states);
    }

    /// <summary>
    /// The number of waits so far; updated with <see cref="Interlocked"/> because loops of several accounts share this fake.
    /// </summary>
    private int _waitCount;

    /// <summary>
    /// The folder names of the waits so far, guarded by its own lock.
    /// </summary>
    private readonly List<string> _waitFolders = [];

    /// <summary>
    /// Gets or sets a value indicating whether the simulated server advertises IDLE.
    /// </summary>
    public bool SupportsIdle { get; set; } = true;

    /// <summary>
    /// Gets the number of <see cref="WaitForInboxChangeAsync"/> calls.
    /// </summary>
    public int WaitCount => Volatile.Read(ref _waitCount);

    /// <summary>
    /// Gets a snapshot of the folder names passed to <see cref="WaitForInboxChangeAsync"/>, in call order.
    /// </summary>
    public IReadOnlyList<string> WaitFolders
    {
        get
        {
            lock (_waitFolders)
            {
                return [.. _waitFolders];
            }
        }
    }

    /// <summary>
    /// Gets or sets the behaviour of <see cref="WaitForInboxChangeAsync"/> given the folder, the maximum wait and the token.
    /// It may return true or false, throw, or block (for example on a task completion source or the token).
    /// When it is <see langword="null"/> the first wait returns <see langword="true"/> and later waits return <see langword="false"/> immediately,
    /// like the real session (which reports a change on its first wait) with a maximum wait that then elapses.
    /// </summary>
    public Func<string, TimeSpan, CancellationToken, Task<bool>>? OnWaitForChange { get; set; }

    /// <inheritdoc />
    public async Task<bool> WaitForInboxChangeAsync(
        string folderFullName,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWait, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        var number = Interlocked.Increment(ref _waitCount);
        lock (_waitFolders)
        {
            _waitFolders.Add(folderFullName);
        }

        if (OnWaitForChange is null)
        {
            return number == 1;
        }

        return await OnWaitForChange(folderFullName, maxWait, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> AppendToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken cancellationToken = default)
    {
        AppendedToSent.Add(rfc822.ToArray());
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IImapSessionFactory"/> returning one scripted session.
/// </summary>
internal sealed class FakeImapSessionFactory : IImapSessionFactory
{
    /// <summary>
    /// Gets the session returned on every connect.
    /// </summary>
    public FakeImapSession Session { get; } = new();

    /// <summary>
    /// Gets or sets an exception to throw instead of connecting.
    /// </summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Gets the number of connection attempts.
    /// </summary>
    public int ConnectCount { get; private set; }

    /// <inheritdoc />
    public Task<IImapSession> ConnectAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        return Failure is null ? Task.FromResult<IImapSession>(Session) : Task.FromException<IImapSession>(Failure);
    }
}

/// <summary>
/// An <see cref="IMailSender"/> recording what it was asked to send.
/// </summary>
internal sealed class RecordingMailSender : IMailSender
{
    /// <summary>
    /// Gets the recorded sends.
    /// </summary>
    public List<(MailAccount Account, OutgoingMessage Message, bool SaveToSent)> Sent { get; } = [];

    /// <inheritdoc />
    public Task SendAsync(
        MailAccount account,
        OutgoingMessage message,
        bool saveToSent,
        CancellationToken cancellationToken = default)
    {
        Sent.Add((account, message, saveToSent));
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="ISyncSettings"/> whose value the test can change between syncs.
/// </summary>
internal sealed class FakeSyncSettings : ISyncSettings
{
    /// <inheritdoc />
    public bool RemoveServerDeletedMessages { get; set; }
}

/// <summary>
/// A clock whose time is set by the test.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    /// <summary>
    /// Gets or sets the current time.
    /// </summary>
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }
}

/// <summary>
/// Helpers for building test data.
/// </summary>
internal static class TestData
{
    /// <summary>
    /// Creates a downloaded message.
    /// </summary>
    /// <param name="uid">The UID.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="body">The body.</param>
    /// <returns>The message.</returns>
    public static RemoteMessage Remote(uint uid, string subject = "Hello", string body = "body text")
    {
        return new RemoteMessage(
            uid,
            $"<{uid}@example.com>",
            subject,
            "Alice",
            "alice@example.com",
            "me@example.com",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(uid),
            body,
            null,
            false,
            []);
    }

    /// <summary>
    /// Creates a selectable folder description.
    /// </summary>
    /// <param name="fullName">The full name.</param>
    /// <param name="role">The role.</param>
    /// <returns>The folder.</returns>
    public static RemoteFolder Folder(string fullName, FolderRole role = FolderRole.Other)
    {
        return new RemoteFolder(fullName, fullName, role, true);
    }

    /// <summary>
    /// Creates an account for the given email address.
    /// </summary>
    /// <param name="email">The email address.</param>
    /// <returns>The account.</returns>
    public static MailAccount Account(string email)
    {
        return MailAccount.Create(
            "Me",
            email,
            AuthMethod.Password,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UtcNow);
    }
}
