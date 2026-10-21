using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Clicky.App.Buddy;
using Clicky.App.Views;
using Clicky.Core;
using Microsoft.Extensions.Hosting;

namespace Clicky.App.Hosting;

/// <summary>Top-level navigation. Must be called on the UI thread.</summary>
public interface IAppShell
{
    void ShowHome();

    void ShowSettings();

    void Quit();
}

internal sealed class AppShell(
    ClickyPaths paths,
    IHostApplicationLifetime lifetime,
    Func<DockedBuddyView> dockedBuddy,
    Func<HotkeysSettingsView> hotkeysView,
    Func<VoiceSettingsView> voiceView,
    Func<SpeechSettingsView> speechView,
    Func<ClaudeSettingsView> claudeView,
    Func<MemorySettingsView> memoryView) : IAppShell
{
    private PlaceholderWindow? _home;
    private PlaceholderWindow? _settings;

    public void ShowHome() =>
        _home = Activate(_home, () => new PlaceholderWindow("Clicky Home", "The Home window arrives in M14.", extra: dockedBuddy()));

    public void ShowSettings() =>
        _settings = Activate(_settings, () => new PlaceholderWindow(
            "Clicky Settings",
            $"Settings pages arrive in M18. Until then, edit the file directly; changes apply live.\n\n{paths.SettingsFile}",
            "Show settings.json",
            () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{paths.SettingsFile}\"") { UseShellExecute = true })?.Dispose(),
            new ScrollViewer { Content = new StackPanel { Children = { hotkeysView(), voiceView(), speechView(), claudeView(), memoryView() } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto })
        {
            Width = 640,
            Height = 820,
        });

    public void Quit() => lifetime.StopApplication();

    // A closed window cannot be shown again, and IsLoaded goes false on close, so reuse only a live one.
    private static PlaceholderWindow Activate(PlaceholderWindow? existing, Func<PlaceholderWindow> create)
    {
        var window = existing is { IsLoaded: true } ? existing : create();

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        return window;
    }
}
