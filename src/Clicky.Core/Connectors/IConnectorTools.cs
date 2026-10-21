using System.Text;
using System.Text.Json;

namespace Clicky.Core.Connectors;

/// <summary>
/// What the backends (M5) and the agent loop (M13) use: the tools of every connected MCP server,
/// ready to put in a Messages API "tools" array, and a way to call them. Names are unique and
/// already valid Claude tool names. Thread-safe.
/// </summary>
public interface IConnectorTools
{
    /// <summary>Snapshot of the tools of every connected, enabled account. Empty until something connects.</summary>
    IReadOnlyList<ConnectorTool> Tools { get; }

    /// <summary>Raised on a thread-pool thread whenever <see cref="Tools"/> changes.</summary>
    event EventHandler? ToolsChanged;

    /// <summary>
    /// <see cref="Tools"/>, after waiting up to <paramref name="maxWait"/> for connections that are
    /// still starting (so the first request after launch sees them). Never throws for a server that is down.
    /// </summary>
    Task<IReadOnlyList<ConnectorTool>> GetToolsAsync(TimeSpan maxWait, CancellationToken cancellationToken = default);

    /// <summary>True if <paramref name="toolName"/> is a connector tool (as opposed to a built-in action).</summary>
    bool IsConnectorTool(string toolName);

    /// <summary>
    /// Calls a tool by its model-facing name. Failures (unknown tool, server down, timeout, the
    /// server's own error) come back as <see cref="ConnectorToolResult.IsError"/> results with a
    /// message for Claude, so the agent loop can hand them straight back as a tool_result.
    /// Only <paramref name="cancellationToken"/> cancellation throws.
    /// </summary>
    /// <remarks>
    /// Does not ask for permission. The caller runs the permission gate first, using
    /// <see cref="ConnectorTool.NeedsConfirmation"/>.
    /// </remarks>
    Task<ConnectorToolResult> CallAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default);
}

/// <summary>One MCP tool as Claude sees it.</summary>
/// <param name="Name">Unique model-facing name, ^[a-zA-Z0-9_-]{1,64}$ (see <see cref="ConnectorNames"/>).</param>
/// <param name="Description">The server's description, prefixed with the connector (and account) it belongs to.</param>
/// <param name="InputSchema">JSON Schema of the arguments, an object schema as the Messages API expects.</param>
/// <param name="ReadOnly">The server says the tool only reads (MCP readOnlyHint).</param>
/// <param name="Destructive">The server says the tool may delete or overwrite (MCP destructiveHint; assumed when not read-only and unstated).</param>
/// <param name="OpenWorld">The tool reaches beyond the server (web, email, other people).</param>
public sealed record ConnectorTool(
    string Name,
    string Description,
    JsonElement InputSchema,
    string ConnectorId,
    string ConnectorName,
    string AccountId,
    string AccountLabel,
    string ServerToolName,
    string? Title,
    bool ReadOnly,
    bool Destructive,
    bool OpenWorld)
{
    /// <summary>
    /// Ask before running (Allow once / Always this conversation / Not now). Anything that is not
    /// declared read-only can send, change or delete, and MCP hints are only hints.
    /// </summary>
    public bool NeedsConfirmation => !ReadOnly;
}

public sealed record ConnectorToolResult(bool IsError, IReadOnlyList<ConnectorContent> Content, JsonElement? StructuredContent = null)
{
    public static ConnectorToolResult Error(string message) => new(true, [new ConnectorContent.Text(message)]);

    /// <summary>All text content (and placeholders for anything else) as one string, for logs and text-only backends.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        foreach (var block in Content)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(block switch
            {
                ConnectorContent.Text t => t.Value,
                ConnectorContent.Image i => $"[image {i.MimeType}]",
                ConnectorContent.Resource r => r.Contents ?? $"[resource {r.Uri}]",
                _ => string.Empty,
            });
        }

        if (builder.Length == 0 && StructuredContent is { } structured)
        {
            builder.Append(structured.GetRawText());
        }

        return builder.ToString();
    }
}

/// <summary>Content of a tool result, mapped to what a Messages API tool_result can carry.</summary>
public abstract record ConnectorContent
{
    private ConnectorContent()
    {
    }

    public sealed record Text(string Value) : ConnectorContent;

    /// <summary>Base64 image data, for an image block.</summary>
    public sealed record Image(string Base64Data, string MimeType) : ConnectorContent;

    /// <summary>An embedded or linked resource; <see cref="Contents"/> is null for binary or linked-only resources.</summary>
    public sealed record Resource(string Uri, string? MimeType, string? Contents) : ConnectorContent;
}
