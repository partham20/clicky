using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Clicky.App.Buddy;
using Clicky.App.Hosting;
using Clicky.App.TextMode;
using Clicky.App.Tray;
using Clicky.Core;
using Clicky.Platform.Windows;
using Clicky.Platform.Windows.Overlay;
using Clicky.Platform.Windows.SingleInstance;
using Clicky.Platform.Windows.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Velopack;

namespace Clicky.App;

public static class Program
{
    private const string AppId = "Clicky";

    [STAThread]
    public static int Main(string[] args)
    {
        // First, before anything touches disk or UI: Velopack's install/update/uninstall hooks run
        // here and exit, and an update downloaded last session is applied (restarting into it).
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => RemoveStartWithWindows())
            .Run();

        // Single instance check runs before logging/host so a second copy has no side effects.
        using var instance = SingleInstanceGuard.Acquire(InstanceId());
        if (!instance.IsPrimary)
        {
            ForegroundRights.GrantToAnyProcess();
            var message = args.Contains("--settings", StringComparer.OrdinalIgnoreCase) ? SingleInstanceGuard.SettingsMessage : SingleInstanceGuard.ShowMessage;
            instance.TrySignalPrimary(message, TimeSpan.FromSeconds(2));
            return 0;
        }

        var paths = ClickyPaths.CreateDefault();
        paths.EnsureCreated();

        var levelSwitch = new LoggingLevelSwitch();
        Log.Logger = Logging.CreateLogger(paths, levelSwitch);
        Log.Information("Clicky {Version} starting (pid {Pid}, args {Args})", typeof(Program).Assembly.GetName().Version, Environment.ProcessId, args);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        try
        {
            var app = new App();
            app.InitializeComponent();
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;

            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddClicky(app, paths, instance, levelSwitch);

            using var host = builder.Build();

            // Off the UI thread: hosted services must never be able to deadlock on the dispatcher
            // context while we block here.
            Task.Run(() => host.StartAsync()).GetAwaiter().GetResult();

            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(() => app.Dispatcher.InvokeAsync(app.Shutdown));

            var tray = host.Services.GetRequiredService<TrayIconService>();
            var buddy = host.Services.GetRequiredService<FloatingBuddy>();
            var hiddenBuddyCaption = host.Services.GetRequiredService<HiddenBuddyCaption>();
            var dropPad = host.Services.GetRequiredService<DropPad>();
            app.Startup += (_, _) =>
            {
                tray.Show();
                buddy.Start();
                hiddenBuddyCaption.Start();
                dropPad.Start();
            };
            app.Exit += (_, _) =>
            {
                dropPad.Dispose();
                hiddenBuddyCaption.Dispose();
                buddy.Dispose();
                host.Services.GetRequiredService<OverlayHost>().Dispose();
                tray.Dispose();
            };

            var exitCode = app.Run();

            Task.Run(() => host.StopAsync()).GetAwaiter().GetResult();
            Log.Information("Clicky exited with code {ExitCode}", exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Clicky failed to start");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // A separate data folder (CLICKY_DATA_DIR: tests, a dev build beside the installed one) is a
    // separate instance, so it neither hands off to nor blocks the everyday copy.
    private static string InstanceId()
    {
        var dataDir = Environment.GetEnvironmentVariable("CLICKY_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            return AppId;
        }

        var key = Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\').ToUpperInvariant());
        return $"{AppId}-{Convert.ToHexString(SHA256.HashData(key))[..12]}";
    }

    // Uninstall must not leave a Run entry pointing at a deleted exe. No logging yet at this point.
    private static void RemoveStartWithWindows()
    {
        try
        {
            new StartupRegistration(AppId, Environment.ProcessPath ?? AppId).Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Nothing to report to: the uninstaller carries on regardless.
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A tray app that dies silently is worse than one that logs and carries on.
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }
}
