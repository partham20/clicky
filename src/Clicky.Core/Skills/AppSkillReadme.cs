namespace Clicky.Core.Skills;

/// <summary>The README.md written into a new skills folder.</summary>
internal static class AppSkillReadme
{
    public const string Text = """
        # Clicky app skills

        Each Markdown file here teaches Clicky about one app: where things are, the right way to do
        common tasks, the shortcuts. When you ask Clicky something while that app is in front, the
        guide goes along with your question, so pointing and step-by-step walkthroughs land on the
        right buttons. Edits apply to your next question; there is nothing to restart.

        A skill is `<name>.md` in this folder, or `<name>\SKILL.md` in a sub-folder. It starts with a
        short header between `---` lines:

        ```
        ---
        name: Excel
        apps: excel
        titles: "- Excel"
        description: Ribbon layout and common tasks in Excel 365
        ---
        Sort and Filter are on the Data tab. Freeze Panes is on the View tab, in the Window group.
        To add a chart, select the data first, then Insert > Recommended Charts.
        ```

        - `apps`: process names, with or without .exe (Task Manager > Details shows them). Separate
          several with commas.
        - `titles`: words from the window title, for apps that run in a browser
          (for example `"- Google Sheets"`).
        - A skill needs `apps` or `titles`. Up to 3 guides go with one question, and only about
          12,000 characters of each one.

        Clicky sends a guide once per conversation and again after you change it. Don't put
        passwords or other secrets in these files: they are sent to Claude.
        """;
}
