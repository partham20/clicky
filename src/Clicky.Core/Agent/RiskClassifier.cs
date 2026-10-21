using System.Text.RegularExpressions;
using Clicky.Core.Actions;
using Clicky.Core.Connectors;

namespace Clicky.Core.Agent;

/// <summary>Why an action always needs the user's yes, even after "Always for this conversation".</summary>
public readonly record struct Risk(bool Irreversible, string? Why)
{
    public static Risk Normal { get; } = new(false, null);

    public static Risk Of(string why) => new(true, why);
}

/// <summary>
/// Spots actions that send, delete, buy, publish or can't be undone, from what they act on rather
/// than from what the model says, so a model that forgets ask_permission still can't send an email
/// unasked. Errs towards asking: a false alarm costs one click, a miss can't be taken back.
/// </summary>
public static partial class RiskClassifier
{
    /// <summary>Apps where Enter (or a typed newline) sends a message.</summary>
    private static readonly HashSet<string> s_messagingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams", "teams", "slack", "whatsapp", "whatsapp.root", "telegram", "discord", "signal", "olk", "outlook", "skype", "zoom", "lync", "thunderbird", "mailspring",
    };

    /// <summary>Shortcuts that send or delete for good in common apps.</summary>
    private static readonly HashSet<string> s_riskyChords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctrl+enter", "ctrl+return", "alt+s", "shift+delete", "shift+del", "ctrl+shift+enter", "cmd+enter", "ctrl+shift+delete",
    };

    public static Risk Classify(ClickyAction action, ControlTarget target)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(target);
        switch (action)
        {
            case ClickAction click:
                foreach (var name in new[] { target.Name, click.Target })
                {
                    if (!string.IsNullOrWhiteSpace(name) && RiskyWord().Match(name) is { Success: true } match)
                    {
                        return Risk.Of($"\"{Trim(name)}\" looks like it {Verb(match.Value)}");
                    }
                }

                return Risk.Normal;
            case KeyAction key:
                var chord = Normalize(key.Keys);
                if (s_riskyChords.Contains(chord))
                {
                    return Risk.Of($"{key.Keys} often sends or deletes for good");
                }

                if (chord is "enter" or "return" && IsMessaging(target))
                {
                    return Risk.Of($"Enter sends the message in {target.App ?? "this app"}");
                }

                return Risk.Normal;
            case TypeAction type when type.Text.Contains('\n', StringComparison.Ordinal) && IsMessaging(target):
                return Risk.Of($"a new line sends the message in {target.App ?? "this app"}");
            default:
                return Risk.Normal;
        }
    }

    /// <summary>Connector tools: the server's own hints. Read-only tools never need asking at all.</summary>
    public static Risk Classify(ConnectorTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.ReadOnly)
        {
            return Risk.Normal;
        }

        if (tool.Destructive)
        {
            return Risk.Of($"{tool.ConnectorName} says it can delete or overwrite");
        }

        if (tool.OpenWorld)
        {
            return Risk.Of($"{tool.ConnectorName} says it reaches other people or the web");
        }

        if (RiskyWord().IsMatch(tool.ServerToolName.Replace('_', ' ').Replace('-', ' ')))
        {
            return Risk.Of($"\"{tool.Title ?? tool.ServerToolName}\" looks like it sends or deletes");
        }

        return Risk.Normal;
    }

    private static bool IsMessaging(ControlTarget target) =>
        target.Process is { } process && s_messagingProcesses.Contains(process);

    private static string Normalize(string keys) =>
        string.Join('+', keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLowerInvariant() switch
            {
                "control" => "ctrl",
                "del" => "delete",
                "return" => "enter",
                var other => other,
            }));

    private static string Verb(string word) => word.ToLowerInvariant() switch
    {
        var w when w.StartsWith("send", StringComparison.Ordinal) || w is "post" or "publish" or "submit" or "reply" or "reply all" or "forward" or "share" => "sends or publishes something",
        var w when w.StartsWith("delet", StringComparison.Ordinal) || w is "remove" or "discard" or "erase" or "trash" or "empty" or "uninstall" or "format" or "clear all" or "reset" => "deletes something",
        _ => "buys, books or commits to something",
    };

    private static string Trim(string text) => text.Length <= 60 ? text.Trim() : text[..60].Trim() + "…";

    // Whole words: "Send" and "Send now" but not "Sender"; "Order" but not "Reorder columns".
    [GeneratedRegex(
        @"\b(send|send now|post|publish|submit|reply|reply all|forward|share|delete|deleted|remove|discard|erase|trash|empty|uninstall|format|clear all|reset|" +
        @"buy|buy now|purchase|pay|pay now|checkout|check out|place order|order|book|book now|confirm|transfer|subscribe|unsubscribe|merge|deploy)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiskyWord();
}
