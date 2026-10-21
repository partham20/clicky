using System.Net.Http;
using System.Windows.Threading;
using Clicky.App.Annotations;
using Clicky.App.Buddy;
using Clicky.App.Ink;
using Clicky.App.Tray;
using Clicky.App.Views;
using Clicky.App.Walkthroughs;
using Clicky.Core;
using Clicky.Core.Annotations;
using Clicky.Core.Assistant;
using Clicky.Core.Assistant.ClaudeCode;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Connectors;
using Clicky.Core.Dictation;
using Clicky.Core.Hotkeys;
using Clicky.Core.Ink;
using Clicky.Core.Memory;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Clicky.Core.Skills;
using Clicky.Core.Voice;
using Clicky.Core.Walkthroughs;
using Clicky.Platform.Windows.Audio;
using Clicky.Platform.Windows.Capture;
using Clicky.Platform.Windows.Dictation;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Hotkeys;
using Clicky.Platform.Windows.Overlay;
using Clicky.Platform.Windows.Security;
using Clicky.Platform.Windows.Shell;
using Clicky.Platform.Windows.SingleInstance;
using Clicky.Platform.Windows.Speech;
using Clicky.Platform.Windows.Startup;
using Clicky.Platform.Windows.Walkthroughs;
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

        // Claude: both backends stay registered so Settings can switch between them live.
        services.AddSingleton<AnthropicApiBackend>();
        services.AddSingleton<ClaudeCodeBackend>();
        services.AddSingleton<ClaudeAssistant>();

        // Memory updates run on the active backend's fast model.
        services.AddSingleton<IMemoryModel>(sp => sp.GetRequiredService<ClaudeAssistant>());
        services.AddSingleton(sp => new MemoryStore(paths.MemoryDirectory, sp.GetRequiredService<ILogger<MemoryStore>>()));
        services.AddSingleton(sp => new MemoryService(
            sp.GetRequiredService<MemoryStore>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<ILogger<MemoryService>>(),
            sp.GetService<IMemoryModel>()));
        services.AddSingleton<IMemoryService>(sp => sp.GetRequiredService<MemoryService>());

        // M16 connectors: backends and the agent loop take IConnectorTools.
        services.AddSingleton<IBrowserLauncher, ShellBrowserLauncher>();
        services.AddSingleton<ConnectorSecrets>();
        services.AddSingleton<IMcpSessionFactory, SdkMcpSessionFactory>();
        services.AddSingleton<ConnectorHub>();
        services.AddSingleton<IConnectorTools>(sp => sp.GetRequiredService<ConnectorHub>());

        services.AddSingleton(sp => new PersonalVocabulary(paths.DictionaryFile, sp.GetRequiredService<ILogger<PersonalVocabulary>>()));
        services.AddSingleton(sp => new DictationHistory(paths, TimeProvider.System, sp.GetRequiredService<ILogger<DictationHistory>>()));
        services.AddSingleton<IDictationPolisher, ClaudeDictationPolisher>();
        services.AddSingleton<ScreenDrafter>();
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

        // M7 marks: safe to resolve on any thread; the overlay host is only touched on the UI thread.
        services.AddSingleton(sp => new Lazy<OverlayHost>(sp.GetRequiredService<OverlayHost>));
        services.AddSingleton<AnnotationRenderer>();
        services.AddSingleton<IAnnotationRenderer>(sp => sp.GetRequiredService<AnnotationRenderer>());
        services.AddSingleton<AnnotationDemo>();

        // M9 ink trail: the talk loop records the pointer during Talk and attaches it to the request.
        services.AddSingleton<InkTrailService>();
        services.AddSingleton<IInkTrailService>(sp => sp.GetRequiredService<InkTrailService>());
        services.AddSingleton<InkTrailDemo>();

        // M8 walkthroughs and app skills. The talk loop passes reply events to IWalkthroughs.Observe;
        // the engine's own "what next?" requests go through SwitchableWalkthroughTurns.
        services.AddSingleton(sp => new AppSkillLibrary(paths.SkillsDirectory, sp.GetRequiredService<ILogger<AppSkillLibrary>>()));
        services.AddSingleton<IAppSkills>(sp => sp.GetRequiredService<AppSkillLibrary>());
        services.AddSingleton<IForegroundApp, WindowsForegroundApp>();
        services.AddSingleton<IStepWatcher, WindowsStepWatcher>();
        services.AddSingleton(sp =>
        {
            var assistant = sp.GetRequiredService<ClaudeAssistant>();
            var presenter = sp.GetRequiredService<ReplyPresenter>();
            return new AssistantWalkthroughTurns(
                sp.GetRequiredService<ICaptureService>(),
                sp.GetRequiredService<ISettingsStore>(),
                sp.GetRequiredService<IAnnotationRenderer>(),
                sp.GetRequiredService<IBuddyPresence>(),
                sp.GetRequiredService<IAppSkills>(),
                sp.GetRequiredService<IForegroundApp>(),
                () => assistant.CurrentConversation(),
                assistant.ReplyAsync,
                () => new SpokenReplySpeech(presenter.Begin()),
                sp.GetRequiredService<ILogger<AssistantWalkthroughTurns>>());
        });
        services.AddSingleton<SwitchableWalkthroughTurns>();
        services.AddSingleton<IWalkthroughTurns>(sp => sp.GetRequiredService<SwitchableWalkthroughTurns>());
        services.AddSingleton(sp => new WalkthroughEngine(
            sp.GetRequiredService<IStepWatcher>(),
            sp.GetRequiredService<IAnnotationRenderer>(),
            sp.GetRequiredService<IWalkthroughTurns>(),
            sp.GetRequiredService<IBuddyPresence>(),
            sp.GetRequiredService<IForegroundApp>(),
            DisplayMonitors.GetAll,
            sp.GetRequiredService<ILogger<WalkthroughEngine>>()));
        services.AddSingleton<IWalkthroughs>(sp => sp.GetRequiredService<WalkthroughEngine>());
        services.AddSingleton<WalkthroughDemo>();
        services.AddTransient<DockedBuddyView>();
        services.AddSingleton<Func<DockedBuddyView>>(sp => sp.GetRequiredService<DockedBuddyView>);
        services.AddTransient<HotkeysSettingsView>();
        services.AddSingleton<Func<HotkeysSettingsView>>(sp => sp.GetRequiredService<HotkeysSettingsView>);
        services.AddTransient<VoiceSettingsView>();
        services.AddSingleton<Func<VoiceSettingsView>>(sp => sp.GetRequiredService<VoiceSettingsView>);
        services.AddTransient<SpeechSettingsView>();
        services.AddSingleton<Func<SpeechSettingsView>>(sp => sp.GetRequiredService<SpeechSettingsView>);
        services.AddTransient<ClaudeSettingsView>();
        services.AddSingleton<Func<ClaudeSettingsView>>(sp => sp.GetRequiredService<ClaudeSettingsView>);
        services.AddTransient<MemorySettingsView>();
        services.AddSingleton<Func<MemorySettingsView>>(sp => sp.GetRequiredService<MemorySettingsView>);
        services.AddTransient<ConnectorsSettingsView>();
        services.AddSingleton<Func<ConnectorsSettingsView>>(sp => sp.GetRequiredService<ConnectorsSettingsView>);
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
        services.AddHostedService<ConnectorStartup>();
        services.AddHostedService<DictationCoordinator>();
        services.AddHostedService<WalkthroughStartup>();

        return services;
    }
}
