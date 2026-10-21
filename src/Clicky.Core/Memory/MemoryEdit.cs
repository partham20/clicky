using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clicky.Core.Memory;

public enum MemoryEditKind
{
    /// <summary>Add <see cref="MemoryEdit.Text"/> under the section, creating the section if needed.</summary>
    Add,

    /// <summary>Swap the entry matching <see cref="MemoryEdit.Old"/> for <see cref="MemoryEdit.Text"/>.</summary>
    Replace,

    /// <summary>Delete the entry matching <see cref="MemoryEdit.Old"/>.</summary>
    Remove,
}

/// <summary>One change the updater proposes. <see cref="MemoryMerger"/> decides whether it is applied.</summary>
public sealed record MemoryEdit(MemoryEditKind Kind, MemoryFile File, string Section, string? Old = null, string? Text = null)
{
    public override string ToString() => Kind switch
    {
        MemoryEditKind.Add => $"add to {File}/{Section}: \"{Text}\"",
        MemoryEditKind.Replace => $"replace in {File}/{Section}: \"{Old}\" -> \"{Text}\"",
        _ => $"remove from {File}/{Section}: \"{Old}\"",
    };
}

/// <summary>
/// Reads the updater's reply: <c>{"edits":[{"op":"add","file":"profile","section":"…","text":"…"}]}</c>.
/// Tolerates prose or a code fence around the JSON. Individual malformed edits are skipped, not fatal.
/// </summary>
public static class MemoryEditParser
{
    /// <exception cref="FormatException">No JSON object with an "edits" array could be found.</exception>
    public static IReadOnlyList<MemoryEdit> Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var start = reply.IndexOf('{', StringComparison.Ordinal);
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new FormatException("The memory update reply has no JSON object.");
        }

        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize(reply.AsSpan(start, end - start + 1), MemoryJsonContext.Default.Wire);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The memory update reply is not valid JSON.", ex);
        }

        if (wire?.Edits is null)
        {
            throw new FormatException("The memory update reply has no \"edits\" array.");
        }

        var edits = new List<MemoryEdit>(wire.Edits.Count);
        foreach (var e in wire.Edits)
        {
            if (e is null || !TryKind(e.Op, out var kind) || !TryFile(e.File, out var file) || string.IsNullOrWhiteSpace(e.Section))
            {
                continue;
            }

            var edit = new MemoryEdit(kind, file, e.Section.Trim(), Clean(e.Old), Clean(e.Text));
            var complete = kind switch
            {
                MemoryEditKind.Add => edit.Text is not null,
                MemoryEditKind.Replace => edit.Old is not null && edit.Text is not null,
                _ => edit.Old is not null,
            };
            if (complete)
            {
                edits.Add(edit);
            }
        }

        return edits;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        return text.StartsWith("- ", StringComparison.Ordinal) ? text[2..].TrimStart() : text;
    }

    private static bool TryKind(string? op, out MemoryEditKind kind)
    {
        kind = op?.Trim().ToLowerInvariant() switch
        {
            "add" => MemoryEditKind.Add,
            "replace" or "update" => MemoryEditKind.Replace,
            "remove" or "delete" => MemoryEditKind.Remove,
            _ => (MemoryEditKind)(-1),
        };
        return Enum.IsDefined(kind);
    }

    private static bool TryFile(string? name, out MemoryFile file)
    {
        file = name?.Trim().ToLowerInvariant() switch
        {
            "profile" or "profile.md" => MemoryFile.Profile,
            "volatile" or "volatile.md" => MemoryFile.Volatile,
            _ => (MemoryFile)(-1),
        };
        return Enum.IsDefined(file);
    }

    internal sealed record Wire(IReadOnlyList<WireEdit?>? Edits);

    internal sealed record WireEdit(string? Op, string? File, string? Section, string? Old, string? Text);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(MemoryEditParser.Wire))]
internal sealed partial class MemoryJsonContext : JsonSerializerContext;
