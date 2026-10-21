using Clicky.App.Views;
using Clicky.Core;
using Clicky.Core.Routines;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Power;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

internal static class RoutineServices
{
    /// <summary>M15 routines: engine, store, signals, tools for the assistant, the Settings page and startup.</summary>
    public static IServiceCollection AddRoutines(this IServiceCollection services, ClickyPaths paths)
    {
        services.AddSingleton(sp => new RoutineStore(paths.RoutinesDatabase, sp.GetRequiredService<ILogger<RoutineStore>>()));
        services.AddSingleton<WindowsSystemSignals>();
        services.AddSingleton<ISystemSignals>(sp => sp.GetRequiredService<WindowsSystemSignals>());

        // Until M14's Clickys replace it: Claude in a conversation of its own, result kept in the history.
        services.AddSingleton<IClickyRunner, AssistantClickyRunner>();
        services.AddSingleton(sp => new RoutineEngine(
            sp.GetRequiredService<RoutineStore>(),
            sp.GetRequiredService<IClickyRunner>(),
            sp.GetRequiredService<ISystemSignals>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<ILogger<RoutineEngine>>(),
            quiet: sp.GetService<IQuietDetector>()));

        // For chat and agent loops: "do this every morning" becomes create_routine.
        services.AddSingleton(sp => new RoutineTools(sp.GetRequiredService<RoutineEngine>()));

        services.AddTransient<RoutinesSettingsView>();
        services.AddSingleton<Func<RoutinesSettingsView>>(sp => sp.GetRequiredService<RoutinesSettingsView>);
        services.AddHostedService<RoutineStartup>();
        return services;
    }
}
