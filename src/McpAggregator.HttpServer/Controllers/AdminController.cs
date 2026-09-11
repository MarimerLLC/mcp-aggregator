using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Client;

namespace McpAggregator.HttpServer.Controllers;

[ApiController]
[Route("api/admin/services")]
public class AdminController : ControllerBase
{
    private readonly ServerRegistry _registry;
    private readonly ConnectionManager _connectionManager;
    private readonly SkillStore _skillStore;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly ToolIndex _toolIndex;

    public AdminController(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SkillStore skillStore,
        SummaryGenerator summaryGenerator,
        ToolIndex toolIndex)
    {
        _registry = registry;
        _connectionManager = connectionManager;
        _skillStore = skillStore;
        _summaryGenerator = summaryGenerator;
        _toolIndex = toolIndex;
    }

    [HttpPost]
    public async Task<IActionResult> RegisterServer([FromBody] RegisterServerRequest request, CancellationToken ct)
    {
        var server = new RegisteredServer
        {
            Name = request.Name,
            DisplayName = request.DisplayName,
            Description = request.Description,
            Transport = request.Transport
        };

        await _registry.EnsureLoadedAsync(ct);
        await _registry.RegisterAsync(server, ct);

        // Generate AI summary if available
        if (_summaryGenerator.IsAvailable)
        {
            try
            {
                var mcpTools = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientTool>>(server.Name,
                    async (client, token) => await client.ListToolsAsync(cancellationToken: token), ct);

                var toolSummaries = mcpTools.Select(t => new ToolSummary
                {
                    Name = t.Name,
                    Description = t.Description
                }).ToList();

                List<PromptDetail> promptDetails = [];
                try
                {
                    var mcpPrompts = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientPrompt>>(server.Name,
                        async (client, token) => await client.ListPromptsAsync(cancellationToken: token), ct);

                    promptDetails = mcpPrompts.Select(p => new PromptDetail
                    {
                        Name = p.Name,
                        Description = p.Description,
                        Arguments = p.ProtocolPrompt.Arguments?.Select(a => new PromptArgumentDetail
                        {
                            Name = a.Name,
                            Description = a.Description,
                            Required = a.Required ?? false
                        }).ToList() ?? []
                    }).ToList();
                }
                catch
                {
                    // Prompt listing is best-effort; some servers may not support it
                }

                var summary = await _summaryGenerator.GenerateSummaryAsync(server, toolSummaries, promptDetails, ct);

                if (summary is not null)
                {
                    await _registry.UpdateSummaryAsync(server.Name, summary, ct);
                }
            }
            catch
            {
                // Summary generation is best-effort
            }
        }

        return Created($"/api/services/{server.Name}", new { message = $"Server '{server.Name}' registered." });
    }

    [HttpGet("{name}")]
    public async Task<IActionResult> GetServer(string name, CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(name);

        return Ok(new
        {
            server.Id,
            server.Name,
            server.DisplayName,
            server.Description,
            Transport = server.Transport.ToRedacted(),
            server.Enabled,
            server.RegisteredAt,
            server.HasSkillDocument,
            server.AiSummary,
            server.RemoteName,
            server.RemoteTitle,
            server.RemoteVersion,
            server.SkillRecordedVersion,
            server.SkillRecordedFingerprint,
            server.SkillRecordedAt
        });
    }

    [HttpPut("{name}")]
    public async Task<IActionResult> UpdateServer(string name, [FromBody] UpdateServerRequest request, CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        await _registry.UpdateServerAsync(name, request.Transport, request.DisplayName, request.Description, ct);

        // Drop any live connection so the next call reconnects with the new configuration, and the
        // cached schemas so the typed wrapper tools are rebuilt from whatever the new transport serves.
        await _connectionManager.DisconnectAsync(name);
        if (request.Transport is not null)
            _toolIndex.InvalidateCache(name);

        return Ok(new { message = $"Server '{name}' updated." });
    }

    [HttpPost("{name}/regenerate-summary")]
    public async Task<IActionResult> RegenerateSummary(string name, CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(name);

        if (!_summaryGenerator.IsAvailable)
            return BadRequest(new { message = "AI summary generation is not configured." });

        try
        {
            var mcpTools = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientTool>>(server.Name,
                async (client, token) => await client.ListToolsAsync(cancellationToken: token), ct);

            var toolSummaries = mcpTools.Select(t => new ToolSummary
            {
                Name = t.Name,
                Description = t.Description
            }).ToList();

            List<PromptDetail> promptDetails = [];
            try
            {
                var mcpPrompts = await _connectionManager.ExecuteWithRetryAsync<IList<McpClientPrompt>>(server.Name,
                    async (client, token) => await client.ListPromptsAsync(cancellationToken: token), ct);

                promptDetails = mcpPrompts.Select(p => new PromptDetail
                {
                    Name = p.Name,
                    Description = p.Description,
                    Arguments = p.ProtocolPrompt.Arguments?.Select(a => new PromptArgumentDetail
                    {
                        Name = a.Name,
                        Description = a.Description,
                        Required = a.Required ?? false
                    }).ToList() ?? []
                }).ToList();
            }
            catch
            {
                // Prompt listing is best-effort; some servers may not support it
            }

            var summary = await _summaryGenerator.GenerateSummaryAsync(server, toolSummaries, promptDetails, ct);

            if (summary is not null)
            {
                await _registry.UpdateSummaryAsync(server.Name, summary, ct);
                return Ok(new { message = $"Summary updated for '{name}'.", summary });
            }

            return StatusCode(500, new { message = $"Failed to generate summary for '{name}'." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Error generating summary: {ex.Message}" });
        }
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> UnregisterServer(string name, CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        await _connectionManager.DisconnectAsync(name);
        _skillStore.Delete(name);
        await _registry.UnregisterAsync(name, ct);
        return Ok(new { message = $"Server '{name}' unregistered." });
    }

    [HttpPut("{name}/skill")]
    public async Task<IActionResult> UpdateSkill(string name, [FromBody] UpdateSkillRequest request, CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        _registry.Get(name); // Validate exists
        await _skillStore.SetAsync(name, request.Markdown, ct);
        await _registry.UpdateSkillFlagAsync(name, true, ct);
        var baselineRecorded = await SkillSnapshot.CaptureAsync(_registry, _toolIndex, name, ct);
        var message = baselineRecorded
            ? $"Skill document updated for '{name}'."
            : $"Skill document updated for '{name}'. {SkillSnapshot.NoBaselineNote}";
        return Ok(new { message });
    }

    [HttpPost("{name}/enable")]
    public async Task<IActionResult> EnableServer(string name, CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        await _registry.SetEnabledAsync(name, true, ct);
        return Ok(new { message = $"Server '{name}' enabled." });
    }

    [HttpPost("{name}/disable")]
    public async Task<IActionResult> DisableServer(string name, CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        await _registry.SetEnabledAsync(name, false, ct);
        await _connectionManager.DisconnectAsync(name);
        return Ok(new { message = $"Server '{name}' disabled and disconnected." });
    }
}

public record RegisterServerRequest(
    string Name,
    string? DisplayName,
    string? Description,
    TransportConfig Transport);

public record UpdateServerRequest(
    string? DisplayName,
    string? Description,
    TransportConfig? Transport);

public record UpdateSkillRequest(string Markdown);
