using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Dictation;

/// <summary>A word or phrase dictation should get right.</summary>
/// <param name="Phrase">How it is written: "Partha", "GitHub", "WASAPI".</param>
/// <param name="SoundsLike">What speech-to-text writes instead ("Parta", "git hub"); replaced with <paramref name="Phrase"/>.</param>
/// <param name="Learned">Added by Clicky from a correction rather than by hand.</param>
public sealed record DictionaryEntry(string Phrase, IReadOnlyList<string>? SoundsLike = null, bool Learned = false)
{
    public IReadOnlyList<string> SoundsLike { get; init; } = SoundsLike ?? [];

    public bool Equals(DictionaryEntry? other) =>
        other is not null
        && Phrase == other.Phrase
        && Learned == other.Learned
        && SoundsLike.SequenceEqual(other.SoundsLike, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Phrase, Learned, SoundsLike.Count);
}

/// <summary>
/// The personal dictionary (dictionary.json): words to bias speech-to-text towards, and fixes for
/// what it mishears. Hand-editable; Clicky adds words it learns from corrections.
/// </summary>
/// <remarks>
/// Thread-safe. A hand edit is picked up the next time the dictionary is used (the file's write time
/// is checked, nothing is watched). A file that cannot be read is logged and left alone: Clicky
/// never overwrites a file it could not parse.
/// </remarks>
public sealed partial class PersonalVocabulary
{
    /// <summary>Whisper's prompt is limited to 224 tokens; this many characters stays well inside it.</summary>
    public const int MaxPromptChars = 600;

    public const int MaxEntries = 2000;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private System.Collections.ObjectModel.ReadOnlyCollection<DictionaryEntry> _entries = new([]);
    private Rule[] _rules = [];
    private DateTime _loadedWriteTime = DateTime.MinValue;
    private bool _unreadable;

    public PersonalVocabulary(string path, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger;
    }

    /// <summary>Raised after the entries change (a save, a learned word, a hand edit noticed).</summary>
    public event EventHandler? Changed;

    public string FilePath => _path;

    public IReadOnlyList<DictionaryEntry> Entries
    {
        get
        {
            Refresh();
            lock (_gate)
            {
                return _entries;
            }
        }
    }

    /// <summary>
    /// A prompt for speech-to-text that names the dictionary's phrases, most recent first, so the
    /// engine prefers those spellings. Empty when the dictionary is.
    /// </summary>
    public string BuildPrompt()
    {
        var phrases = Entries.Select(e => e.Phrase).Reverse().Distinct(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var phrase in phrases)
        {
            if (builder.Length + phrase.Length + 2 > MaxPromptChars)
            {
                break;
            }

            builder.Append(builder.Length == 0 ? string.Empty : ", ").Append(phrase);
        }

        return builder.Length == 0 ? string.Empty : builder.Append('.').ToString();
    }

    /// <summary>Replaces mishearings with the dictionary's spellings, whole words only, ignoring case.</summary>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Refresh();
        Rule[] rules;
        lock (_gate)
        {
            rules = _rules;
        }

        foreach (var rule in rules)
        {
            text = rule.Pattern.Replace(text, rule.Phrase);
        }

        return text;
    }

    /// <summary>Replaces every entry and saves. Returns false (and logs) if the file could not be written.</summary>
    public bool Save(IEnumerable<DictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var clean = Clean(entries);
        lock (_gate)
        {
            if (!Write(clean))
            {
                return false;
            }

            Set(clean);
            _unreadable = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Remembers that speech-to-text wrote <paramref name="heard"/> where the user meant
    /// <paramref name="written"/>. Returns false if there was nothing new to learn.
    /// </summary>
    public bool Learn(string heard, string written)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heard);
        ArgumentException.ThrowIfNullOrWhiteSpace(written);
        heard = heard.Trim();
        written = written.Trim();
        Refresh();

        lock (_gate)
        {
            if (_unreadable)
            {
                _logger.LogWarning("Dictionary: not learning \"{Written}\" because {Path} could not be read", written, _path);
                return false;
            }

            var entries = _entries.ToList();
            var index = entries.FindIndex(e => string.Equals(e.Phrase, written, StringComparison.Ordinal));
            if (index >= 0)
            {
                var entry = entries[index];
                var covered = entry.SoundsLike.Contains(heard, StringComparer.OrdinalIgnoreCase)
                    || (string.Equals(heard, written, StringComparison.OrdinalIgnoreCase) && HasDistinctiveCase(written));
                if (covered)
                {
                    return false;
                }

                entries[index] = entry with { SoundsLike = [.. entry.SoundsLike, heard] };
            }
            else
            {
                entries.Add(new DictionaryEntry(written, [heard], Learned: true));
            }

            var clean = Clean(entries);
            if (!Write(clean))
            {
                return false;
            }

            Set(clean);
        }

        _logger.LogInformation("Dictionary: learned \"{Heard}\" -> \"{Written}\"", heard, written);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static List<DictionaryEntry> Clean(IEnumerable<DictionaryEntry> entries)
    {
        var result = new List<DictionaryEntry>();
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Phrase))
            {
                continue;
            }

            var phrase = entry.Phrase.Trim();
            var soundsLike = (entry.SoundsLike ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Where(s => !string.Equals(s, phrase, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var existing = result.FindIndex(e => string.Equals(e.Phrase, phrase, StringComparison.Ordinal));
            if (existing >= 0)
            {
                result[existing] = result[existing] with { SoundsLike = [.. result[existing].SoundsLike.Union(soundsLike, StringComparer.Ordinal)] };
                continue;
            }

            result.Add(new DictionaryEntry(phrase, soundsLike, entry.Learned));
        }

        // Oldest learned words go first when the list is full; words added by hand are kept.
        while (result.Count > MaxEntries && result.FindIndex(e => e.Learned) is var oldest and >= 0)
        {
            result.RemoveAt(oldest);
        }

        return result;
    }

    private void Refresh()
    {
        DateTime writeTime;
        try
        {
            writeTime = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (_gate)
        {
            if (writeTime == _loadedWriteTime)
            {
                return;
            }

            _loadedWriteTime = writeTime;
            if (writeTime == DateTime.MinValue)
            {
                Set([]);
                _unreadable = false;
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(_path);
                var file = JsonSerializer.Deserialize(bytes, DictionaryJsonContext.Default.DictionaryFile);
                Set(Clean(file?.Words ?? []));
                _unreadable = false;
                _logger.LogDebug("Dictionary: loaded {Count} entries from {Path}", _entries.Count, _path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _unreadable = true;
                _logger.LogWarning(ex, "Dictionary: could not read {Path}; using the last good entries", _path);
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Under the lock.
    private void Set(List<DictionaryEntry> entries)
    {
        _entries = entries.AsReadOnly();
        var rules = new List<Rule>();
        foreach (var entry in entries)
        {
            var variants = entry.SoundsLike.ToList();
            if (HasDistinctiveCase(entry.Phrase))
            {
                // "github" and "Github" become "GitHub" without anyone having to list them.
                variants.Add(entry.Phrase);
            }

            foreach (var variant in variants.OrderByDescending(v => v.Length))
            {
                var words = variant.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
                var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"[\s-]+", words) + @"(?![\p{L}\p{N}])";
                rules.Add(new Rule(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), entry.Phrase.Replace("$", "$$", StringComparison.Ordinal)));
            }
        }

        _rules = [.. rules];
    }

    /// <summary>"GitHub", "iPhone", "WASAPI": casing no ordinary sentence would produce, so it is safe to enforce.</summary>
    private static bool HasDistinctiveCase(string phrase) =>
        phrase.Skip(1).Any(char.IsUpper) && phrase.Any(char.IsLetter);

    // Under the lock.
    private bool Write(List<DictionaryEntry> entries)
    {
        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new DictionaryFile(entries), DictionaryJsonContext.Readable.DictionaryFile);
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }

            _loadedWriteTime = File.GetLastWriteTimeUtc(_path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Dictionary: could not save {Path}", _path);
            return false;
        }
    }

    private sealed record Rule(Regex Pattern, string Phrase);
}

/// <summary>dictionary.json.</summary>
public sealed record DictionaryFile(IReadOnlyList<DictionaryEntry>? Words)
{
    public IReadOnlyList<DictionaryEntry> Words { get; init; } = Words ?? [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(DictionaryFile))]
internal sealed partial class DictionaryJsonContext : JsonSerializerContext
{
    /// <summary>Relaxed escaping so "C++" and accents stay readable in a file people edit by hand.</summary>
    public static DictionaryJsonContext Readable => s_readable ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    // Lazy: static initializers in the generated half of this partial class may run after ours.
    private static DictionaryJsonContext? s_readable;
}
