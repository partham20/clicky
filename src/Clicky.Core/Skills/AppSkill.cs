namespace Clicky.Core.Skills;

/// <summary>
/// What Clicky knows about one app, from a Markdown file in %AppData%\Clicky\skills: where things are,
/// the right way to do common tasks, shortcuts. It goes to the model with questions asked while that
/// app is in front, so pointing and walkthroughs land on the right controls.
/// </summary>
/// <param name="Name">Shown in logs and prompts, e.g. "Excel".</param>
/// <param name="Apps">Process names it is for, without ".exe", matched case-insensitively.</param>
/// <param name="Titles">Window-title fragments it is for (any one, case-insensitive), e.g. "- Google Sheets" for a web app.</param>
/// <param name="Body">The guidance itself, Markdown, already trimmed to <see cref="AppSkillParser.MaxBodyChars"/>.</param>
/// <param name="Path">The file it came from.</param>
public sealed record AppSkill(
    string Name,
    string? Description,
    IReadOnlyList<string> Apps,
    IReadOnlyList<string> Titles,
    string Body,
    string Path)
{
    /// <summary>True when this skill is for <paramref name="app"/>, by process name or window title.</summary>
    public bool Matches(ForegroundApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        foreach (var name in Apps)
        {
            if (string.Equals(name, app.App, StringComparison.OrdinalIgnoreCase) || string.Equals(name, app.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var fragment in Titles)
        {
            if (app.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool Equals(AppSkill? other) =>
        other is not null && Name == other.Name && Description == other.Description && Body == other.Body && Path == other.Path
        && Apps.SequenceEqual(other.Apps) && Titles.SequenceEqual(other.Titles);

    public override int GetHashCode() => HashCode.Combine(Name, Body.Length, Path);
}
