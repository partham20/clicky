using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Clicky.Core.Assistant;
using Clicky.Core.Assistant.ClaudeCode;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Suggestions;

/// <summary>What the suggestions research and editor need from Claude; an interface so they can be tested without a backend.</summary>
public interface ISuggestionModel
{
    /// <summary>One plain request on the active backend's fast or deep model.</summary>
    /// <exception cref="ClaudeBackendException">Not set up, or the request failed.</exception>
    Task<string> CompleteAsync(string system, string user, ModelTier tier, CancellationToken cancellationToken);

    /// <summary>
    /// Searches the web for <paramref name="query"/> and returns what it found, with URLs.
    /// The Messages API's web search tool, or Claude Code's WebSearch on the subscription.
    /// </summary>
    /// <exception cref="ClaudeBackendException">Not set up, web search not allowed for this account, or it failed.</exception>
    Task<string> SearchWebAsync(string query, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISuggestionModel"/> on whichever backend settings.claude.backend selects, with the
/// models the router uses for it.
/// </summary>
public sealed class ClaudeSuggestionModel(
    ClaudeAssistant assistant,
    ClaudeCodeBackend claudeCode,
    ISecretStore secrets,
    ISettingsStore settings,
    ILogger<ClaudeSuggestionModel> logger) : ISuggestionModel
{
    /// <summary>Searches per query on the API; Claude Code decides for itself.</summary>
    public const int MaxSearchesPerQuery = 3;

    internal const string SearchSystemPrompt =
        """
        You search the web for a morning briefing. Search for what the user asks, then report what you found:
        one line per relevant result, newest first, each with its date if known and its URL.
        Only things published in the last few days unless the user asks otherwise. No commentary, no advice.
        If nothing relevant turns up, say so in one line.
        """;

    public Task<string> CompleteAsync(string system, string user, ModelTier tier, CancellationToken cancellationToken)
    {
        var claude = settings.Current.Claude;
        return assistant.Backend(claude.Backend).CompleteAsync(system, user, claude.ModelFor(tier), cancellationToken);
    }

    public async Task<string> SearchWebAsync(string query, CancellationToken cancellationToken)
    {
        var claude = settings.Current.Claude;
        var model = claude.ModelFor(ModelTier.Fast);
        if (claude.Backend == ClaudeBackendKind.Subscription)
        {
            return await claudeCode.CompleteAsync(SearchSystemPrompt, query, model, "WebSearch", cancellationToken).ConfigureAwait(false);
        }

        var key = AnthropicKey.Read(secrets) ?? throw new ClaudeBackendException(ClaudeFailure.NotConfigured, "No Anthropic API key saved.");
        return await SearchWithApiAsync(key, model, query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SearchWithApiAsync(string key, string model, string query, CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 4_000,
            System = new List<BetaTextBlockParam> { new() { Text = SearchSystemPrompt } },
            Tools = [new BetaWebSearchTool20250305 { MaxUses = MaxSearchesPerQuery }],
            Messages = [new BetaMessageParam { Role = Role.User, Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = query } } }],
        };

        // Once a day, so a client of its own rather than sharing the talk backend's.
        using var client = new AnthropicClient { ApiKey = key, MaxRetries = 2, Timeout = TimeSpan.FromSeconds(120) };
        try
        {
            var message = await client.Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
            var text = new StringBuilder();
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var part))
                {
                    text.Append(part.Text);
                }
            }

            return text.ToString().Trim();
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException)
        {
            logger.LogInformation(ex, "Web search for suggestions failed");
            var failure = ex is AnthropicIOException or HttpRequestException ? ClaudeFailure.Network : ClaudeFailure.Failed;
            throw new ClaudeBackendException(failure, $"Web search failed: {ex.Message.Split('\n', 2)[0]}", ex);
        }
    }
}
