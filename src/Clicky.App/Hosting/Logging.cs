using System.Globalization;
using Clicky.Core;
using Clicky.Core.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Clicky.App.Hosting;

internal static class Logging
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Created before the host so startup failures are logged. Level is driven at runtime by
    /// settings.logging.minimumLevel through <paramref name="levelSwitch"/>.
    /// </summary>
    public static Logger CreateLogger(ClickyPaths paths, LoggingLevelSwitch levelSwitch) =>
        new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "clicky-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: OutputTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .WriteTo.Debug(outputTemplate: OutputTemplate, formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();

    public static LogEventLevel ToSerilog(this LogLevelSetting level) => level switch
    {
        LogLevelSetting.Verbose => LogEventLevel.Verbose,
        LogLevelSetting.Debug => LogEventLevel.Debug,
        LogLevelSetting.Information => LogEventLevel.Information,
        LogLevelSetting.Warning => LogEventLevel.Warning,
        LogLevelSetting.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };
}
