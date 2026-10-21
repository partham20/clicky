using System.Text.RegularExpressions;

namespace Clicky.Core.Diagnostics;

/// <summary>
/// Scrubs text that leaves the PC in a bug report: API keys and tokens (Clicky keeps them in
/// Credential Manager, but a pasted key can still end up in a log line), and the Windows user
/// name inside paths.
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "[redacted]";

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = AnthropicKey().Replace(text, Placeholder);
        text = OpenAiKey().Replace(text, Placeholder);
        text = GitHubToken().Replace(text, Placeholder);
        text = BearerToken().Replace(text, "${scheme} " + Placeholder);
        text = NamedSecret().Replace(text, "${name}" + Placeholder);
        return UserProfilePath().Replace(text, "${prefix}<user>");
    }

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{16,}")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"(?<scheme>\b[Bb]earer)\s+[A-Za-z0-9._~+/=\-]{12,}")]
    private static partial Regex BearerToken();

    // "apiKey": "abc…", api_key=abc…, password: abc… (JSON or key=value in a log line).
    [GeneratedRegex(@"(?<name>""?\b(?:api[_\-]?key|access[_\-]?token|refresh[_\-]?token|client[_\-]?secret|password|secret)""?\s*[:=]\s*""?)[^""\s,;}]{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecret();

    // C:\Users\jane doe\… and the 8.3 form C:\Users\JANE~1.DOE\…, either slash. The name must end
    // at a separator, quote or line end, so prose that merely mentions C:\Users is left alone.
    [GeneratedRegex(@"(?<prefix>\b[A-Za-z]:[\\/]+Users[\\/]+)[^\\/:*?""<>|\r\n]{1,64}(?=[\\/""'\r]|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex UserProfilePath();
}
