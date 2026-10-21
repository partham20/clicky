namespace Clicky.Core.Hotkeys;

/// <summary>What a hotkey does. Names are written to settings.json, so do not rename them.</summary>
public enum HotkeyAction
{
    /// <summary>Push-to-talk: look at the screen and listen while held.</summary>
    Talk,

    /// <summary>Type a question instead of speaking it.</summary>
    TextMode,

    /// <summary>Dictate into the focused field while held.</summary>
    Dictation,

    /// <summary>Dictate until the gesture is repeated.</summary>
    DictationHandsFree,

    OpenHome,

    /// <summary>Toggle always-listening voice mode.</summary>
    AlwaysOnVoice,
}
