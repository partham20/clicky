using System.Diagnostics;
using System.Runtime.InteropServices;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Speech;

/// <summary>
/// Looks for reasons not to speak: muted or missing speakers, a call, a screen share, Focus
/// Assist / Do not disturb, a full-screen app. Windows has a real API for only some of these, so
/// the rest are careful guesses; each one fails open (to speaking) if Windows changes underneath it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Speakers.</b> The endpoint Clicky would play on: none, muted, or volume at zero.</item>
/// <item><b>Calls.</b> The microphone-in-use list Windows keeps for its privacy indicator
/// (CapabilityAccessManager\ConsentStore\microphone): an app whose last use has a start but no stop
/// has the mic open now. Teams, Zoom, Meet in a browser, Discord and Slack all keep it open for the
/// whole call, even when muted. Apps that hold the mic for other reasons can be listed in
/// settings.speech.ignoreMicApps.</item>
/// <item><b>Screen sharing.</b> No API exists. Most shares happen in a call, which already counts;
/// beyond that, the sharing toolbars of Teams, Zoom, Chrome/Edge, Firefox and Slack are recognized
/// by their window titles.</item>
/// <item><b>Focus Assist.</b> SHQueryUserNotificationState (documented) reports quiet time,
/// presentation mode and full-screen apps. The active Focus Assist profile comes from the shell's
/// own WNF state, and Windows 11's Do not disturb from its notification setting; both undocumented.</item>
/// </list>
/// </remarks>
public sealed class WindowsQuietDetector(ILogger<WindowsQuietDetector> logger) : IQuietDetector
{
    private const string MicrophoneStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string NotificationSettings = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    /// <summary>Title fragments of the toolbars apps show while sharing. Specific on purpose: browser titles are page titles.</summary>
    private static readonly string[] SharingTitles =
    [
        "is sharing your screen",
        "is sharing a window",
        "is sharing your entire screen",
        "is sharing this tab",
        "sharing indicator",
        "you are screen sharing",
        "you're screen sharing",
        "sharing control bar",
        "sharing toolbar",
        "share toolbar",
        "screen share controls",
    ];

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ms-teams"] = "Microsoft Teams",
        ["msteams"] = "Microsoft Teams",
        ["teams"] = "Microsoft Teams",
        ["zoom"] = "Zoom",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["discord"] = "Discord",
        ["slack"] = "Slack",
        ["webex"] = "Webex",
        ["skype"] = "Skype",
        ["whatsapp"] = "WhatsApp",
    };

    private static readonly string OwnKey = (Environment.ProcessPath ?? string.Empty).Replace('\\', '#');

    private int _focusAssistUnavailable;

    public QuietCheck Check(SpeechSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var stopwatch = Stopwatch.StartNew();
        var reasons = QuietReasons.None;
        string? detail = null;

        reasons |= CheckOutput(settings.OutputDevice);

        if (settings.QuietInCalls && MicrophoneInUse(settings.IgnoreMicApps) is { } caller)
        {
            reasons |= QuietReasons.InCall;
            detail = caller;
        }

        if (settings.QuietWhenSharing && SharingToolbar() is { } sharer)
        {
            reasons |= QuietReasons.ScreenSharing;
            detail ??= sharer;
        }

        if (settings.QuietInFocusAssist)
        {
            reasons |= CheckFocus();
        }

        logger.LogDebug("Quiet check: {Reasons}{Detail} in {Ms:0.0} ms", reasons, detail is null ? string.Empty : $" ({detail})", stopwatch.Elapsed.TotalMilliseconds);
        return new QuietCheck(reasons, detail);
    }

    private static string Friendly(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var underscore = stem.IndexOf('_', StringComparison.Ordinal);
        if (underscore > 0)
        {
            // Packaged family name: "MSTeams_8wekyb3d8bbwe".
            stem = stem[..underscore];
        }

        var dot = stem.LastIndexOf('.');
        var leaf = dot >= 0 ? stem[(dot + 1)..] : stem;
        return FriendlyNames.TryGetValue(leaf, out var friendly) || FriendlyNames.TryGetValue(stem, out friendly) ? friendly : leaf;
    }

    private static bool IsRunning(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private QuietReasons CheckOutput(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = WasapiSpeaker.Resolve(enumerator, deviceId);
            if (device is null)
            {
                return QuietReasons.NoOutputDevice;
            }

            var volume = device.AudioEndpointVolume;
            return volume.Mute || volume.MasterVolumeLevelScalar < 0.005f ? QuietReasons.Muted : QuietReasons.None;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not read the output device's volume");
            return QuietReasons.None;
        }
    }

    /// <summary>The app that has the microphone open right now, or null.</summary>
    private string? MicrophoneInUse(IReadOnlyList<string> ignored)
    {
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(MicrophoneStore);
            if (store is null)
            {
                return null;
            }

            foreach (var name in store.GetSubKeyNames())
            {
                using var key = store.OpenSubKey(name);
                if (key is null)
                {
                    continue;
                }

                if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var exe in key.GetSubKeyNames())
                    {
                        using var app = key.OpenSubKey(exe);
                        if (app is not null && InUse(app) && !IsIgnored(exe, ignored) && !string.Equals(exe, OwnKey, StringComparison.OrdinalIgnoreCase) && IsRunning(exe.Replace('#', '\\')))
                        {
                            return Friendly(exe.Replace('#', '\\'));
                        }
                    }
                }
                else if (InUse(key) && !IsIgnored(name, ignored))
                {
                    return Friendly(name);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogDebug(ex, "Could not read the microphone-in-use list");
        }

        return null;

        static bool InUse(RegistryKey key) =>
            key.GetValue("LastUsedTimeStart") is long start && start != 0 && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

        static bool IsIgnored(string name, IReadOnlyList<string> ignored) =>
            ignored.Any(i => name.EndsWith(i.Replace('\\', '#'), StringComparison.OrdinalIgnoreCase) || name.StartsWith(i, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The app whose sharing toolbar is on screen, or null.</summary>
    private static unsafe string? SharingToolbar()
    {
        var found = new SharingSearch();
        var handle = GCHandle.Alloc(found);
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        if (found.Window == 0)
        {
            return null;
        }

        GetWindowThreadProcessId(found.Window, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return Friendly(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return found.Title;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnWindow(nint hwnd, nint state)
    {
        if (!IsWindowVisible(hwnd))
        {
            return 1;
        }

        int cloaked;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return 1;
        }

        var buffer = stackalloc char[256];
        var length = GetWindowText(hwnd, buffer, 256);
        if (length <= 0)
        {
            return 1;
        }

        var title = new string(buffer, 0, length);
        foreach (var fragment in SharingTitles)
        {
            if (title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                var search = (SharingSearch)GCHandle.FromIntPtr(state).Target!;
                search.Window = hwnd;
                search.Title = title;
                return 0;
            }
        }

        return 1;
    }

    private QuietReasons CheckFocus()
    {
        var reasons = QuietReasons.None;
        if (SHQueryUserNotificationState(out var state) == 0)
        {
            reasons |= state switch
            {
                QunsQuietTime => QuietReasons.FocusAssist,
                QunsBusy or QunsRunningD3dFullScreen or QunsPresentationMode or QunsApp => QuietReasons.FullScreen,
                _ => QuietReasons.None,
            };
        }

        if (Volatile.Read(ref _focusAssistUnavailable) == 0)
        {
            try
            {
                uint size = sizeof(uint);
                var status = NtQueryWnfStateData(WnfShelQuietHoursActiveProfileChanged, 0, 0, out _, out var profile, ref size);
                if (status == 0 && size >= sizeof(uint) && profile != 0)
                {
                    reasons |= QuietReasons.FocusAssist;
                }
                else if (status != 0)
                {
                    Volatile.Write(ref _focusAssistUnavailable, 1);
                    logger.LogDebug("Focus Assist state is not readable on this Windows (NTSTATUS 0x{Status:X8})", status);
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                Volatile.Write(ref _focusAssistUnavailable, 1);
            }
        }

        try
        {
            using var notifications = Registry.CurrentUser.OpenSubKey(NotificationSettings);
            if (notifications?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is int enabled && enabled == 0)
            {
                // Windows 11 Do not disturb.
                reasons |= QuietReasons.FocusAssist;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogDebug(ex, "Could not read the notification settings");
        }

        return reasons;
    }

    private sealed class SharingSearch
    {
        public nint Window { get; set; }

        public string? Title { get; set; }
    }
}
