namespace Clicky.Core.Clickys;

/// <summary>A starting point offered in Home's New Clicky dialog.</summary>
public sealed record ClickyTemplate(string Title, string Description, ClickyDraft Draft);

public static class ClickyTemplates
{
    public static IReadOnlyList<ClickyTemplate> All { get; } =
    [
        new(
            "Blank",
            "Start from nothing and write your own instructions.",
            new ClickyDraft("New Clicky")),
        new(
            "Researcher",
            "Digs into a topic and writes up what it found, with sources.",
            new ClickyDraft(
                "Researcher",
                "You research whatever the user asks about and write a clear brief: the answer first, then the details that matter, then " +
                "open questions. Say how sure you are and what you would check next. Use the connectors you have for sources when they help.",
                Color: "#8250DF")),
        new(
            "Daily briefing",
            "Pulls together what needs attention today. Pair it with a morning routine.",
            new ClickyDraft(
                "Daily briefing",
                "Each time you run, put together a short briefing for the user's day: what is scheduled, what is waiting on them, and anything " +
                "that changed since yesterday, from the connectors you can use. Lead with the three things that matter most. Keep it to one screen.",
                Color: "#1A7F37")),
        new(
            "Writer",
            "Drafts and polishes emails, posts and docs in your voice.",
            new ClickyDraft(
                "Writer",
                "You draft and edit writing for the user: emails, messages, posts and documents. Match their voice from the notes and files you " +
                "have. Give one strong draft, not options, unless they ask; then list any facts you assumed so they can check them.",
                Color: "#BF3989")),
        new(
            "Report builder",
            "Turns data or notes into a formatted report you can preview and save.",
            new ClickyDraft(
                "Report builder",
                "You turn the user's data, files and notes into a tidy report. Produce the report as one standalone HTML document (inline CSS, no " +
                "scripts, no external links or images) in a single html code block, then a two-line summary of what it shows.",
                Color: "#BC4C00")),
    ];
}
