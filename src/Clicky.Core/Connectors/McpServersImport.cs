using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Clicky.Core.Settings;

namespace Clicky.Core.Connectors;

/// <summary>
/// Reads the "mcpServers" JSON that Claude Desktop (claude_desktop_config.json), Claude Code
/// (.mcp.json) and most MCP server READMEs use, so a server can be pasted instead of retyped.
/// Environment values and credential-looking headers become secrets, never settings.
/// </summary>
public static partial class McpServersImport
{
    /// <summary>One server ready to save, with the secrets for its first account.</summary>
    public sealed record Imported(ConnectorConfig Config, IReadOnlyDictionary<string, string> SecretEnvironment, IReadOnlyDictionary<string, string> SecretHeaders);

    /// <summary>%AppData%\Claude\claude_desktop_config.json.</summary>
    public static string ClaudeDesktopConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

    /// <summary>
    /// Accepts {"mcpServers": {name: server}}, a bare {name: server} map, or a single server object.
    /// A server is {"command", "args", "env", "cwd"} or {"url", "type", "headers"}.
    /// </summary>
    public static IReadOnlyList<Imported> Parse(string json, out IReadOnlyList<string> problems)
    {
        var issues = new List<string>();
        problems = issues;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            issues.Add("That is not valid JSON: " + ex.Message);
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add("Expected a JSON object with an \"mcpServers\" section.");
                return [];
            }

            if (root.TryGetProperty("mcpServers", out var servers) || root.TryGetProperty("servers", out servers))
            {
                root = servers;
            }

            if (IsServer(root))
            {
                return Single(ParseServer("server", root, issues));
            }

            var imported = new List<Imported>();
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object || !IsServer(property.Value))
                {
                    issues.Add($"{property.Name}: no \"command\" or \"url\", skipped.");
                    continue;
                }

                if (ParseServer(property.Name, property.Value, issues) is { } server)
                {
                    imported.Add(server);
                }
            }

            if (imported.Count == 0 && issues.Count == 0)
            {
                issues.Add("No servers found.");
            }

            return imported;
        }
    }

    private static IReadOnlyList<Imported> Single(Imported? server) => server is null ? [] : [server];

    private static bool IsServer(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && (element.TryGetProperty("command", out _) || element.TryGetProperty("url", out _));

    private static Imported? ParseServer(string name, JsonElement server, List<string> issues)
    {
        var url = String(server, "url");
        var type = String(server, "type")?.ToLowerInvariant();
        if (url is not null || type is "http" or "streamable-http" or "streamablehttp" or "sse")
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                issues.Add($"{name}: \"{url}\" is not an http(s) address, skipped.");
                return null;
            }

            var plain = new Dictionary<string, string>(StringComparer.Ordinal);
            var secret = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (header, value) in Map(server, "headers"))
            {
                (LooksSecret(header) ? secret : plain)[header] = value;
            }

            var config = new ConnectorConfig(
                Name: name,
                Transport: ConnectorTransport.Http,
                Url: uri.AbsoluteUri,
                Headers: plain,
                SecretHeaders: [.. secret.Keys],

                // No credentials given: sign in through the browser if the server asks for it.
                Auth: secret.Count == 0 ? ConnectorAuth.OAuth : ConnectorAuth.None);
            return new Imported(config.Normalize(), new Dictionary<string, string>(), secret);
        }

        var command = String(server, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            issues.Add($"{name}: empty \"command\", skipped.");
            return null;
        }

        var arguments = new List<string>();
        if (server.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            arguments.AddRange(args.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText()));
        }

        // Every env value is treated as secret: READMEs put tokens there, and nothing is lost by
        // keeping a harmless value in Credential Manager instead of settings.json.
        var environment = Map(server, "env");
        var stdio = new ConnectorConfig(
            Name: name,
            Transport: ConnectorTransport.Stdio,
            Command: command,
            Arguments: arguments,
            WorkingDirectory: String(server, "cwd") ?? string.Empty,
            SecretEnvironment: [.. environment.Keys]);
        return new Imported(stdio.Normalize(), environment, new Dictionary<string, string>());
    }

    private static bool LooksSecret(string header) => SecretHeaderPattern().IsMatch(header);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Dictionary<string, string> Map(JsonElement element, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            }
        }

        return map;
    }

    [GeneratedRegex("auth|key|token|secret|cookie|password", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretHeaderPattern();
}

/// <summary>Windows command-line quoting for the one-line arguments box in Settings.</summary>
public static class CommandLine
{
    /// <summary>Splits like CommandLineToArgvW: spaces separate, quotes group, \" is a literal quote.</summary>
    public static IReadOnlyList<string> Split(string? line)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                var start = i;
                while (i < line.Length && line[i] == '\\')
                {
                    i++;
                }

                var slashes = i - start;
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }

                hasToken = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>The inverse of <see cref="Split"/>: quotes only what needs it.</summary>
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            builder.Append('\\', c == '"' ? (slashes * 2) + 1 : slashes);
            slashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', slashes * 2).Append('"');
        return builder.ToString();
    }
}
