using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Clicky.Core.Settings;

// Source-generated: no reflection at startup, trim/AOT friendly. Comments and trailing commas are
// accepted because people hand-edit this file.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ClickySettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
    /// <summary>
    /// For writing: the default encoder escapes '+' as +, which would turn "Ctrl+Alt" into
    /// unreadable text in a file people edit by hand. The file is local and never embedded in HTML,
    /// so relaxed escaping is safe.
    /// </summary>
    public static JsonTypeInfo<ClickySettings> ReadableClickySettings => s_readable ??= CreateReadable();

    // Lazy: static initializers in the generated half of this partial class may run after ours.
    private static JsonTypeInfo<ClickySettings>? s_readable;

    private static JsonTypeInfo<ClickySettings> CreateReadable()
    {
        var options = new JsonSerializerOptions(Default!.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return (JsonTypeInfo<ClickySettings>)options.GetTypeInfo(typeof(ClickySettings));
    }
}
