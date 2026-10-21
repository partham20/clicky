using System.Globalization;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;

namespace Clicky.Core.Connectors;

/// <summary>
/// Where connector secrets live in the <see cref="ISecretStore"/> (Credential Manager on Windows,
/// as "Clicky/mcp/&lt;connector&gt;/&lt;account&gt;/…"). Values longer than one credential can hold
/// (OAuth tokens often are) are split across numbered entries.
/// </summary>
public sealed class ConnectorSecrets(ISecretStore store)
{
    /// <summary>Characters per credential entry. Credential Manager blobs hold 2,560 bytes of UTF-16.</summary>
    internal const int ChunkLength = 1200;

    private const int MaxChunks = 32;
    private const char CountSeparator = '|';

    public ISecretStore Store { get; } = store;

    public static string EnvironmentName(string connectorId, string accountId, string variable) => $"mcp/{connectorId}/{accountId}/env/{variable}";

    public static string HeaderName(string connectorId, string accountId, string header) => $"mcp/{connectorId}/{accountId}/header/{header}";

    public static string OAuthName(string connectorId, string accountId) => $"mcp/{connectorId}/{accountId}/oauth";

    /// <summary>The client secret of a pre-registered OAuth client; shared by all accounts.</summary>
    public static string ClientSecretName(string connectorId) => $"mcp/{connectorId}/client-secret";

    public string? Read(string name) => ReadChunked(Store, name);

    public void Write(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            DeleteChunked(Store, name);
        }
        else
        {
            WriteChunked(Store, name, value);
        }
    }

    public bool Has(string name) => !string.IsNullOrEmpty(Read(name));

    /// <summary>Every per-account secret of <paramref name="config"/> is set (OAuth tokens are checked at connect time).</summary>
    public IReadOnlyList<string> MissingFor(ConnectorConfig config, string accountId) =>
    [
        .. config.SecretEnvironment.Where(v => !Has(EnvironmentName(config.Id, accountId, v))),
        .. config.SecretHeaders.Where(h => !Has(HeaderName(config.Id, accountId, h))),
    ];

    /// <summary>Removes everything stored for one account.</summary>
    public void DeleteAccount(ConnectorConfig config, string accountId)
    {
        foreach (var variable in config.SecretEnvironment)
        {
            DeleteChunked(Store, EnvironmentName(config.Id, accountId, variable));
        }

        foreach (var header in config.SecretHeaders)
        {
            DeleteChunked(Store, HeaderName(config.Id, accountId, header));
        }

        DeleteChunked(Store, OAuthName(config.Id, accountId));
    }

    /// <summary>Removes everything stored for a connector and all its accounts.</summary>
    public void DeleteConnector(ConnectorConfig config)
    {
        foreach (var account in config.Accounts)
        {
            DeleteAccount(config, account.Id);
        }

        DeleteChunked(Store, ClientSecretName(config.Id));
    }

    // Layout: "<name>" holds "<count>|<first chunk>", "<name>#2".."<name>#<count>" hold the rest.
    // The count prefix makes a stale tail from a longer earlier value harmless.
    internal static void WriteChunked(ISecretStore store, string name, string value)
    {
        var count = Math.Max(1, (value.Length + ChunkLength - 1) / ChunkLength);
        if (count > MaxChunks)
        {
            throw new ArgumentException($"Secret {name} is too long to store ({value.Length} characters).", nameof(value));
        }

        var previous = ChunkCount(store.Read(name));

        // Tail first, head last, so a reader never sees the new count before all its chunks exist.
        for (var i = count - 1; i >= 1; i--)
        {
            store.Write($"{name}#{i + 1}", value.Substring(i * ChunkLength, Math.Min(ChunkLength, value.Length - (i * ChunkLength))));
        }

        store.Write(name, count.ToString(CultureInfo.InvariantCulture) + CountSeparator + value[..Math.Min(ChunkLength, value.Length)]);

        for (var i = count + 1; i <= previous; i++)
        {
            store.Delete($"{name}#{i}");
        }
    }

    internal static string? ReadChunked(ISecretStore store, string name)
    {
        var head = store.Read(name);
        if (head is null)
        {
            return null;
        }

        var count = ChunkCount(head);
        if (count == 0)
        {
            // Written by hand or by something else: treat the whole value as the secret.
            return head;
        }

        var separator = head.IndexOf(CountSeparator, StringComparison.Ordinal);
        var parts = new string[count];
        parts[0] = head[(separator + 1)..];
        for (var i = 1; i < count; i++)
        {
            var part = store.Read($"{name}#{i + 1}");
            if (part is null)
            {
                return null;
            }

            parts[i] = part;
        }

        return string.Concat(parts);
    }

    internal static void DeleteChunked(ISecretStore store, string name)
    {
        var count = ChunkCount(store.Read(name));
        store.Delete(name);
        for (var i = 2; i <= Math.Max(count, 1); i++)
        {
            store.Delete($"{name}#{i}");
        }
    }

    private static int ChunkCount(string? head)
    {
        if (head is null)
        {
            return 0;
        }

        var separator = head.IndexOf(CountSeparator, StringComparison.Ordinal);
        return separator is > 0 and <= 2
            && int.TryParse(head.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count is >= 1 and <= MaxChunks
                ? count
                : 0;
    }
}
