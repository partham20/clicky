using Microsoft.Win32;

namespace Clicky.Platform.Windows.Startup;

public enum StartupState
{
    NotRegistered,
    Enabled,

    /// <summary>Our Run entry exists but the user turned it off in Task Manager / Settings > Apps > Startup.</summary>
    DisabledByUser,
}

/// <summary>
/// Start-with-Windows via HKCU\...\Run (no elevation, per user).
/// </summary>
/// <remarks>
/// <para>Task Manager does not delete Run entries when the user disables them; it writes a 12-byte
/// value under Explorer\StartupApproved\Run whose first byte has bit 0 set for "disabled". We read
/// that so the toggle reflects reality, and clear it when the user re-enables from inside Clicky.</para>
/// <para>Velopack installs to %LocalAppData%\ClickyApp\current\Clicky.exe and swaps "current" on
/// every update, with a stable launcher stub at %LocalAppData%\ClickyApp\Clicky.exe. The Run entry
/// must point at the stub or it breaks after the first update (checked against a real install in M18).</para>
/// </remarks>
public sealed class StartupRegistration
{
    public const string AutostartArgument = "--autostart";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static readonly byte[] ApprovedEnabledValue = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly string _valueName;

    public StartupRegistration(string valueName, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _valueName = valueName;
        LaunchCommand = $"\"{ResolveLaunchPath(executablePath)}\" {AutostartArgument}";
    }

    /// <summary>The exact command line written to the Run key.</summary>
    public string LaunchCommand { get; }

    /// <summary>Maps a Velopack "current\" exe to the root launcher stub; any other path is returned unchanged.</summary>
    public static string ResolveLaunchPath(string executablePath)
    {
        var full = Path.GetFullPath(executablePath);
        var dir = Path.GetDirectoryName(full);
        if (dir is null || !string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        var root = Path.GetDirectoryName(dir);
        if (root is null)
        {
            return full;
        }

        var stub = Path.Combine(root, Path.GetFileName(full));
        var isVelopackLayout = File.Exists(Path.Combine(root, "Update.exe")) && File.Exists(stub);
        return isVelopackLayout ? stub : full;
    }

    public StartupState GetState()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (run?.GetValue(_valueName) is not string)
        {
            return StartupState.NotRegistered;
        }

        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: false);
        if (approved?.GetValue(_valueName) is byte[] { Length: > 0 } flags && (flags[0] & 0x01) != 0)
        {
            return StartupState.DisabledByUser;
        }

        return StartupState.Enabled;
    }

    /// <summary>True when the Run entry exists but points somewhere else (app moved, or pre-Velopack dev build).</summary>
    public bool IsRegisteredWithDifferentCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return run?.GetValue(_valueName) is string existing
            && !string.Equals(existing, LaunchCommand, StringComparison.OrdinalIgnoreCase);
    }

    public void Enable()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
        {
            run.SetValue(_valueName, LaunchCommand, RegistryValueKind.String);
        }

        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
        if (approved?.GetValue(_valueName) is not null)
        {
            approved.SetValue(_valueName, ApprovedEnabledValue, RegistryValueKind.Binary);
        }
    }

    public void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            run?.DeleteValue(_valueName, throwOnMissingValue: false);
        }

        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
        approved?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
