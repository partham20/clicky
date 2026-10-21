using System.Windows;
using Clicky.App.Buddy;
using Clicky.App.Views;
using Clicky.Core.Updates;
using Microsoft.Extensions.Hosting;

namespace Clicky.App.Hosting;

/// <summary>Top-level navigation. Must be called on the UI thread.</summary>
public interface IAppShell
{
    void ShowHome();

    void ShowSettings();

    /// <summary>Settings, open at one page (a <see cref="SettingsPage.Id"/> from <see cref="SettingsPages"/>).</summary>
    void ShowSettingsPage(string pageId);

    /// <summary>First-run setup and the hands-on tutorial; opens by itself until finished once.</summary>
    void ShowOnboarding();

    /// <summary>Report a bug: describe, bundle logs into a zip, open a GitHub issue.</summary>
    void ShowBugReport();

    /// <summary>The Update button: hands the downloaded version to the updater and quits so it can install.</summary>
    void RestartToUpdate();

    void Quit();
}

internal sealed class AppShell(
    IHostApplicationLifetime lifetime,
    Func<DockedBuddyView> dockedBuddy,
    Func<SettingsWindow> settingsWindow,
    Func<OnboardingWindow> onboardingWindow,
    Func<BugReportWindow> bugReport,
    UpdateService updates) : IAppShell
{
    private PlaceholderWindow? _home;
    private SettingsWindow? _settings;
    private OnboardingWindow? _onboarding;
    private BugReportWindow? _bugReport;

    public void ShowHome() =>
        _home = Activate(_home, () => new PlaceholderWindow("Clicky Home", "The Home window arrives in M14.", extra: dockedBuddy()));

    public void ShowSettings() => _settings = Activate(_settings, settingsWindow);

    public void ShowSettingsPage(string pageId)
    {
        ShowSettings();
        _settings!.ShowPage(pageId);
    }

    public void ShowOnboarding() => _onboarding = Activate(_onboarding, onboardingWindow);

    public void ShowBugReport() => _bugReport = Activate(_bugReport, bugReport);

    public void RestartToUpdate()
    {
        if (updates.RestartToUpdate())
        {
            Quit();
        }
    }

    public void Quit() => lifetime.StopApplication();

    // A closed window cannot be shown again, and IsLoaded goes false on close, so reuse only a live one.
    private static T Activate<T>(T? existing, Func<T> create)
        where T : Window
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
