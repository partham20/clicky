using Clicky.Core.Settings;

namespace Clicky.Core.Voice;

/// <summary>Reasons not to speak a reply out loud. Several can hold at once.</summary>
[Flags]
public enum QuietReasons
{
    None = 0,

    /// <summary>The user turned on Quiet mode.</summary>
    QuietMode = 1 << 0,

    /// <summary>The output device is muted or its volume is at zero.</summary>
    Muted = 1 << 1,

    /// <summary>No output device is plugged in or enabled.</summary>
    NoOutputDevice = 1 << 2,

    /// <summary>Another app has the microphone open: most likely a call.</summary>
    InCall = 1 << 3,

    /// <summary>A known screen-sharing toolbar is on screen.</summary>
    ScreenSharing = 1 << 4,

    /// <summary>Focus Assist / Do not disturb, or Windows' quiet time.</summary>
    FocusAssist = 1 << 5,

    /// <summary>A presentation or a full-screen app or game has the screen.</summary>
    FullScreen = 1 << 6,
}

/// <param name="Reasons">What was detected.</param>
/// <param name="Detail">The app behind <see cref="QuietReasons.InCall"/> or <see cref="QuietReasons.ScreenSharing"/>, for the log and the card.</param>
public sealed record QuietCheck(QuietReasons Reasons, string? Detail = null)
{
    public static QuietCheck None { get; } = new(QuietReasons.None);
}

/// <summary>Looks at the PC right now: output device, calls, screen sharing, Focus Assist.</summary>
public interface IQuietDetector
{
    /// <summary>Everything that holds now. Fast (a few ms); never throws.</summary>
    QuietCheck Check(SpeechSettings settings);
}

/// <summary>Which detected conditions keep Clicky quiet, given the user's settings, and how to say so.</summary>
public static class QuietPolicy
{
    /// <summary>The reasons that apply: muted or no device always, the rest as settings.speech says.</summary>
    public static QuietReasons Apply(SpeechSettings settings, QuietReasons detected)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var applies = QuietReasons.Muted | QuietReasons.NoOutputDevice;
        if (settings.QuietMode)
        {
            detected |= QuietReasons.QuietMode;
            applies |= QuietReasons.QuietMode;
        }

        if (settings.QuietInCalls)
        {
            applies |= QuietReasons.InCall;
        }

        if (settings.QuietWhenSharing)
        {
            applies |= QuietReasons.ScreenSharing;
        }

        if (settings.QuietInFocusAssist)
        {
            applies |= QuietReasons.FocusAssist | QuietReasons.FullScreen;
        }

        return detected & applies;
    }

    /// <summary>One short line for the card, naming the most telling reason.</summary>
    public static string Describe(QuietReasons reasons, string? detail = null)
    {
        var app = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
        return reasons switch
        {
            _ when reasons.HasFlag(QuietReasons.QuietMode) => "Quiet mode is on.",
            _ when reasons.HasFlag(QuietReasons.InCall) => $"You seem to be in a call{app}.",
            _ when reasons.HasFlag(QuietReasons.ScreenSharing) => $"You're sharing your screen{app}.",
            _ when reasons.HasFlag(QuietReasons.NoOutputDevice) => "No speakers or headphones are connected.",
            _ when reasons.HasFlag(QuietReasons.Muted) => "Your sound is muted.",
            _ when reasons.HasFlag(QuietReasons.FocusAssist) => "Do not disturb is on.",
            _ when reasons.HasFlag(QuietReasons.FullScreen) => "A full-screen app or presentation is running.",
            _ => string.Empty,
        };
    }
}
