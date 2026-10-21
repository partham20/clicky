using Clicky.Core.Assistant;

namespace Clicky.Core.Settings;

/// <summary>settings.claude: which backend answers, and which models the fast/deep router uses.</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record ClaudeSettings(
    ClaudeBackendKind Backend = ClaudeBackendKind.Api,
    ModelRouting Routing = ModelRouting.Auto,
    string ApiFastModel = ClaudeSettings.DefaultApiFastModel,
    string ApiDeepModel = ClaudeSettings.DefaultApiDeepModel,
    string SubscriptionFastModel = ClaudeSettings.DefaultSubscriptionFastModel,
    string SubscriptionDeepModel = ClaudeSettings.DefaultSubscriptionDeepModel,
    ClaudeEffort FastEffort = ClaudeEffort.Low,
    ClaudeEffort DeepEffort = ClaudeEffort.Medium,
    int MaxTurns = ClaudeSettings.DefaultMaxTurns,
    int IdleResetMinutes = ClaudeSettings.DefaultIdleResetMinutes,
    string CliPath = "")
{
    // Current IDs from the Claude docs (2026-10). Aliases on the subscription follow the newest model.
    public const string DefaultApiFastModel = "claude-haiku-5-5";
    public const string DefaultApiDeepModel = "claude-opus-5-5";
    public const string DefaultSubscriptionFastModel = "haiku";
    public const string DefaultSubscriptionDeepModel = "opus";
    public const int DefaultMaxTurns = 40;
    public const int DefaultIdleResetMinutes = 10;
    public const int MinMaxTurns = 2;
    public const int MaxMaxTurns = 200;

    public ClaudeBackendKind Backend { get; init; } = Backend;

    /// <summary>Auto picks the fast or deep model per question; the others pin one.</summary>
    public ModelRouting Routing { get; init; } = Routing;

    /// <summary>Messages API model for quick questions.</summary>
    public string ApiFastModel { get; init; } = ApiFastModel;

    /// <summary>Messages API model for hard, screen-heavy or agent questions.</summary>
    public string ApiDeepModel { get; init; } = ApiDeepModel;

    /// <summary>Claude Code model or alias (haiku, sonnet, opus) for quick questions.</summary>
    public string SubscriptionFastModel { get; init; } = SubscriptionFastModel;

    public string SubscriptionDeepModel { get; init; } = SubscriptionDeepModel;

    /// <summary>Thinking effort for the fast model on the API (Claude Code uses its own default).</summary>
    public ClaudeEffort FastEffort { get; init; } = FastEffort;

    public ClaudeEffort DeepEffort { get; init; } = DeepEffort;

    /// <summary>Turns of conversation sent back as history.</summary>
    public int MaxTurns { get; init; } = MaxTurns;

    /// <summary>A conversation idle this long starts over.</summary>
    public int IdleResetMinutes { get; init; } = IdleResetMinutes;

    /// <summary>Full path to claude.exe; empty finds it on PATH or in the usual install folders.</summary>
    public string CliPath { get; init; } = CliPath;

    public ClaudeSettings Normalize() => this with
    {
        Backend = Enum.IsDefined(Backend) ? Backend : ClaudeBackendKind.Api,
        Routing = Enum.IsDefined(Routing) ? Routing : ModelRouting.Auto,
        ApiFastModel = Clean(ApiFastModel, DefaultApiFastModel),
        ApiDeepModel = Clean(ApiDeepModel, DefaultApiDeepModel),
        SubscriptionFastModel = Clean(SubscriptionFastModel, DefaultSubscriptionFastModel),
        SubscriptionDeepModel = Clean(SubscriptionDeepModel, DefaultSubscriptionDeepModel),
        FastEffort = Enum.IsDefined(FastEffort) ? FastEffort : ClaudeEffort.Low,
        DeepEffort = Enum.IsDefined(DeepEffort) ? DeepEffort : ClaudeEffort.Medium,
        MaxTurns = Math.Clamp(MaxTurns, MinMaxTurns, MaxMaxTurns),
        IdleResetMinutes = Math.Clamp(IdleResetMinutes, 1, 24 * 60),
        CliPath = CliPath?.Trim().Trim('"') ?? string.Empty,
    };

    public string ModelFor(ModelTier tier) => (Backend, tier) switch
    {
        (ClaudeBackendKind.Subscription, ModelTier.Deep) => SubscriptionDeepModel,
        (ClaudeBackendKind.Subscription, _) => SubscriptionFastModel,
        (_, ModelTier.Deep) => ApiDeepModel,
        _ => ApiFastModel,
    };

    public ClaudeEffort EffortFor(ModelTier tier) => tier == ModelTier.Deep ? DeepEffort : FastEffort;

    private static string Clean(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
