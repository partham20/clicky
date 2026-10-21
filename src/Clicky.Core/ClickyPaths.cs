namespace Clicky.Core;

/// <summary>
/// Well-known locations under the per-user data root (%AppData%\Clicky by default).
/// The root is injectable so tests and portable builds can redirect everything.
/// </summary>
public sealed class ClickyPaths
{
    public const string AppFolderName = "Clicky";

    /// <param name="root">Settings, logs and other small per-user data.</param>
    /// <param name="localRoot">
    /// Large, re-downloadable data (speech models), kept out of a roaming profile. Defaults to
    /// <paramref name="root"/>.
    /// </param>
    public ClickyPaths(string root, string? localRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        LocalRoot = string.IsNullOrWhiteSpace(localRoot) ? Root : Path.GetFullPath(localRoot);
    }

    public string Root { get; }

    public string LocalRoot { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string SkillsDirectory => Path.Combine(Root, "skills");

    public string MemoryDirectory => Path.Combine(Root, "memory");

    /// <summary>Words dictation should know (see settings.dictation); Clicky adds the ones it learns.</summary>
    public string DictionaryFile => Path.Combine(Root, "dictionary.json");

    /// <summary>Text of every dictation, one file per day, kept for settings.dictation.keepHistoryDays.</summary>
    public string DictationHistoryDirectory => Path.Combine(Root, "dictation");

    /// <summary>Only written when settings.debug.saveCaptures is on.</summary>
    public string DebugCapturesDirectory => Path.Combine(Root, "debug", "captures");

    /// <summary>Only written when settings.debug.saveAudio is on.</summary>
    public string DebugAudioDirectory => Path.Combine(Root, "debug", "audio");

    /// <summary>Whisper models, %LocalAppData%\Clicky\models by default.</summary>
    public string ModelsDirectory => Path.Combine(LocalRoot, "models");

    /// <summary>
    /// Working folder for Claude Code (subscription backend): an empty folder, so it finds no
    /// project files, plus the system prompt it is given.
    /// </summary>
    public string ClaudeCodeDirectory => Path.Combine(LocalRoot, "claude-code");

    /// <summary>
    /// %AppData%\Clicky (roaming) plus %LocalAppData%\Clicky for models. CLICKY_DATA_DIR overrides
    /// both for dev/test.
    /// </summary>
    public static ClickyPaths CreateDefault()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CLICKY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new ClickyPaths(overrideRoot);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new ClickyPaths(Path.Combine(appData, AppFolderName), Path.Combine(localAppData, AppFolderName));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
