using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace McpAggregator.HttpServer.Controllers;

[ApiController]
[Route("api/admin/services")]
public class AdminController : ControllerBase
{
    private readonly ServerRegistry _registry;
    private readonly ConnectionManager _connectionManager;
    private readonly SkillStore _skillStore;
    private readonly SummaryGenerator _summaryGenerator;

    public AdminController(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SkillStore skillStore,
        SummaryGenerator summaryGenerator)
    {
        _registry = registry;
        _connectionManager = connectionManager;
        _skillStore = skillStore;
        _summaryGenerator = summaryGenerator;
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

        await _registry.RegisterAsync(server, ct);

        // Generate AI summary if available
        if (_summaryGenerator.IsAvailable)
        {
            try
            {
                var client = await _connectionManager.GetClientAsync(server.Name, ct);
                var mcpTools = await client.ListToolsAsync(cancellationToken: ct);

                var toolSummaries = mcpTools.Select(t => new ToolSummary
                {
                    Name = t.Name,
                    Description = t.Description
                }).ToList();

                var summary = await _summaryGenerator.GenerateSummaryAsync(server, toolSummaries, ct);

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

    [HttpPost("{name}/regenerate-summary")]
    public async Task<IActionResult> RegenerateSummary(string name, CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        var server = _registry.Get(name);

        if (!_summaryGenerator.IsAvailable)
            return BadRequest(new { message = "AI summary generation is not configured." });

        try
        {
            var client = await _connectionManager.GetClientAsync(server.Name, ct);
            var mcpTools = await client.ListToolsAsync(cancellationToken: ct);

            var toolSummaries = mcpTools.Select(t => new ToolSummary
            {
                Name = t.Name,
                Description = t.Description
            }).ToList();

            var summary = await _summaryGenerator.GenerateSummaryAsync(server, toolSummaries, ct);

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
        await _connectionManager.DisconnectAsync(name);
        _skillStore.Delete(name);
        await _registry.UnregisterAsync(name, ct);
        return Ok(new { message = $"Server '{name}' unregistered." });
    }

    [HttpPut("{name}/skill")]
    public async Task<IActionResult> UpdateSkill(string name, [FromBody] UpdateSkillRequest request, CancellationToken ct)
    {
        _registry.Get(name); // Validate exists
        await _skillStore.SetAsync(name, request.Markdown, ct);
        await _registry.UpdateSkillFlagAsync(name, true, ct);
        return Ok(new { message = $"Skill document updated for '{name}'." });
    }
}

public record RegisterServerRequest(
    string Name,
    string? DisplayName,
    string? Description,
    TransportConfig Transport);

public record UpdateSkillRequest(string Markdown);
