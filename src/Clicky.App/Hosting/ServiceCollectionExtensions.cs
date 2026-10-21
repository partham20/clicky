using System.Net.Http;
using System.Windows.Threading;
using Clicky.App.Buddy;
using Clicky.App.Tray;
using Clicky.App.Views;
using Clicky.Core;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Dictation;
using Clicky.Core.Hotkeys;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Audio;
using Clicky.Platform.Windows.Capture;
using Clicky.Platform.Windows.Dictation;
using Clicky.Platform.Windows.Hotkeys;
using Clicky.Platform.Windows.Overlay;
using Clicky.Platform.Windows.Security;
using Clicky.Platform.Windows.SingleInstance;
using Clicky.Platform.Windows.Speech;
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

        services.AddSingleton<IBuddyPresence, BuddyPresence>();
        services.AddSingleton<IBuddySkin, PointerBuddySkin>();

        services.AddSingleton<WindowsHotkeyService>();
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<WindowsHotkeyService>());
        services.AddSingleton<WindowsCaptureService>();
        services.AddSingleton<ICaptureService>(sp => sp.GetRequiredService<WindowsCaptureService>());

        // Model downloads run for minutes, so no client-wide timeout; OpenAI requests set their own.
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        services.AddSingleton<ISecretStore, CredentialSecretStore>();
        services.AddSingleton<IMicrophone, WasapiMicrophone>();
        services.AddSingleton(sp => new ModelDownloader(
            sp.GetRequiredService<HttpClient>(),
            paths.ModelsDirectory,
            sp.GetRequiredService<ILogger<ModelDownloader>>()));
        services.AddSingleton<WhisperSpeechToText>();
        services.AddSingleton<OpenAiSpeechToText>();
        services.AddSingleton<Func<SpeechEngine, ISpeechToText>>(sp => engine => engine == SpeechEngine.Cloud
            ? sp.GetRequiredService<OpenAiSpeechToText>()
            : sp.GetRequiredService<WhisperSpeechToText>());
        services.AddSingleton<VoiceInput>();

        services.AddSingleton<ISpeaker, WasapiSpeaker>();
        services.AddSingleton<WindowsTextToSpeech>();
        services.AddSingleton<OpenAiTextToSpeech>();
        services.AddSingleton<Func<VoiceOutputEngine, ITextToSpeech>>(sp => engine => engine == VoiceOutputEngine.Cloud
            ? sp.GetRequiredService<OpenAiTextToSpeech>()
            : sp.GetRequiredService<WindowsTextToSpeech>());
        services.AddSingleton<IQuietDetector, WindowsQuietDetector>();
        services.AddSingleton<VoiceOutput>();
        services.AddSingleton<ReplyPresenter>();

        services.AddSingleton(sp => new PersonalVocabulary(paths.DictionaryFile, sp.GetRequiredService<ILogger<PersonalVocabulary>>()));
        services.AddSingleton(sp => new DictationHistory(paths, TimeProvider.System, sp.GetRequiredService<ILogger<DictationHistory>>()));
        services.AddSingleton<IDictationPolisher, RulesOnlyPolisher>();
        services.AddSingleton(sp => new DictationInput(
            sp.GetRequiredService<IMicrophone>(),
            sp.GetRequiredService<Func<SpeechEngine, ISpeechToText>>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<PersonalVocabulary>(),
            sp.GetRequiredService<DictationHistory>(),
            sp.GetRequiredService<IDictationPolisher>(),
            sp.GetRequiredService<ILogger<DictationInput>>()));
        services.AddSingleton(sp => new CorrectionWatcher(sp.GetRequiredService<PersonalVocabulary>(), sp.GetRequiredService<ILogger<CorrectionWatcher>>()));
        services.AddSingleton<WindowsTextInserter>();
        services.AddSingleton<ITextInserter>(sp => sp.GetRequiredService<WindowsTextInserter>());

        // UI-thread objects: resolved on the UI thread by Program (OverlayHost creates an HWND).
        services.AddSingleton<OverlayHost>();
        services.AddSingleton<FloatingBuddy>();
        services.AddTransient<DockedBuddyView>();
        services.AddSingleton<Func<DockedBuddyView>>(sp => sp.GetRequiredService<DockedBuddyView>);
        services.AddTransient<HotkeysSettingsView>();
        services.AddSingleton<Func<HotkeysSettingsView>>(sp => sp.GetRequiredService<HotkeysSettingsView>);
        services.AddTransient<VoiceSettingsView>();
        services.AddSingleton<Func<VoiceSettingsView>>(sp => sp.GetRequiredService<VoiceSettingsView>);
        services.AddTransient<SpeechSettingsView>();
        services.AddSingleton<Func<SpeechSettingsView>>(sp => sp.GetRequiredService<SpeechSettingsView>);
        services.AddTransient<DictationSettingsView>();
        services.AddSingleton<Func<DictationSettingsView>>(sp => sp.GetRequiredService<DictationSettingsView>);

        services.AddSingleton<IAppShell, AppShell>();
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<IUserNotifier, UserNotifier>();

        services.AddHostedService<LogLevelBinder>();
        services.AddHostedService<StartWithWindowsService>();
        services.AddHostedService<ActivationListener>();
        services.AddHostedService<HotkeyCoordinator>();
        services.AddHostedService<HotkeySettingsBinder>();
        services.AddHostedService<VoiceStartup>();
        services.AddHostedService<DictationCoordinator>();

        return services;
    }
}
