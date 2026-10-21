namespace Clicky.Core.Diagnostics;

/// <summary>A "new issue" link with the title and body filled in, short enough for any browser.</summary>
public static class GitHubIssueLink
{
    /// <summary>GitHub rejects very long query strings; stay well under its limit once escaped.</summary>
    public const int MaxBodyLength = 5000;

    public const int MaxTitleLength = 80;

    public static Uri Create(string repositoryUrl, BugReport report, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentNullException.ThrowIfNull(report);

        var body = report.Summary.TrimEnd();
        if (body.Length > MaxBodyLength)
        {
            body = body[..MaxBodyLength] + "\n\n(cut short; the full text is report.md in the zip)";
        }

        body += $"\n\n## Report\n\nDrag `{Path.GetFileName(report.ZipPath)}` here to attach the logs (look through it first).\n";
        var url = $"{repositoryUrl.TrimEnd('/')}/issues/new?labels=bug&title={Uri.EscapeDataString(Title(description))}&body={Uri.EscapeDataString(body)}";
        return new Uri(url);
    }

    internal static string Title(string description)
    {
        var firstLine = description.Trim().Split('\n', 2)[0].Trim();
        if (firstLine.Length == 0)
        {
            return "Bug report";
        }

        return firstLine.Length <= MaxTitleLength ? firstLine : firstLine[..(MaxTitleLength - 1)].TrimEnd() + "…";
    }
}
