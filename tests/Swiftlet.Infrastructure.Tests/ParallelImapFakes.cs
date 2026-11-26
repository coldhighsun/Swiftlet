using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// One connection to a shared <see cref="FakeImapSession"/> server, with asynchronous hooks to hold commands open,
/// and a check that it never runs two commands at once.
/// </summary>
/// <param name="server">The shared server state.</param>
internal sealed class FakeImapConnection(FakeImapSession server) : IImapSession
{
    /// <summary>
    /// The number of commands running on this connection.
    /// </summary>
    private int _running;

    /// <summary>
    /// Gets or sets a hook awaited before new messages are fetched, given the folder name and the token.
    /// </summary>
    public Func<string, CancellationToken, Task>? BeforeFetchNewAsync { get; init; }

    /// <summary>
    /// Gets or sets a hook awaited before listed messages are downloaded, given the first UID and the token.
    /// </summary>
    public Func<uint, CancellationToken, Task>? BeforeDownloadAsync { get; init; }

    /// <summary>
    /// Gets a value indicating whether the connection was disposed.
    /// </summary>
    public bool Disposed { get; private set; }

    /// <summary>
    /// Gets a value indicating whether two commands ever overlapped on this connection, which a real client forbids.
    /// </summary>
    public bool Overlapped { get; private set; }

    /// <inheritdoc />
    public bool SupportsIdle => server.SupportsIdle;

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(null, () => server.ListFoldersAsync(cancellationToken));
    }

    /// <inheritdoc />
    public Task<uint> GetUidValidityAsync(string folderFullName, CancellationToken cancellationToken = default)
    {
        return RunAsync(null, () => server.GetUidValidityAsync(folderFullName, cancellationToken));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteMessage>> FetchNewMessagesAsync(
        string folderFullName,
        uint afterUid,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            BeforeFetchNewAsync is null ? null : () => BeforeFetchNewAsync(folderFullName, cancellationToken),
            () => server.FetchNewMessagesAsync(folderFullName, afterUid, maxCount, cancellationToken));
    }

    /// <inheritdoc />
    public Task<OlderHeadersPage> ListOlderAsync(
        string folderFullName,
        uint beforeUid,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(null, () => server.ListOlderAsync(folderFullName, beforeUid, maxCount, cancellationToken));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteMessage>> FetchMessagesAsync(
        string folderFullName,
        IReadOnlyList<RemoteHeader> headers,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            BeforeDownloadAsync is null ? null : () => BeforeDownloadAsync(headers[0].Uid, cancellationToken),
            () => server.FetchMessagesAsync(folderFullName, headers, cancellationToken));
    }

    /// <inheritdoc />
    public Task<byte[]?> DownloadAttachmentAsync(string folderFullName, uint uid, int ordinal, CancellationToken cancellationToken = default)
    {
        return RunAsync(null, () => server.DownloadAttachmentAsync(folderFullName, uid, ordinal, cancellationToken));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, InlineImage>?> DownloadInlineImagesAsync(
        string folderFullName,
        uint uid,
        IReadOnlyCollection<string> contentIds,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(null, () => server.DownloadInlineImagesAsync(folderFullName, uid, contentIds, cancellationToken));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<uint, uint>> DeleteMessagesAsync(
        string folderFullName,
        IReadOnlyList<uint> uids,
        string? trashFolderFullName,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(null, () => server.DeleteMessagesAsync(folderFullName, uids, trashFolderFullName, cancellationToken));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<uint>> ListUidsBetweenAsync(
        string folderFullName,
        uint afterUid,
        uint beforeUid,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(null, () => server.ListUidsBetweenAsync(folderFullName, afterUid, beforeUid, cancellationToken));
    }

    /// <inheritdoc />
    public Task SetFlagsAsync(string folderFullName, IReadOnlyList<FlagUpdate> updates, CancellationToken cancellationToken = default)
    {
        return RunAsync(
            null,
            async () =>
            {
                await server.SetFlagsAsync(folderFullName, updates, cancellationToken);
                return true;
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RemoteFlagState>> FetchFlagsAsync(string folderFullName, uint minUid, CancellationToken cancellationToken = default)
    {
        return RunAsync(null, () => server.FetchFlagsAsync(folderFullName, minUid, cancellationToken));
    }

    /// <inheritdoc />
    public Task<bool> WaitForInboxChangeAsync(string folderFullName, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        return server.WaitForInboxChangeAsync(folderFullName, maxWait, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> AppendToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken cancellationToken = default)
    {
        return server.AppendToSentAsync(rfc822, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Runs one command: first the hook, then the server call, which is locked because the fake server is not thread safe.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="before">The hook, or <see langword="null"/>.</param>
    /// <param name="command">The server call.</param>
    /// <returns>The result of the call.</returns>
    private async Task<T> RunAsync<T>(Func<Task>? before, Func<Task<T>> command)
    {
        if (Interlocked.Increment(ref _running) > 1)
        {
            Overlapped = true;
        }

        try
        {
            if (before is not null)
            {
                await before();
            }

            Task<T> call;
            lock (server)
            {
                call = command();
            }

            return await call;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>
/// Holds commands open until the test releases them, keyed by an arbitrary value.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
internal sealed class CommandGate<TKey>
    where TKey : notnull
{
    /// <summary>
    /// The completion of each key, created on first use.
    /// </summary>
    private readonly Dictionary<TKey, TaskCompletionSource> _gates = [];

    /// <summary>
    /// The keys that have been waited on, in order.
    /// </summary>
    private readonly List<TKey> _arrived = [];

    /// <summary>
    /// Gets a snapshot of the keys that have been waited on, in order.
    /// </summary>
    public IReadOnlyList<TKey> Arrived
    {
        get
        {
            lock (_gates)
            {
                return [.. _arrived];
            }
        }
    }

    /// <summary>
    /// Waits until the key is released.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the key is released.</returns>
    public Task WaitAsync(TKey key, CancellationToken cancellationToken)
    {
        lock (_gates)
        {
            _arrived.Add(key);
            return Get(key).Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Releases keys, whether or not they have been waited on yet.
    /// </summary>
    /// <param name="keys">The keys.</param>
    public void Release(params TKey[] keys)
    {
        lock (_gates)
        {
            foreach (var key in keys)
            {
                Get(key).TrySetResult();
            }
        }
    }

    /// <summary>
    /// Gets the completion of a key; the caller holds the lock.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The completion.</returns>
    private TaskCompletionSource Get(TKey key)
    {
        if (!_gates.TryGetValue(key, out var gate))
        {
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[key] = gate;
        }

        return gate;
    }
}
