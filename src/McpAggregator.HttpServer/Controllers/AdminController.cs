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

    public AdminController(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SkillStore skillStore)
    {
        _registry = registry;
        _connectionManager = connectionManager;
        _skillStore = skillStore;
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
        return Created($"/api/services/{server.Name}", new { message = $"Server '{server.Name}' registered." });
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
