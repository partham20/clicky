using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Clicky.Platform.Windows.SingleInstance;

/// <summary>
/// One Clicky per user per logon session. The first process owns a Local\ mutex and listens on a
/// named pipe; later processes send it a message (e.g. "show") and exit.
/// </summary>
/// <remarks>
/// <para>Local\ scopes the mutex to the session, so fast-user-switching and RDP sessions each get
/// their own instance. Pipe names are machine-global, so the pipe name carries the user SID and
/// session id, and both ends use <see cref="PipeOptions.CurrentUserOnly"/>.</para>
/// <para>If the running copy is elevated and this one is not (or vice versa), opening the mutex or
/// pipe fails with access denied. That still means "another instance exists", so we exit rather
/// than start a second copy.</para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string ShowMessage = "show";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex? _mutex;

    private SingleInstanceGuard(Mutex? mutex, bool isPrimary, string pipeName)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        PipeName = pipeName;
    }

    public bool IsPrimary { get; }

    public string PipeName { get; }

    public static SingleInstanceGuard Acquire(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        var sessionId = Process.GetCurrentProcess().SessionId;
        var pipeName = $"{appId}.{sid}.{sessionId}.activate";
        var mutexName = $@"Local\{appId}.SingleInstance";

        try
        {
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (createdNew)
            {
                return new SingleInstanceGuard(mutex, isPrimary: true, pipeName);
            }

            mutex.Dispose();
            return new SingleInstanceGuard(null, isPrimary: false, pipeName);
        }
        catch (UnauthorizedAccessException)
        {
            // Exists, but was created at a different integrity level.
            return new SingleInstanceGuard(null, isPrimary: false, pipeName);
        }
    }

    /// <summary>Secondary instance: hand <paramref name="message"/> to the primary. Returns false if it could not be reached.</summary>
    public bool TrySignalPrimary(string message, TimeSpan timeout, ILogger? logger = null)
    {
        if (IsPrimary)
        {
            throw new InvalidOperationException("The primary instance cannot signal itself.");
        }

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            // Connect spins while the pipe does not exist yet, so a primary that is still starting
            // up (or between two listen iterations) is covered by the timeout.
            client.Connect(timeout);
            var payload = Encoding.UTF8.GetBytes(message + "\n");
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not reach the running instance over {Pipe}", PipeName);
            return false;
        }
    }

    /// <summary>Primary instance: accept messages until cancelled. Each connection carries one line.</summary>
    public async Task ListenAsync(Func<string, CancellationToken, Task> onMessage, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        ArgumentNullException.ThrowIfNull(logger);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance listens.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await using (server.ConfigureAwait(false))
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readCts.CancelAfter(ReadTimeout);
                    using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        await onMessage(line.Trim(), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Activation client connected but sent nothing within {Timeout}", ReadTimeout);
            }
            catch (IOException ex)
            {
                // Client vanished mid-message; keep serving.
                logger.LogDebug(ex, "Activation pipe I/O error");
            }
            catch (UnauthorizedAccessException ex)
            {
                // Someone else already holds this pipe name; nothing we can do but stop listening.
                logger.LogError(ex, "Cannot create activation pipe {Pipe}; second launches will not bring this instance forward", PipeName);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from a thread that does not own it; closing the handle below is enough.
        }

        _mutex.Dispose();
    }
}
