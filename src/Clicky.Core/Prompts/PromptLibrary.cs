using System.Globalization;
using System.Text;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;

namespace Clicky.Core.Prompts;

/// <summary>How a backend gets actions out of the model.</summary>
public enum ActionDelivery
{
    /// <summary>Each action is a tool (Messages API).</summary>
    Tools,

    /// <summary>Actions are &lt;action&gt; tags in the text (Claude Code headless, which takes no custom tools).</summary>
    Tags,
}

/// <summary>
/// The prompts, kept as Markdown files embedded from <c>Clicky.Core/Prompts</c> so they can be read
/// and edited as prose (and shared with the Linux app later). System prompts are built once per
/// delivery and never change within a run, so they stay cacheable.
/// </summary>
public static class PromptLibrary
{
    private static readonly Lazy<string> s_toolsSystem = new(() => Compose("actions-tools.md"));
    private static readonly Lazy<string> s_tagsSystem = new(() => Compose("actions-tags.md"));

    public static string SystemPrompt(ActionDelivery delivery) =>
        delivery == ActionDelivery.Tools ? s_toolsSystem.Value : s_tagsSystem.Value;

    /// <summary>
    /// The user's turn as the model sees it: a line of screen context, then their words, marked as
    /// spoken (so it expects recognition errors) or typed.
    /// </summary>
    public static string UserText(ClaudeRequest request)
    {
        var text = new StringBuilder();
        if (request.Screenshot is { } shot)
        {
            text.Append(CultureInfo.InvariantCulture, $"[Screenshot: {shot.Geometry.ImageWidth} x {shot.Geometry.ImageHeight} pixels.");
            if (shot.Cursor is { } pointer)
            {
                text.Append(CultureInfo.InvariantCulture, $" The user's pointer is at ({pointer.X:0}, {pointer.Y:0}).");
            }

            text.Append("]\n");
        }
        else
        {
            text.Append("[No screenshot with this message.]\n");
        }

        text.Append(request.Source switch
        {
            RequestSource.Voice => "The user said (speech-to-text): ",
            RequestSource.Typed => "The user typed: ",
            _ => string.Empty,
        });
        text.Append(request.Text.Trim());
        return text.ToString();
    }

    public static string Read(string fileName)
    {
        var name = $"Clicky.Core.Prompts.{fileName}";
        using var stream = typeof(PromptLibrary).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded prompt {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static string Compose(string actionsFile) =>
        $"{Read("system.md")}\n\n{Read(actionsFile).Replace("{{ACTION_SCHEMA}}", ActionSchema.Default.Json, StringComparison.Ordinal)}";
}
