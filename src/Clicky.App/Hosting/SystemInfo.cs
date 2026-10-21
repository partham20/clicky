using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Clicky.Core.Settings;
using Clicky.Core.Updates;
using Clicky.Platform.Windows.Display;
using Microsoft.Win32;

namespace Clicky.App.Hosting;

/// <summary>What a bug report says about this PC. Nothing personal: no names, accounts or files.</summary>
internal static class SystemInfo
{
    public static IReadOnlyList<KeyValuePair<string, string>> Collect(UpdateStatus update, ClickySettings settings)
    {
        var info = new List<KeyValuePair<string, string>>
        {
            new("Clicky", $"{update.CurrentVersion} ({(update.Phase == UpdatePhase.NotInstalled ? "development build" : "installed")})"),
            new("Windows", WindowsVersion()),
            new("Architecture", $"{RuntimeInformation.OSArchitecture} (process {RuntimeInformation.ProcessArchitecture})"),
            new(".NET", RuntimeInformation.FrameworkDescription),
            new("CPU", $"{Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim() ?? "unknown"}, {Environment.ProcessorCount} logical"),
            new("Memory", $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):0.0} GB"),
            new("Monitors", Monitors()),
            new("Language", $"{CultureInfo.CurrentUICulture.Name} (format {CultureInfo.CurrentCulture.Name})"),
            new("Running for", Uptime()),
            new("Voice", $"{settings.Voice.Engine}, language {settings.Voice.Language}, GPU {(settings.Voice.UseGpu ? "on" : "off")}"),
            new("Speech", $"{settings.Speech.Engine}{(settings.Speech.QuietMode ? ", quiet mode" : string.Empty)}"),
            new("Update status", update.Describe()),
        };
        return info;
    }

    private static string WindowsVersion()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Registry.GetValue(key, "ProductName", null) as string ?? "Windows";
        var display = Registry.GetValue(key, "DisplayVersion", null) as string;
        var build = Environment.OSVersion.Version.Build;
        var revision = Registry.GetValue(key, "UBR", null) is int ubr ? ubr : 0;

        // Windows 11 still calls itself "Windows 10" in ProductName; the build number tells them apart.
        if (build >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        return $"{product}{(display is null ? string.Empty : " " + display)} (build {build}.{revision})";
    }

    private static string Monitors()
    {
        try
        {
            var monitors = DisplayMonitors.GetAll();
            return monitors.Count == 0 ? "none found" : string.Join("; ", monitors.Select(m => m.ToString()));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return $"unknown ({ex.Message})";
        }
    }

    private static string Uptime()
    {
        var running = DateTime.Now - Process.GetCurrentProcess().StartTime;
        return running.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)running.TotalHours} h {running.Minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{running.Minutes} min {running.Seconds} s");
    }
}
