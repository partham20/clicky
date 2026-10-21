using System.Text.Json;
using System.Text.Json.Serialization;

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
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
