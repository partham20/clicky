namespace Clicky.Core.Skills;

/// <summary>The app the user is in: the process that owns the foreground window, and that window's title.</summary>
/// <param name="ProcessName">Executable file name, e.g. "EXCEL.EXE"; "?" when it can't be read (an elevated app).</param>
/// <param name="Title">Window title; empty when it has none.</param>
public sealed record ForegroundApp(string ProcessName, string Title)
{
    /// <summary>The process name without ".exe", for matching and for prompts.</summary>
    public string App => ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ProcessName[..^4] : ProcessName;

    public override string ToString() => string.IsNullOrEmpty(Title) ? ProcessName : $"{ProcessName} \"{Title}\"";
}

/// <summary>Reads which app is in front.</summary>
public interface IForegroundApp
{
    /// <summary>The foreground app right now, or null when there is no foreground window. Any thread; never throws.</summary>
    ForegroundApp? Current();
}
