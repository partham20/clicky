using System.Diagnostics;
using System.Text;

namespace Clicky.Core.Assistant.ClaudeCode;

/// <param name="Executable">Full path to claude.exe.</param>
/// <param name="Arguments">Passed as separate arguments (no shell, so nothing is re-parsed).</param>
/// <param name="WorkingDirectory">An empty folder, so Claude Code finds no project to read.</param>
/// <param name="RemoveEnvironment">Variables to drop from the child's environment.</param>
public sealed record ClaudeCliStartInfo(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyList<string> RemoveEnvironment);

/// <summary>A running <c>claude -p</c> speaking stream-json over stdin/stdout. A seam for tests.</summary>
public interface IClaudeCliProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>Exit code once <see cref="HasExited"/>.</summary>
    int? ExitCode { get; }

    /// <summary>The last few lines Claude Code wrote to stderr, for error messages.</summary>
    string ErrorTail { get; }

    /// <summary>Writes one JSON line to stdin and flushes it.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>The next stdout line, or null at end of stream.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process and anything it started. Safe to call more than once.</summary>
    void Kill();
}

/// <summary>The real process.</summary>
public sealed class ClaudeCliProcess : IClaudeCliProcess
{
    private const int ErrorTailLines = 20;

    private readonly Process _process;
    private readonly Queue<string> _errors = new();

    private ClaudeCliProcess(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_errors)
            {
                _errors.Enqueue(e.Data);
                while (_errors.Count > ErrorTailLines)
                {
                    _errors.Dequeue();
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode => HasExited ? _process.ExitCode : null;

    public string ErrorTail
    {
        get
        {
            lock (_errors)
            {
                return string.Join('\n', _errors);
            }
        }
    }

    /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
    public static ClaudeCliProcess Start(ClaudeCliStartInfo info)
    {
        var start = new ProcessStartInfo(info.Executable)
        {
            WorkingDirectory = info.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in info.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in info.RemoveEnvironment)
        {
            start.Environment.Remove(name);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {info.Executable}.");
        return new ClaudeCliProcess(process);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

/// <summary>Finds claude.exe: the configured path, then PATH, then the native installer's folder.</summary>
public static class ClaudeCliLocator
{
    public static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe" } : ["claude"];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = Path.Combine(home, ".local", "bin", names[0]);
        return File.Exists(native) ? native : null;
    }

    /// <summary>An npm install (claude.cmd) that can't be started without a shell; for the error message.</summary>
    public static string? FindNpmShim()
    {
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
        return File.Exists(npm) ? npm : null;
    }
}
