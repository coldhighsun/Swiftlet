using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Application.Sync;

/// <summary>
/// Downloads a list of messages over several connections at once and hands them out strictly in list order.
/// At most one message per connection is downloading or waiting to be taken, which bounds the memory used.
/// </summary>
internal sealed class ParallelDownloader : IAsyncDisposable
{
    /// <summary>
    /// The connections of the pass.
    /// </summary>
    private readonly BackfillConnections _connections;

    /// <summary>
    /// The folder the messages are downloaded from.
    /// </summary>
    private readonly string _folderFullName;

    /// <summary>
    /// The messages to download, in the order they are handed out.
    /// </summary>
    private readonly IReadOnlyList<RemoteHeader> _headers;

    /// <summary>
    /// The connections that have no download assigned.
    /// </summary>
    private readonly Stack<IImapSession> _idle;

    /// <summary>
    /// The started downloads in list order.
    /// </summary>
    private readonly Queue<(RemoteHeader Header, IImapSession Session, Task<IReadOnlyList<RemoteMessage>> Download)> _running = new();

    /// <summary>
    /// Cancels the running downloads when the downloader is abandoned.
    /// </summary>
    private readonly CancellationTokenSource _abort;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Asks whether no further downloads should be started; optional.
    /// </summary>
    private readonly Func<bool>? _shouldStop;

    /// <summary>
    /// The position of the next message to start.
    /// </summary>
    private int _next;

    /// <summary>
    /// Whether stopping was requested; it is not taken back, so the downloads handed out never have a gap.
    /// </summary>
    private bool _stopped;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParallelDownloader"/> class.
    /// </summary>
    /// <param name="connections">The connections of the pass.</param>
    /// <param name="helpers">The additional connections to use besides the primary one.</param>
    /// <param name="folderFullName">The folder the messages are downloaded from.</param>
    /// <param name="headers">The messages to download, in the order they are handed out.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token of the batch.</param>
    /// <param name="shouldStop">Asks whether no further downloads should be started; the first message is started regardless. Optional.</param>
    public ParallelDownloader(
        BackfillConnections connections,
        IReadOnlyList<IImapSession> helpers,
        string folderFullName,
        IReadOnlyList<RemoteHeader> headers,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<bool>? shouldStop = null)
    {
        _shouldStop = shouldStop;
        _connections = connections;
        _folderFullName = folderFullName;
        _headers = headers;
        _logger = logger;

        // The primary connection is pushed last so it takes the first, newest message.
        _idle = new Stack<IImapSession>([.. helpers.Reverse(), connections.Primary]);
        _abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>
    /// Gets a value indicating whether stopping was requested and every started download has been taken, so there is no next message.
    /// </summary>
    public bool IsStopped => _next > 0 && _running.Count == 0 && _next < _headers.Count && CheckStop();

    /// <summary>
    /// Gets the next message of the list, starting further downloads on the free connections.
    /// A download that failed on an additional connection is repeated on the primary one.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token of the batch.</param>
    /// <returns>The downloaded message, or an empty list if it vanished from the server.</returns>
    public async Task<IReadOnlyList<RemoteMessage>> TakeNextAsync(CancellationToken cancellationToken)
    {
        StartDownloads();
        var (header, session, download) = _running.Dequeue();
        IReadOnlyList<RemoteMessage> result;
        try
        {
            result = await download;
        }
        catch (Exception ex) when (session != _connections.Primary && !cancellationToken.IsCancellationRequested)
        {
            // An additional connection may be refused, throttled or dropped while idle; the primary one takes over its message.
            _logger.LogWarning(ex, "An additional backfill connection failed in {Folder}; it is no longer used.", _folderFullName);
            await _connections.RetireAsync(session);

            // A connection runs one command at a time, so whatever the primary one is doing has to end first.
            foreach (var other in _running.Where(r => r.Session == _connections.Primary))
            {
                await ((Task)other.Download).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }

            return await _connections.Primary.FetchMessagesAsync(_folderFullName, [header], cancellationToken);
        }

        _idle.Push(session);

        // Refill before the caller stores the message, so the connections do not wait for the database.
        StartDownloads();
        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Only an abandoned batch (failure or cancellation) still has downloads running; no connection may be left busy.
        if (_running.Count > 0)
        {
            await _abort.CancelAsync();
        }

        while (_running.TryDequeue(out var running))
        {
            await ((Task)running.Download).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }

        _abort.Dispose();
    }

    /// <summary>
    /// Assigns the next messages to the free connections.
    /// </summary>
    private void StartDownloads()
    {
        // The first message is downloaded in any case, so a batch makes progress however busy the account is.
        while (_next < _headers.Count && (_next == 0 || !CheckStop()) && _idle.TryPop(out var session))
        {
            var header = _headers[_next++];
            _running.Enqueue((header, session, DownloadAsync(session, header)));
        }
    }

    /// <summary>
    /// Asks, and remembers, whether no further downloads should be started.
    /// </summary>
    /// <returns><see langword="true"/> once stopping was requested.</returns>
    private bool CheckStop()
    {
        _stopped |= _shouldStop?.Invoke() == true;
        return _stopped;
    }

    /// <summary>
    /// Downloads one message; a synchronous failure is captured in the task like any other.
    /// </summary>
    /// <param name="session">The connection to use.</param>
    /// <param name="header">The message.</param>
    /// <returns>The download.</returns>
    private async Task<IReadOnlyList<RemoteMessage>> DownloadAsync(IImapSession session, RemoteHeader header)
    {
        return await session.FetchMessagesAsync(_folderFullName, [header], _abort.Token);
    }
}
