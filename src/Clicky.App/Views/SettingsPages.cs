using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Clicky.App.Views;

/// <summary>A page in the Settings window's sidebar. Pages with no sections registered stay hidden.</summary>
/// <param name="Glyph">Segoe Fluent Icons / MDL2 Assets code point.</param>
public sealed record SettingsPage(string Id, string Title, int Order, string Glyph);

/// <summary>
/// The Settings window's pages, in sidebar order. A module adds its settings with one line in DI:
/// <c>services.AddSettingsSection&lt;MyView&gt;(SettingsPages.Agents)</c>. Agents and Suggestions are
/// reserved for M13-M15 and M17 and appear once something registers a section on them.
/// </summary>
public static class SettingsPages
{
    public static readonly SettingsPage General = new("general", "General", 0, "\uE713");
    public static readonly SettingsPage Cursor = new("cursor", "Cursor and character", 10, "\uE7C9");
    public static readonly SettingsPage Shortcuts = new("shortcuts", "Shortcuts", 20, "\uE765");
    public static readonly SettingsPage Voice = new("voice", "Voice", 30, "\uE720");

    /// <summary>Text mode and documents (M10).</summary>
    public static readonly SettingsPage TextMode = new("text", "Typed questions and documents", 35, "\uE8D2");
    public static readonly SettingsPage Claude = new("claude", "Claude", 40, "\uE8BD");
    public static readonly SettingsPage Dictation = new("dictation", "Dictation and dictionary", 50, "\uE82D");
    public static readonly SettingsPage Memory = new("memory", "Memory", 60, "\uE8F1");
    public static readonly SettingsPage Connectors = new("connectors", "Connectors", 70, "\uE71B");

    /// <summary>Computer use (M13), Clickys (M14) and routines (M15).</summary>
    public static readonly SettingsPage Agents = new("agents", "Agents", 80, "\uE99A");

    /// <summary>Suggestions and announcements (M17).</summary>
    public static readonly SettingsPage Suggestions = new("suggestions", "Suggestions", 90, "\uE789");

    public static readonly SettingsPage Updates = new("updates", "Updates and support", 100, "\uE895");
}

/// <summary>One block on a settings page. A page shows its sections by <paramref name="Order"/>, top to bottom.</summary>
/// <param name="Create">Makes a fresh view; called on the UI thread each time a Settings window opens the page.</param>
public sealed record SettingsSection(SettingsPage Page, int Order, Func<IServiceProvider, FrameworkElement> Create);

public static class SettingsSectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TView"/> (transient, resolved on the UI thread) as a section of
    /// <paramref name="page"/>. The view should subscribe to settings changes on Loaded and
    /// unsubscribe on Unloaded, like the existing ones.
    /// </summary>
    public static IServiceCollection AddSettingsSection<TView>(this IServiceCollection services, SettingsPage page, int order = 0)
        where TView : FrameworkElement
    {
        ArgumentNullException.ThrowIfNull(page);
        services.AddTransient<TView>();
        services.AddSingleton(new SettingsSection(page, order, sp => sp.GetRequiredService<TView>()));
        return services;
    }
}
