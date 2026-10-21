using System.Globalization;
using System.Text;

namespace Clicky.Core.Memory;

/// <summary>
/// Renders memory as a block for the system prompt of voice, dictation and agent requests. Sections
/// are never cut: when hand edits push memory past the budget, whole sections are left out, profile
/// first in file order, and the block says how many were left out.
/// </summary>
public static class MemoryPrompt
{
    public const string Intro =
        "What you remember about the user from earlier conversations. Follow the preferences in it; " +
        "a preference stated in the current conversation wins over it. Don't recite it or mention that you " +
        "have it unless asked.";

    /// <returns>The block, or an empty string when there is nothing remembered.</returns>
    public static string Build(MemorySnapshot snapshot, MemoryLimits limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(limits);

        var profile = snapshot.Memory.Profile.Sections.Where(s => s.Entries.Count > 0).ToList();
        var current = snapshot.Memory.Volatile.Sections.Where(s => s.Entries.Count > 0).ToList();
        if (profile.Count == 0 && current.Count == 0)
        {
            return string.Empty;
        }

        var budget = limits.PromptChars;
        var omitted = 0;
        List<string> Fit(List<MemorySection> sections)
        {
            var fitted = new List<string>();
            foreach (var section in sections)
            {
                var text = MemoryDocument.SectionMarkdown(section);
                if (text.Length > budget)
                {
                    omitted++;
                    continue;
                }

                budget -= text.Length;
                fitted.Add(text);
            }

            return fitted;
        }

        var profileText = Fit(profile);
        var currentText = Fit(current);

        var sb = new StringBuilder();
        sb.Append("<memory>\n").Append(Intro).Append('\n');
        if (profileText.Count > 0)
        {
            sb.Append("<profile>\n").AppendJoin(string.Empty, profileText).Append("</profile>\n");
        }

        if (currentText.Count > 0)
        {
            var written = snapshot.VolatileWritten;
            sb.Append("<current_work");
            if (written is { } w)
            {
                sb.Append(" updated=\"").Append(w.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('"');
                if (now - w > limits.VolatileStaleAfter)
                {
                    sb.Append(" note=\"may be out of date; check before relying on it\"");
                }
            }

            sb.Append(">\n").AppendJoin(string.Empty, currentText).Append("</current_work>\n");
        }

        if (omitted > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"({omitted} more section{(omitted == 1 ? string.Empty : "s")} left out for length.)\n");
        }

        return sb.Append("</memory>").ToString();
    }
}
