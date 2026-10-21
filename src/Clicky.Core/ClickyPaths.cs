namespace Clicky.Core;

/// <summary>
/// Well-known locations under the per-user data root (%AppData%\Clicky by default).
/// The root is injectable so tests and portable builds can redirect everything.
/// </summary>
public sealed class ClickyPaths
{
    public const string AppFolderName = "Clicky";

    public ClickyPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string SkillsDirectory => Path.Combine(Root, "skills");

    public string MemoryDirectory => Path.Combine(Root, "memory");

    /// <summary>Only written when settings.debug.saveCaptures is on.</summary>
    public string DebugCapturesDirectory => Path.Combine(Root, "debug", "captures");

    /// <summary>%AppData%\Clicky (roaming), overridable with CLICKY_DATA_DIR for dev/test.</summary>
    public static ClickyPaths CreateDefault()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CLICKY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new ClickyPaths(overrideRoot);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return new ClickyPaths(Path.Combine(appData, AppFolderName));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
