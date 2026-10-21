using Clicky.App.Views;
using Clicky.Core;
using Clicky.Core.Agent;
using Clicky.Core.Clickys;
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

        // Results go to the Clicky's chat (M14); routines with no Clicky get Claude in a conversation of
        // their own, with the answer kept in the routine's history.
        services.AddSingleton<AssistantClickyRunner>();
        services.AddSingleton<IClickyRunner>(sp => new ClickyServiceRunner(
            sp.GetRequiredService<ClickyService>(),
            sp.GetRequiredService<AssistantClickyRunner>()));
        services.AddSingleton(sp => new RoutineEngine(
            sp.GetRequiredService<RoutineStore>(),
            sp.GetRequiredService<IClickyRunner>(),
            sp.GetRequiredService<ISystemSignals>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<ILogger<RoutineEngine>>(),
            quiet: sp.GetService<IQuietDetector>()));

        // Clickys' chats (M14): their own routines as tools; creating and deleting ask on the agent card.
        // Lazy, since the engine runs routines through the ClickyService these tools are given to.
        services.AddSingleton(sp => new RoutineConfirmer(sp.GetRequiredService<IAgentPresenter>()));
        services.AddSingleton(sp => new RoutineTools(sp.GetRequiredService<RoutineEngine>(), confirmer: sp.GetRequiredService<RoutineConfirmer>()));
        services.AddSingleton<IClickyTools>(sp => new ClickyRoutineTools(new Lazy<RoutineTools>(sp.GetRequiredService<RoutineTools>)));

        // Talk and text mode: Claude's routine action, saved once the user agrees on the agent card.
        services.AddSingleton(sp => new RoutineProposer(
            sp.GetRequiredService<RoutineEngine>(),
            sp.GetRequiredService<IAgentPresenter>(),
            sp.GetRequiredService<ILogger<RoutineProposer>>()));
        services.AddSingleton<IRoutineProposer>(sp => sp.GetRequiredService<RoutineProposer>());

        services.AddSettingsSection<RoutinesSettingsView>(SettingsPages.Agents, order: 20);
        services.AddHostedService<RoutineStartup>();
        return services;
    }
}
