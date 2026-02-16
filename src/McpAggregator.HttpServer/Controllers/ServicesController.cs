using McpAggregator.Core.Services;
using McpAggregator.Core.Tools;
using Microsoft.AspNetCore.Mvc;

namespace McpAggregator.HttpServer.Controllers;

[ApiController]
[Route("api/services")]
public class ServicesController : ControllerBase
{
    private readonly ServerRegistry _registry;
    private readonly ToolIndex _toolIndex;
    private readonly SkillStore _skillStore;
    private readonly ToolProxyHandler _proxy;

    public ServicesController(
        ServerRegistry registry,
        ToolIndex toolIndex,
        SkillStore skillStore,
        ToolProxyHandler proxy)
    {
        _registry = registry;
        _toolIndex = toolIndex;
        _skillStore = skillStore;
        _proxy = proxy;
    }

    [HttpGet]
    public async Task<IActionResult> ListServices(CancellationToken ct)
    {
        await _registry.EnsureLoadedAsync(ct);
        var index = await _toolIndex.GetIndexAsync(ct);
        return Ok(index);
    }

    [HttpGet("{name}")]
    public async Task<IActionResult> GetServiceDetails(string name, CancellationToken ct)
    {
        var details = await _toolIndex.GetDetailsAsync(name, ct);
        return Ok(details);
    }

    [HttpGet("{name}/skill")]
    public async Task<IActionResult> GetServiceSkill(string name, CancellationToken ct)
    {
        _registry.Get(name); // Validate exists
        var skill = await _skillStore.GetAsync(name, ct);
        if (skill is null)
            return NotFound(new { error = $"No skill document for '{name}'." });
        return Content(skill, "text/markdown");
    }

    [HttpPost("{name}/tools/{tool}/invoke")]
    public async Task<IActionResult> InvokeTool(
        string name,
        string tool,
        [FromBody] InvokeRequest? request,
        CancellationToken ct)
    {
        var result = await _proxy.InvokeAsync(name, tool, request?.Arguments, ct);
        return Ok(new { result });
    }
}

public record InvokeRequest(string? Arguments);
