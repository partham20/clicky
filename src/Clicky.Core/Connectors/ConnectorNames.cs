using System.Security.Cryptography;
using System.Text;

namespace Clicky.Core.Connectors;

/// <summary>
/// Ids and the tool names Claude sees. The Messages API allows tool names matching
/// ^[a-zA-Z0-9_-]{1,64}$, so a connector tool is "&lt;connector&gt;__&lt;tool&gt;", or
/// "&lt;connector&gt;-&lt;account&gt;__&lt;tool&gt;" when the connector has several accounts on.
/// </summary>
public static class ConnectorNames
{
    public const int MaxToolNameLength = 64;
    public const int MaxSlugLength = 24;
    public const string Separator = "__";

    /// <summary>Lowercase [a-z0-9-], no leading/trailing/double dashes, at most <see cref="MaxSlugLength"/> characters.</summary>
    public static string Slug(string? text, string fallback)
    {
        var builder = new StringBuilder();
        foreach (var c in (text ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '-' && (c is ' ' or '-' or '_' or '.' or '/' or '@' or ':'))
            {
                builder.Append('-');
            }

            if (builder.Length >= MaxSlugLength)
            {
                break;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? fallback : slug;
    }

    /// <summary><paramref name="slug"/>, or slug-2, slug-3… when it is already in <paramref name="used"/>.</summary>
    public static string Unique(string slug, IReadOnlySet<string> used)
    {
        if (!used.Contains(slug))
        {
            return slug;
        }

        for (var n = 2; ; n++)
        {
            var suffix = "-" + n;
            var candidate = (slug.Length + suffix.Length > MaxSlugLength ? slug[..(MaxSlugLength - suffix.Length)] : slug) + suffix;
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>"linear" for a single account, "linear-work" when several accounts are on.</summary>
    public static string Prefix(string connectorId, string accountId, bool multipleAccounts) =>
        multipleAccounts ? $"{connectorId}-{accountId}" : connectorId;

    /// <summary>
    /// The model-facing name: prefix + "__" + the server's tool name with anything outside
    /// [a-zA-Z0-9_-] replaced. Too long and the tail is swapped for a short hash so distinct tools stay distinct.
    /// </summary>
    public static string ToolName(string prefix, string serverToolName)
    {
        var builder = new StringBuilder(prefix.Length + Separator.Length + serverToolName.Length);
        builder.Append(prefix).Append(Separator);
        foreach (var c in serverToolName)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        var name = builder.ToString();
        if (name.Length <= MaxToolNameLength)
        {
            return name;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + "\n" + serverToolName)))[..8];
        return name[..(MaxToolNameLength - 9)] + "_" + hash;
    }
}
