using Clicky.Core.Hotkeys;

namespace Clicky.Core.Settings;

/// <summary>
/// settings.hotkeys. Bindings are stored as text ("Ctrl+Alt", "double Ctrl") so a typo in one of
/// them is logged and skipped instead of making the whole file invalid.
/// </summary>
public sealed record HotkeySettings
{
    /// <summary>Defaults from the build plan. Always-on voice ships disabled: triple-tapping Ctrl is easy to do by accident.</summary>
    public static IReadOnlyList<HotkeyBindingSettings> DefaultBindings { get; } =
    [
        new() { Action = HotkeyAction.Talk, Gesture = "Ctrl+Alt" },
        new() { Action = HotkeyAction.TextMode, Gesture = "double Ctrl" },
        new() { Action = HotkeyAction.Dictation, Gesture = "Ctrl+Win" },
        new() { Action = HotkeyAction.DictationHandsFree, Gesture = "double Ctrl+Win" },
        new() { Action = HotkeyAction.OpenHome, Gesture = "Ctrl+Alt+A" },
        new() { Action = HotkeyAction.AlwaysOnVoice, Gesture = "triple Ctrl", Enabled = false },
    ];

    public IReadOnlyList<HotkeyBindingSettings> Bindings { get; init; } = DefaultBindings;

    /// <summary>
    /// Drops null and unknown-action entries and appends a default for any action that has no entry
    /// at all, so actions added in later versions show up. To turn one off, disable it or blank its gesture.
    /// </summary>
    public HotkeySettings Normalize()
    {
        var bindings = (Bindings ?? DefaultBindings)
            .Where(b => b is not null && Enum.IsDefined(b.Action))
            .Select(b => b.Gesture is null ? b with { Gesture = string.Empty } : b)
            .ToList();

        foreach (var missing in DefaultBindings.Where(d => bindings.All(b => b.Action != d.Action)))
        {
            bindings.Add(missing);
        }

        return this with { Bindings = bindings };
    }

    /// <summary>Enabled, parseable, valid bindings, plus a message for each one that was skipped.</summary>
    public IReadOnlyList<HotkeyBinding> ToBindings(out IReadOnlyList<string> problems)
    {
        var bindings = new List<HotkeyBinding>();
        var errors = new List<string>();
        foreach (var setting in Bindings)
        {
            if (!setting.Enabled || string.IsNullOrWhiteSpace(setting.Gesture))
            {
                continue;
            }

            if (!HotkeyGesture.TryParse(setting.Gesture, out var gesture))
            {
                errors.Add($"{setting.Action}: cannot read \"{setting.Gesture}\"");
                continue;
            }

            if (gesture.Validate() is { } problem)
            {
                errors.Add($"{setting.Action}: \"{setting.Gesture}\": {problem}");
                continue;
            }

            if (bindings.Find(b => b.Gesture == gesture) is { } clash)
            {
                errors.Add($"{setting.Action}: \"{setting.Gesture}\" is already used by {clash.Action}");
                continue;
            }

            bindings.Add(new HotkeyBinding(setting.Action, gesture));
        }

        problems = errors;
        return bindings;
    }

    // Records compare collections by reference; settings change detection needs content equality.
    public bool Equals(HotkeySettings? other) =>
        other is not null && (ReferenceEquals(Bindings, other.Bindings) || (Bindings is not null && other.Bindings is not null && Bindings.SequenceEqual(other.Bindings)));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var binding in Bindings ?? [])
        {
            hash.Add(binding);
        }

        return hash.ToHashCode();
    }
}

/// <remarks>Positional so a missing "enabled" reads as true (see <see cref="CaptureSettings"/>).</remarks>
public sealed record HotkeyBindingSettings(HotkeyAction Action = HotkeyAction.Talk, string Gesture = "", bool Enabled = true)
{
    public HotkeyAction Action { get; init; } = Action;

    /// <summary>Gesture text, e.g. "Ctrl+Alt", "Ctrl+Alt+A", "double Ctrl". Empty means unbound.</summary>
    public string Gesture { get; init; } = Gesture;

    public bool Enabled { get; init; } = Enabled;
}
