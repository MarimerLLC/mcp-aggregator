using System.ComponentModel;
using System.Text.Json;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tools;

/// <summary>
/// Tools that change what the aggregator is, rather than use it. Deliberately <b>not</b> marked
/// <c>[McpServerToolType]</c>: <see cref="AdminToolSet"/> builds them so the SDK never adds them to
/// the shared tool collection on its own. In <see cref="Configuration.WrapperToolMode.Lazy"/> mode
/// they stay out of every session's initial <c>tools/list</c> and are disclosed per session by
/// <c>show_admin_tools</c> (or by calling one by name); in Eager mode they are listed as usual.
/// </summary>
public class AdminTools
{
    /// <summary>
    /// The names of every tool declared on this class plus the enable/disable pair. This is the
    /// set <see cref="Services.WrapperToolCatalog"/> hides in Lazy mode; a test keeps it equal to
    /// the <see cref="McpServerToolAttribute"/> names actually declared here.
    /// </summary>
    public static readonly IReadOnlySet<string> ToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "register_server", "update_server", "unregister_server", "update_skill", "regenerate_summary",
        "enable_service", "disable_service", "refresh_skill", "list_skill_history", "restore_skill",
        "list_skill_drafts", "apply_skill_draft", "discard_skill_draft", "set_skill_sources",
    };

    /// <summary>Name and description of every tool declared here, in declaration order.</summary>
    public static IReadOnlyList<(string Name, string? Description)> Describe()
        => typeof(AdminTools)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => (
                Name: m.GetCustomAttributes(typeof(McpServerToolAttribute), false).OfType<McpServerToolAttribute>().FirstOrDefault()?.Name,
                Description: m.GetCustomAttributes(typeof(DescriptionAttribute), false).OfType<DescriptionAttribute>().FirstOrDefault()?.Description))
            .Where(t => t.Name is not null)
            .Select(t => (t.Name!, t.Description))
            .ToList();

    [McpServerTool(Name = "enable_service")]
    [Description("Enable a registered MCP server, allowing its tools to be invoked.")]
    public static async Task<string> EnableService(
        ServerRegistry registry,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        await registry.SetEnabledAsync(serverName, true, ct);
        return $"Server '{serverName}' enabled.";
    }

    [McpServerTool(Name = "disable_service")]
    [Description("Disable a registered MCP server, preventing its tools from being invoked.")]
    public static async Task<string> DisableService(
        ServerRegistry registry,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        await registry.SetEnabledAsync(serverName, false, ct);
        return $"Server '{serverName}' disabled.";
    }

    [McpServerTool(Name = "register_server")]
    [Description("Register a new downstream MCP server with the aggregator.")]
    public static async Task<string> RegisterServer(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SummaryGenerator summaryGenerator,
        [Description("Unique name for the server")] string name,
        [Description("Transport type: 'Stdio' or 'Http'")] string transportType,
        [Description("For Stdio: the command to run. For Http: the server URL.")] string endpoint,
        [Description("Optional display name")] string? displayName = null,
        [Description("Optional description")] string? description = null,
        [Description("For Stdio: JSON array of command arguments")] string? arguments = null,
        [Description("For Stdio: JSON object of environment variables")] string? environment = null,
        [Description("For Http: JSON object of HTTP headers. Values may reference environment variables as ${VAR}, resolved at connect time.")] string? headers = null,
        [Description("For Http: connection timeout in seconds")] int? connectionTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        var transport = new TransportConfig();

        if (Enum.TryParse<TransportType>(transportType, ignoreCase: true, out var tt))
            transport.Type = tt;
        else
            return $"Invalid transport type '{transportType}'. Use 'Stdio' or 'Http'.";

        switch (transport.Type)
        {
            case TransportType.Stdio:
                transport.Command = endpoint;
                transport.Arguments = arguments is not null
                    ? JsonSerializer.Deserialize<string[]>(arguments)
                    : null;
                transport.Environment = environment is not null
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(environment)
                    : null;
                break;
            case TransportType.Http:
                transport.Url = endpoint;
                transport.Headers = headers is not null
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(headers)
                    : null;
                transport.ConnectionTimeout = connectionTimeoutSeconds is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
                break;
        }

        var server = new RegisteredServer
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Transport = transport
        };

        await registry.EnsureLoadedAsync(ct);
        await registry.RegisterAsync(server, ct);

        // Generate AI summary if available
        var summary = await GenerateSummaryForServerAsync(
            server, registry, connectionManager, summaryGenerator, ct);

        var result = $"Server '{name}' registered successfully.";
        if (summary is not null)
            result += $" AI summary: {summary}";

        return result;
    }

    [McpServerTool(Name = "update_server")]
    [Description("Update a registered server's transport configuration or metadata, preserving its skill document and AI summary.")]
    public static async Task<string> UpdateServer(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        ToolIndex toolIndex,
        [Description("The name of the registered server")] string serverName,
        [Description("Transport type: 'Stdio' or 'Http'. Required together with endpoint to replace the transport.")] string? transportType = null,
        [Description("For Stdio: the command to run. For Http: the server URL. Required together with transportType.")] string? endpoint = null,
        [Description("New display name")] string? displayName = null,
        [Description("New description")] string? description = null,
        [Description("For Stdio: JSON array of command arguments")] string? arguments = null,
        [Description("For Stdio: JSON object of environment variables")] string? environment = null,
        [Description("For Http: JSON object of HTTP headers. Values may reference environment variables as ${VAR}, resolved at connect time.")] string? headers = null,
        [Description("For Http: connection timeout in seconds")] int? connectionTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        await registry.EnsureLoadedAsync(ct);

        TransportConfig? transport = null;

        if (transportType is not null || endpoint is not null)
        {
            if (transportType is null || endpoint is null)
                return "Both 'transportType' and 'endpoint' must be supplied to replace the transport configuration.";

            if (!Enum.TryParse<TransportType>(transportType, ignoreCase: true, out var tt))
                return $"Invalid transport type '{transportType}'. Use 'Stdio' or 'Http'.";

            transport = new TransportConfig { Type = tt };

            switch (tt)
            {
                case TransportType.Stdio:
                    transport.Command = endpoint;
                    transport.Arguments = arguments is not null
                        ? JsonSerializer.Deserialize<string[]>(arguments)
                        : null;
                    transport.Environment = environment is not null
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(environment)
                        : null;
                    break;
                case TransportType.Http:
                    transport.Url = endpoint;
                    transport.Headers = headers is not null
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(headers)
                        : null;
                    transport.ConnectionTimeout = connectionTimeoutSeconds is { } seconds
                        ? TimeSpan.FromSeconds(seconds)
                        : null;
                    break;
            }
        }

        await registry.UpdateServerAsync(serverName, transport, displayName, description, ct);

        // Drop any live connection so the next call reconnects with the new configuration, and the
        // cached schemas so the typed wrapper tools are rebuilt from whatever the new transport serves.
        await connectionManager.DisconnectAsync(serverName);
        if (transport is not null)
            toolIndex.InvalidateCache(serverName);

        return $"Server '{serverName}' updated successfully.";
    }

    [McpServerTool(Name = "regenerate_summary")]
    [Description("Re-generate the AI summary for an existing registered server.")]
    public static async Task<string> RegenerateSummary(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SummaryGenerator summaryGenerator,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        var server = registry.Get(serverName);

        if (!summaryGenerator.IsAvailable)
            return "AI summary generation is not configured. Set McpAggregator:AI:Enabled to true and provide an endpoint.";

        var summary = await GenerateSummaryForServerAsync(
            server, registry, connectionManager, summaryGenerator, ct);

        return summary is not null
            ? $"Summary updated for '{serverName}': {summary}"
            : $"Failed to generate summary for '{serverName}'.";
    }

    [McpServerTool(Name = "unregister_server")]
    [Description("Remove a registered MCP server from the aggregator.")]
    public static async Task<string> UnregisterServer(
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SkillStore skillStore,
        [Description("The name of the registered server to remove")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        await connectionManager.DisconnectAsync(serverName);
        skillStore.Delete(serverName);
        await registry.UnregisterAsync(serverName, ct);
        return $"Server '{serverName}' unregistered successfully.";
    }

    [McpServerTool(Name = "update_skill")]
    [Description("Set or update the skill document (markdown) for a registered server.")]
    public static async Task<string> UpdateSkill(
        ServerRegistry registry,
        SkillStore skillStore,
        ToolIndex toolIndex,
        [Description("The name of the registered server")] string serverName,
        [Description("Markdown content for the skill document")] string markdown,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        registry.Get(serverName); // Validate server exists
        // A document fetched with get_service_skill may start with the stale notice; never store it.
        await skillStore.SetAsync(serverName, SkillNotice.Strip(markdown), ct);
        await registry.UpdateSkillFlagAsync(serverName, true, ct);
        var baselineRecorded = await SkillSnapshot.CaptureAsync(registry, toolIndex, serverName, ct);
        return baselineRecorded
            ? $"Skill document updated for '{serverName}'."
            : $"Skill document updated for '{serverName}'. {SkillSnapshot.NoBaselineNote}";
    }

    [McpServerTool(Name = "refresh_skill")]
    [Description("Revise a stale skill document with the aggregator's model: it proposes a minimal edit for what changed on the server (see skillDrift in get_service_details), checks it, and returns the draft, a diff and any validation problems. Nothing is saved unless apply is true. With apply: true the reviewed draft from the previous call is published as is (pass regenerate: true for a new one); without a stored draft, a new one is generated and published if it passes validation. The replaced version goes to history (list_skill_history, restore_skill). When only the server's version changed, apply: true records the document against the new version without calling the model.")]
    public static async Task<string> RefreshSkill(
        SkillMaintainer maintainer,
        [Description("The name of the registered server")] string serverName,
        [Description("Publish the draft. Default false: propose only.")] bool apply = false,
        [Description("Ignore a stored draft and ask the model for a new one.")] bool regenerate = false,
        CancellationToken ct = default)
    {
        var result = await maintainer.RefreshAsync(serverName, apply, regenerate, ct);
        return JsonSerializer.Serialize(result, SkillMaintainer.ResultJson);
    }

    [McpServerTool(Name = "set_skill_sources")]
    [Description("Name a server's change sources: release notes and documentation the skill-revision model reads, so it can see behavior changes a schema diff can't show. Pass a value to set it, an empty string to clear it, or omit it to keep it. Returns what reading the sources produces right now (characters and errors per source). With sources set, a version-only change is also revised by the model rather than just re-recorded.")]
    public static async Task<string> SetSkillSources(
        ServerRegistry registry,
        SkillSourceReader reader,
        [Description("The name of the registered server")] string serverName,
        [Description("Release notes: an https URL on an allowed host, or a GitHub location 'owner/repo[@ref]:path' naming a file or a folder (files named yyyy-MM-dd-... are read from the skill's baseline date on).")] string? changeNotes = null,
        [Description("A tool on this server that returns its own documentation, e.g. 'get_guide'.")] string? guideTool = null,
        [Description("JSON array of argument objects for guideTool, one call each, e.g. [{\"name\":\"calendar\"},{\"name\":\"email\"}]. Omitted or [] = one call with no arguments.")] string? guideArguments = null,
        [Description("JSON array of this server's own resource URIs that hold documentation.")] string? guideResources = null,
        CancellationToken ct = default)
    {
        await registry.EnsureLoadedAsync(ct);
        var server = registry.Get(serverName);
        var sources = server.SkillSources is { } existing
            ? new SkillSources
            {
                ChangeNotes = existing.ChangeNotes,
                GuideTool = existing.GuideTool,
                GuideArguments = existing.GuideArguments,
                GuideResources = existing.GuideResources,
            }
            : new SkillSources();

        if (changeNotes is not null)
        {
            if (changeNotes.Trim().Length > 0 && reader.ValidateChangeNotes(changeNotes.Trim()) is { } invalid)
                return $"Not saved: {invalid}";
            sources.ChangeNotes = changeNotes.Trim().Length == 0 ? null : changeNotes.Trim();
        }
        if (guideTool is not null)
            sources.GuideTool = guideTool.Trim().Length == 0 ? null : guideTool.Trim();
        if (guideArguments is not null)
        {
            try
            {
                sources.GuideArguments = guideArguments.Trim().Length == 0
                    ? null
                    : JsonSerializer.Deserialize<List<JsonElement>>(guideArguments);
            }
            catch (JsonException ex)
            {
                return $"Not saved: guideArguments must be a JSON array of objects ({ex.Message}).";
            }
            if (sources.GuideArguments?.Any(a => a.ValueKind != JsonValueKind.Object) == true)
                return "Not saved: every guideArguments entry must be a JSON object.";
        }
        if (guideResources is not null)
        {
            try
            {
                sources.GuideResources = guideResources.Trim().Length == 0
                    ? null
                    : JsonSerializer.Deserialize<List<string>>(guideResources);
            }
            catch (JsonException ex)
            {
                return $"Not saved: guideResources must be a JSON array of URIs ({ex.Message}).";
            }
        }

        await registry.UpdateSkillSourcesAsync(serverName, sources, ct);
        server = registry.Get(serverName);
        if (server.SkillSources is null)
            return $"Cleared the skill change sources for '{serverName}'.";

        var material = await reader.ReadAsync(server, server.SkillRecordedAt, ct);
        return JsonSerializer.Serialize(new
        {
            message = $"Saved the skill change sources for '{serverName}'. Below is what they produce right now, from the skill's baseline date ({server.SkillRecordedAt:yyyy-MM-dd}) on.",
            sources = server.SkillSources,
            read = material.Reports,
        }, SkillMaintainer.ResultJson);
    }

    [McpServerTool(Name = "list_skill_drafts")]
    [Description("List revised skill-document drafts waiting for review (made by refresh_skill or by the background upkeep in Propose mode), with whether each passed validation and still fits the current document and server. Pass serverName to get that draft's full text and its diff against the current document.")]
    public static async Task<string> ListSkillDrafts(
        SkillMaintainer maintainer,
        [Description("Optional: one server, to include the draft text and diff")] string? serverName = null,
        CancellationToken ct = default)
    {
        var drafts = await maintainer.ListDraftsAsync(serverName, ct);
        return JsonSerializer.Serialize(drafts, SkillMaintainer.ResultJson);
    }

    [McpServerTool(Name = "apply_skill_draft")]
    [Description("Publish the waiting skill-document draft for a server exactly as stored (see list_skill_drafts). Never calls the model. Refused when the draft failed validation or was made for an earlier version of the document or server. The replaced version goes to history.")]
    public static async Task<string> ApplySkillDraft(
        SkillMaintainer maintainer,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct = default)
    {
        var result = await maintainer.ApplyDraftAsync(serverName, ct);
        return JsonSerializer.Serialize(result, SkillMaintainer.ResultJson);
    }

    [McpServerTool(Name = "discard_skill_draft")]
    [Description("Discard the waiting skill-document draft for a server. The background upkeep won't draft again until the document or the server changes; refresh_skill can still make a new one on request.")]
    public static async Task<string> DiscardSkillDraft(
        SkillMaintainer maintainer,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct = default)
        => await maintainer.DiscardDraftAsync(serverName, ct) switch
        {
            DraftDiscardOutcome.Discarded => $"Discarded the skill-document draft for '{serverName}'.",
            DraftDiscardOutcome.Replaced => $"A newer draft for '{serverName}' was saved while discarding, so nothing was discarded. Review it with list_skill_drafts.",
            _ => $"No draft was waiting for '{serverName}'.",
        };

    [McpServerTool(Name = "list_skill_history")]
    [Description("List the replaced versions of a server's skill document, newest first, with the server version each was written for. Pass a version to restore_skill to bring it back.")]
    public static async Task<string> ListSkillHistory(
        ServerRegistry registry,
        SkillStore skillStore,
        [Description("The name of the registered server")] string serverName,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        registry.Get(serverName); // Validate server exists
        var history = await skillStore.ListHistoryAsync(serverName, ct);
        return JsonSerializer.Serialize(history, SkillMaintainer.ResultJson);
    }

    [McpServerTool(Name = "restore_skill")]
    [Description("Restore an earlier version of a server's skill document (a version from list_skill_history). The current document goes to history first. The restored version is judged against the catalog it was written for, so it may read stale.")]
    public static async Task<string> RestoreSkill(
        ServerRegistry registry,
        SkillStore skillStore,
        ToolIndex toolIndex,
        [Description("The name of the registered server")] string serverName,
        [Description("The version to restore, as list_skill_history shows it")] string version,
        CancellationToken ct)
    {
        await registry.EnsureLoadedAsync(ct);
        registry.Get(serverName); // Validate server exists
        var entry = await skillStore.GetHistoryAsync(serverName, version, ct);
        if (entry is null)
            return $"No version '{version}' in the skill history for '{serverName}'. Call list_skill_history for the versions.";

        var (markdown, baseline) = entry.Value;
        await skillStore.SetAsync(serverName, markdown, ct);
        await registry.UpdateSkillFlagAsync(serverName, true, ct);
        await SkillSnapshot.ApplyBaselineAsync(registry, toolIndex, serverName, baseline, ct);
        skillStore.DeleteDraft(serverName);

        return baseline is null
            ? $"Restored version '{version}' of the skill document for '{serverName}'. It was kept without a baseline, so its freshness is unknown until the document is saved again with update_skill."
            : $"Restored version '{version}' of the skill document for '{serverName}', written for {baseline.RemoteVersion ?? "an unknown version"}.";
    }

    private static async Task<string?> GenerateSummaryForServerAsync(
        RegisteredServer server,
        ServerRegistry registry,
        ConnectionManager connectionManager,
        SummaryGenerator summaryGenerator,
        CancellationToken ct)
    {
        if (!summaryGenerator.IsAvailable)
            return null;

        try
        {
            var mcpTools = await connectionManager.ExecuteWithRetryAsync<IList<McpClientTool>>(server.Name,
                async (client, token) => await client.ListToolsAsync(cancellationToken: token), ct);

            var toolSummaries = mcpTools.Select(t => new ToolSummary
            {
                Name = t.Name,
                Description = t.Description
            }).ToList();

            List<PromptDetail> promptDetails = [];
            try
            {
                var mcpPrompts = await connectionManager.ExecuteWithRetryAsync<IList<McpClientPrompt>>(server.Name,
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

            var summary = await summaryGenerator.GenerateSummaryAsync(server, toolSummaries, promptDetails, ct);

            if (summary is not null)
            {
                await registry.UpdateSummaryAsync(server.Name, summary, ct);
            }

            return summary;
        }
        catch
        {
            // Summary generation is best-effort; don't fail registration
            return null;
        }
    }
}
