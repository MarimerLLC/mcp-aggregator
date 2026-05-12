using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

public class SummaryGenerator
{
    private readonly IChatClient? _chatClient;
    private readonly AiOptions _options;
    private readonly ILogger<SummaryGenerator> _logger;

    public SummaryGenerator(
        IServiceProvider serviceProvider,
        IOptions<AiOptions> options,
        ILogger<SummaryGenerator> logger)
    {
        _chatClient = serviceProvider.GetService<IChatClient>();
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable => _chatClient is not null && _options.Enabled;

    public async Task<string?> GenerateSummaryAsync(
        RegisteredServer server,
        IReadOnlyList<ToolSummary> tools,
        IReadOnlyList<PromptDetail>? prompts = null,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var toolList = tools.Count > 0
                ? string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}"))
                : "(none)";

            var promptSection = "";
            if (prompts is { Count: > 0 })
            {
                var promptList = string.Join("\n", prompts.Select(p =>
                {
                    var args = p.Arguments.Count > 0
                        ? $" [args: {string.Join(", ", p.Arguments.Select(a => a.Required ? a.Name : $"{a.Name}?"))}]"
                        : "";
                    return $"- {p.Name}{args}: {p.Description}";
                }));
                promptSection = $"""


                    Prompt templates:
                    {promptList}
                    """;
            }

            var serverIdentity = string.IsNullOrWhiteSpace(server.RemoteTitle)
                ? server.RemoteName
                : $"{server.RemoteTitle} ({server.RemoteName})";

            var instructionsSection = string.IsNullOrWhiteSpace(server.RemoteInstructions)
                ? ""
                : $"""


                    Server-supplied instructions (authoritative — these come from the server itself
                    and describe how it expects to be used; weight this heavily over names alone):
                    {server.RemoteInstructions}
                    """;

            var userContent = $"""
                Server name: {server.Name}
                Display name: {server.DisplayName ?? "(none)"}
                Registered description: {server.Description ?? "(none)"}
                Remote identity: {serverIdentity ?? "(none)"}
                Remote version: {server.RemoteVersion ?? "(none)"}

                Tools:
                {toolList}{promptSection}{instructionsSection}
                """;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.Timeout);

            var response = await _chatClient!.GetResponseAsync(
                [
                    new(ChatRole.System, """
                        You are a technical indexer building a capability index for an AI agent orchestrator.
                        Given an MCP server's metadata, tool catalog, prompt templates, and any
                        server-supplied instructions, produce a concise summary (2-4 sentences,
                        max 300 characters) that describes what this server does and what capabilities
                        it offers. The summary will be read by another AI agent deciding whether to
                        route a request to this server, so use precise technical language and emphasize
                        what kinds of tasks or domains this server handles.

                        When server-supplied instructions are present, treat them as the authoritative
                        description of the server's purpose and recommended use — they override
                        guesses inferred from tool names. If prompt templates are present, mention them
                        as first-class capabilities alongside tools.

                        Output ONLY the summary text, no quotes or labels.
                        """),
                    new(ChatRole.User, userContent)
                ],
                cancellationToken: cts.Token);

            var summary = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(summary))
                return null;

            _logger.LogInformation("Generated AI summary for '{Server}': {Summary}", server.Name, summary);
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate AI summary for '{Server}'", server.Name);
            return null;
        }
    }
}
