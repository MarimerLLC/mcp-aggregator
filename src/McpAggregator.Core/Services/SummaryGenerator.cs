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
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var toolList = string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}"));

            var userContent = $"""
                Server name: {server.Name}
                Display name: {server.DisplayName ?? "(none)"}
                Description: {server.Description ?? "(none)"}

                Tools:
                {toolList}
                """;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.Timeout);

            var response = await _chatClient!.GetResponseAsync(
                [
                    new(ChatRole.System, """
                        You are a technical indexer. Given an MCP server's metadata and tool catalog,
                        produce a concise summary (2-4 sentences, max 300 characters) that describes
                        what this server does and what capabilities it offers. Focus on being
                        differentiated and useful for an LLM deciding whether to route a request
                        to this server. Output ONLY the summary text, no quotes or labels.
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
