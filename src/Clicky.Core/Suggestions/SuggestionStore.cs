using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Suggestions;

/// <summary>
/// Every idea shown and what the user did with it, plus which mornings had a card, in
/// %AppData%\Clicky\suggestions.json. The editor reads it so ideas aren't repeated and so it learns
/// what the user likes; the planner reads the mornings to back off. Entries older than
/// <see cref="KeepDays"/> are dropped. Thread-safe; every change is written straight away.
/// </summary>
public sealed class SuggestionStore
{
    public const int KeepDays = 120;

    private readonly string _file;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private List<Suggestion>? _suggestions;
    private List<MorningRecord>? _mornings;

    public SuggestionStore(string file, TimeProvider time, ILogger<SuggestionStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        _file = file;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<Suggestion> Suggestions
    {
        get
        {
            lock (_gate)
            {
                Load();
                return [.. _suggestions!];
            }
        }
    }

    public IReadOnlyList<MorningRecord> Mornings
    {
        get
        {
            lock (_gate)
            {
                Load();
                return [.. _mornings!];
            }
        }
    }

    public IReadOnlyList<Suggestion> ForDay(DateOnly day) => [.. Suggestions.Where(s => s.Day == day)];

    public MorningRecord? Morning(DateOnly day) => Mornings.LastOrDefault(m => m.Day == day);

    public void Add(IEnumerable<Suggestion> suggestions)
    {
        lock (_gate)
        {
            Load();
            _suggestions!.AddRange(suggestions);
            Save();
        }
    }

    /// <summary>Replaces the suggestion with <paramref name="id"/>; returns the new one, or null if it isn't there.</summary>
    public Suggestion? Update(string id, Func<Suggestion, Suggestion> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            Load();
            var index = _suggestions!.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return null;
            }

            var updated = change(_suggestions[index]);
            _suggestions[index] = updated;
            Save();
            return updated;
        }
    }

    public void SetMorning(MorningRecord morning)
    {
        ArgumentNullException.ThrowIfNull(morning);
        lock (_gate)
        {
            Load();
            _mornings!.RemoveAll(m => m.Day == morning.Day);
            _mornings.Add(morning);
            _mornings.Sort((a, b) => a.Day.CompareTo(b.Day));
            Save();
        }
    }

    private void Load()
    {
        if (_suggestions is not null)
        {
            return;
        }

        _suggestions = [];
        _mornings = [];
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }

            var file = JsonSerializer.Deserialize(File.ReadAllBytes(_file), SuggestionJsonContext.Default.SuggestionFile);
            _suggestions.AddRange((file?.Suggestions ?? []).Where(s => s is { Id: not null, Title: not null }).Select(Repair));
            _mornings.AddRange(file?.Mornings ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken file must not stop suggestions for good: keep a copy for the user and start over.
            _logger.LogWarning(ex, "Could not read {File}; starting a new suggestion history", _file);
            TryMoveAside();
        }
    }

    /// <summary>Fills in what a hand edit or an older file left null.</summary>
    private static Suggestion Repair(Suggestion s) => s with
    {
        Detail = s.Detail ?? string.Empty,
        Why = s.Why ?? string.Empty,
        Task = s.Task ?? s.Title,
        Topic = s.Topic ?? string.Empty,
        Sources = s.Sources ?? [],
        Adjustments = s.Adjustments ?? [],
    };

    private void Save()
    {
        var cutoff = DateOnly.FromDateTime(_time.GetLocalNow().DateTime).AddDays(-KeepDays);
        _suggestions!.RemoveAll(s => s.Day < cutoff);
        _mornings!.RemoveAll(m => m.Day < cutoff);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new SuggestionFile(_suggestions, _mornings), SuggestionJsonContext.Readable.SuggestionFile));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {File}", _file);
        }
    }

    private void TryMoveAside()
    {
        try
        {
            File.Move(_file, $"{_file}.{_time.GetLocalNow():yyyyMMdd-HHmmss}.bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not move {File} aside", _file);
        }
    }
}

internal sealed record SuggestionFile(List<Suggestion>? Suggestions, List<MorningRecord>? Mornings);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SuggestionFile))]
internal sealed partial class SuggestionJsonContext : JsonSerializerContext
{
    private static SuggestionJsonContext? s_readable;

    /// <summary>Unescaped text, so the file stays readable when people look at it.</summary>
    public static SuggestionJsonContext Readable => s_readable ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
