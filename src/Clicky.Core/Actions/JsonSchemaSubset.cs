using System.Globalization;
using System.Text.Json;

namespace Clicky.Core.Actions;

/// <summary>
/// Validates JSON against the subset of JSON Schema 2020-12 the action schema uses: type, const,
/// enum, properties, required, additionalProperties: false, items, min/maxItems, min/maxLength,
/// minimum/maximum, oneOf and local $ref. Anything else in a schema is an error at load time, so the
/// schema can't quietly grow a rule this validator ignores.
/// </summary>
internal static class JsonSchemaSubset
{
    private static readonly HashSet<string> s_known =
    [
        "$schema", "$id", "$defs", "$ref", "title", "description", "version",
        "type", "const", "enum", "properties", "required", "additionalProperties",
        "items", "minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum", "oneOf",
    ];

    /// <summary>Throws if <paramref name="schema"/> uses a keyword this validator does not implement.</summary>
    public static void EnsureSupported(JsonElement schema, string path = "#")
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (!s_known.Contains(property.Name))
            {
                throw new InvalidOperationException($"Schema keyword '{property.Name}' at {path} is not supported by the action validator.");
            }

            switch (property.Name)
            {
                case "$defs" or "properties":
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        EnsureSupported(child.Value, $"{path}/{property.Name}/{child.Name}");
                    }

                    break;
                case "items":
                    EnsureSupported(property.Value, $"{path}/items");
                    break;
                case "oneOf":
                    var i = 0;
                    foreach (var child in property.Value.EnumerateArray())
                    {
                        EnsureSupported(child, $"{path}/oneOf/{i++}");
                    }

                    break;
                case "additionalProperties" when property.Value.ValueKind != JsonValueKind.False:
                    throw new InvalidOperationException($"{path}: additionalProperties must be false.");
            }
        }
    }

    /// <returns>Null when <paramref name="instance"/> is valid, else the first problem found, e.g. "x: must be at least 0".</returns>
    public static string? Validate(JsonElement instance, JsonElement schema, JsonElement root, string path = "")
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            schema = Resolve(root, reference.GetString()!);
        }

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = 0;
            string? firstError = null;
            foreach (var option in oneOf.EnumerateArray())
            {
                var error = Validate(instance, option, root, path);
                if (error is null)
                {
                    matches++;
                }
                else
                {
                    firstError ??= error;
                }
            }

            if (matches != 1)
            {
                return matches == 0 ? firstError : $"{Name(path)}: matches more than one schema";
            }
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(instance, constant))
        {
            return $"{Name(path)}: must be {constant.GetRawText()}";
        }

        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => JsonElement.DeepEquals(v, instance)))
        {
            return $"{Name(path)}: must be one of {string.Join(", ", values.EnumerateArray().Select(v => v.GetRawText()))}";
        }

        if (schema.TryGetProperty("type", out var type) && CheckType(instance, type.GetString()!) is { } typeError)
        {
            return $"{Name(path)}: {typeError}";
        }

        return instance.ValueKind switch
        {
            JsonValueKind.Object => ValidateObject(instance, schema, root, path),
            JsonValueKind.Array => ValidateArray(instance, schema, root, path),
            JsonValueKind.String => ValidateString(instance, schema, path),
            JsonValueKind.Number => ValidateNumber(instance, schema, path),
            _ => null,
        };
    }

    public static JsonElement Resolve(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Only local $ref is supported: {reference}");
        }

        var node = root;
        foreach (var part in reference[2..].Split('/'))
        {
            node = node.GetProperty(part);
        }

        return node;
    }

    private static string? CheckType(JsonElement instance, string type) => type switch
    {
        "object" when instance.ValueKind != JsonValueKind.Object => "must be an object",
        "array" when instance.ValueKind != JsonValueKind.Array => "must be an array",
        "string" when instance.ValueKind != JsonValueKind.String => "must be a string",
        "number" when instance.ValueKind != JsonValueKind.Number => "must be a number",
        "integer" when instance.ValueKind != JsonValueKind.Number || !instance.TryGetInt64(out _) => "must be a whole number",
        "boolean" when instance.ValueKind is not (JsonValueKind.True or JsonValueKind.False) => "must be true or false",
        "object" or "array" or "string" or "number" or "integer" or "boolean" => null,
        _ => throw new InvalidOperationException($"Unsupported schema type '{type}'."),
    };

    private static string? ValidateObject(JsonElement instance, JsonElement schema, JsonElement root, string path)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray())
            {
                if (!instance.TryGetProperty(name.GetString()!, out _))
                {
                    return $"{Name(Join(path, name.GetString()!))}: is required";
                }
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties);
        var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        foreach (var property in instance.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                if (Validate(property.Value, propertySchema, root, Join(path, property.Name)) is { } error)
                {
                    return error;
                }
            }
            else if (closed)
            {
                return $"{Name(Join(path, property.Name))}: is not allowed";
            }
        }

        return null;
    }

    private static string? ValidateArray(JsonElement instance, JsonElement schema, JsonElement root, string path)
    {
        var count = instance.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32())
        {
            return $"{Name(path)}: needs at least {min.GetInt32()} items";
        }

        if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())
        {
            return $"{Name(path)}: allows at most {max.GetInt32()} items";
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in instance.EnumerateArray())
            {
                if (Validate(item, items, root, $"{path}[{i++}]") is { } error)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static string? ValidateString(JsonElement instance, JsonElement schema, string path)
    {
        var length = instance.GetString()!.Length;
        if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32())
        {
            return min.GetInt32() == 1 ? $"{Name(path)}: must not be empty" : $"{Name(path)}: needs at least {min.GetInt32()} characters";
        }

        if (schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32())
        {
            return $"{Name(path)}: allows at most {max.GetInt32()} characters";
        }

        return null;
    }

    private static string? ValidateNumber(JsonElement instance, JsonElement schema, string path)
    {
        var value = instance.GetDouble();
        if (!double.IsFinite(value))
        {
            return $"{Name(path)}: must be finite";
        }

        if (schema.TryGetProperty("minimum", out var min) && value < min.GetDouble())
        {
            return $"{Name(path)}: must be at least {min.GetDouble().ToString(CultureInfo.InvariantCulture)}";
        }

        if (schema.TryGetProperty("maximum", out var max) && value > max.GetDouble())
        {
            return $"{Name(path)}: must be at most {max.GetDouble().ToString(CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static string Name(string path) => path.Length == 0 ? "action" : path;
}
