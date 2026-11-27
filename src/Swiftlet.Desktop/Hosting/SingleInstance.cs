using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// How an attempt to become the only running instance ended.
/// </summary>
public enum SingleInstanceOutcome
{
    /// <summary>
    /// This process is the only instance and continues to start.
    /// </summary>
    Primary,

    /// <summary>
    /// Another instance runs and was asked to show its window; this process ends.
    /// </summary>
    Forwarded,

    /// <summary>
    /// Another instance holds the data but did not answer; this process ends.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Keeps a data directory to one running instance. The first instance holds a lock file and listens on a named pipe;
/// a later one asks it through the pipe to show its window.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// The name of the lock file in the data directory.
    /// </summary>
    private const string LockFileName = "instance.lock";

    /// <summary>
    /// The byte that asks for the window and confirms the request.
    /// </summary>
    private const byte ActivateMessage = 1;

    /// <summary>
    /// How long one exchange over the pipe may take.
    /// </summary>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The pause between two attempts.
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The held lock file.
    /// </summary>
    private readonly FileStream _lock;

    /// <summary>
    /// Ends the listener.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// The task answering later instances.
    /// </summary>
    private readonly Task _listener;

    /// <summary>
    /// Guards the handler and the pending request.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Shows the window; <see langword="null"/> until the application is ready for requests.
    /// </summary>
    private Action? _handler;

    /// <summary>
    /// Whether a request arrived before there was a handler.
    /// </summary>
    private bool _pending;

    /// <summary>
    /// Whether the instance was disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SingleInstance"/> class and starts listening.
    /// </summary>
    /// <param name="lockFile">The held lock file.</param>
    /// <param name="pipeName">The name of the pipe.</param>
    private SingleInstance(FileStream lockFile, string pipeName)
    {
        _lock = lockFile;
        var cancellationToken = _stopping.Token;
        _listener = Task.Run(() => ListenAsync(pipeName, cancellationToken), CancellationToken.None);
    }

    /// <summary>
    /// Gets or sets the logger; set once logging is available.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Becomes the only instance of a data directory, or asks the instance that already is to show its window. Both are
    /// retried until the timeout, because the other instance may be just starting or just quitting.
    /// </summary>
    /// <param name="dataDirectory">The application data directory; created when missing.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome, and the held instance when this process is the only one.</returns>
    public static async Task<(SingleInstanceOutcome Outcome, SingleInstance? Instance)> AcquireAsync(
        string dataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataDirectory);
        var lockPath = Path.Combine(dataDirectory, LockFileName);
        var pipeName = PipeName(dataDirectory);
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            if (TryLock(lockPath) is { } lockFile)
            {
                return (SingleInstanceOutcome.Primary, new SingleInstance(lockFile, pipeName));
            }

            if (await TrySignalAsync(pipeName, cancellationToken))
            {
                return (SingleInstanceOutcome.Forwarded, null);
            }

            if (TimeProvider.System.GetElapsedTime(started) >= timeout)
            {
                return (SingleInstanceOutcome.Unavailable, null);
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }
    }

    /// <summary>
    /// Sets what shows the window. A request that arrived earlier is delivered at once.
    /// </summary>
    /// <param name="handler">Shows the window; may be called on any thread.</param>
    public void SetActivationHandler(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        bool pending;
        lock (_gate)
        {
            _handler = handler;
            pending = _pending;
            _pending = false;
        }

        if (pending)
        {
            handler();
        }
    }

    /// <summary>
    /// Stops answering later instances, which then wait for the lock; call it once this instance starts to quit.
    /// </summary>
    public void StopListening()
    {
        if (_disposed || _stopping.IsCancellationRequested)
        {
            return;
        }

        _stopping.Cancel();

        // The listener only waits on the pipe, so it ends at once; the limit keeps a surprise from blocking the exit.
        _listener.Wait(ExchangeTimeout);
    }

    /// <summary>
    /// Stops listening and releases the lock.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopListening();
        _disposed = true;
        _stopping.Dispose();
        _lock.Dispose();
    }

    /// <summary>
    /// Opens the lock file for this process alone.
    /// </summary>
    /// <param name="path">The lock file.</param>
    /// <returns>The held file, or <see langword="null"/> when another process holds it.</returns>
    private static FileStream? TryLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Another instance holds the file.
            return null;
        }
    }

    /// <summary>
    /// Derives the pipe name from the data directory, so instances with other data do not hear each other.
    /// </summary>
    /// <param name="dataDirectory">The data directory.</param>
    /// <returns>The name; short, because Unix limits the length of the socket path.</returns>
    private static string PipeName(string dataDirectory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }

        return "swiftlet-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
    }

    /// <summary>
    /// Asks the listening instance to show its window.
    /// </summary>
    /// <param name="pipeName">The name of the pipe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the instance confirmed the request.</returns>
    private static async Task<bool> TrySignalAsync(string pipeName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExchangeTimeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);

            var processId = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(processId, timeout.Token);
            if (OperatingSystem.IsWindows())
            {
                // Windows lets only the process the user just started move a window to the front, so it passes that on.
                // A refusal is harmless: the window is still shown, and the taskbar button flashes.
                _ = AllowSetForegroundWindow(BitConverter.ToInt32(processId));
            }

            await pipe.WriteAsync(new[] { ActivateMessage }, timeout.Token);
            var answer = new byte[1];
            await pipe.ReadExactlyAsync(answer, timeout.Token);
            return answer[0] == ActivateMessage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Nobody listens yet or any more; the caller tries again.
            return false;
        }
    }

    /// <summary>
    /// Lets another process bring its window to the front.
    /// </summary>
    /// <param name="processId">The process.</param>
    /// <returns>Whether Windows accepted.</returns>
    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Answers later instances one after the other until stopped.
    /// </summary>
    /// <param name="pipeName">The name of the pipe.</param>
    /// <param name="cancellationToken">Ends the listener.</param>
    /// <returns>A task that completes when the listener has stopped.</returns>
    private async Task ListenAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                await AnswerAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A caller that gave up or a pipe that could not be created must not end the listener.
                Logger?.LogWarning(ex, "Answering another instance failed");
                await Task.Delay(RetryDelay, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Exchanges the messages with one caller.
    /// </summary>
    /// <param name="pipe">The connected pipe.</param>
    /// <param name="cancellationToken">Ends the listener.</param>
    /// <returns>A task that completes when the caller has its answer.</returns>
    private async Task AnswerAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExchangeTimeout);
        await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), timeout.Token);
        var request = new byte[1];
        await pipe.ReadExactlyAsync(request, timeout.Token);
        if (request[0] != ActivateMessage)
        {
            return;
        }

        Logger?.LogInformation("Another instance was started; showing the window");
        Activate();
        await pipe.WriteAsync(request, timeout.Token);

        // Closing the pipe at once could drop the answer before the caller has read it.
        var end = new byte[1];
        _ = await pipe.ReadAsync(end, timeout.Token);
    }

    /// <summary>
    /// Shows the window, or remembers the request until there is a handler.
    /// </summary>
    private void Activate()
    {
        Action? handler;
        lock (_gate)
        {
            handler = _handler;
            _pending = handler is null;
        }

        handler?.Invoke();
    }
}
