using System.Windows.Threading;
using Clicky.App.Tray;
using Clicky.Core;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.SingleInstance;
using Clicky.Platform.Windows.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;

namespace Clicky.App.Hosting;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddClicky(
        this IServiceCollection services,
        App app,
        ClickyPaths paths,
        SingleInstanceGuard instance,
        LoggingLevelSwitch levelSwitch)
    {
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

        // Static Log.Logger is owned (and flushed) by Program.
        services.AddSerilog(Log.Logger, dispose: false);

        services.AddSingleton(app);
        services.AddSingleton<Dispatcher>(app.Dispatcher);
        services.AddSingleton(paths);
        services.AddSingleton(instance);
        services.AddSingleton(levelSwitch);

        services.AddSingleton<JsonSettingsStore>(sp =>
            JsonSettingsStore.Open(paths.SettingsFile, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<JsonSettingsStore>());

        services.AddSingleton(_ => new StartupRegistration(
            "Clicky",
            Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.")));

        services.AddSingleton<IAppShell, AppShell>();
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<TrayIconService>();

        services.AddHostedService<LogLevelBinder>();
        services.AddHostedService<StartWithWindowsService>();
        services.AddHostedService<ActivationListener>();

        return services;
    }
}
