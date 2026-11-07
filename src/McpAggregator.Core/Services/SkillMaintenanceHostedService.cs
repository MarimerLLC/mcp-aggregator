using System.Diagnostics;
using McpAggregator.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

/// <summary>
/// Keeps skill documents current without being asked (issue #58; <c>docs/skill-maintenance.md</c>).
/// On a timer, and for a single server when <see cref="SkillMaintenanceSignal"/> asks, it checks every
/// enabled server that has a skill document and, depending on <see cref="SkillMaintenanceOptions.Mode"/>:
/// <list type="bullet">
/// <item><c>Notify</c>: only counts stale skills (<c>mcp_skill_stale_servers</c>);</item>
/// <item><c>Propose</c>: re-baselines version-only changes and has <see cref="SkillMaintainer"/> draft a
/// revision for other stale skills, which waits in <c>list_skill_drafts</c>;</item>
/// <item><c>Auto</c>: the same, but a draft that passes validation is published.</item>
/// </list>
/// Servers are handled one at a time. A draft is made at most once per server state (a stored draft
/// that still fits is never regenerated, whether it passed validation or not), model calls are capped
/// per UTC day, and a server whose revision fails is backed off exponentially.
/// </summary>
public sealed class SkillMaintenanceHostedService : BackgroundService
{
    private readonly ServerRegistry _registry;
    private readonly ToolIndex _toolIndex;
    private readonly SkillMaintainer _maintainer;
    private readonly SkillMaintenanceOptions _options;
    private readonly SkillMaintenanceSignal _signal;
    private readonly TimeProvider _time;
    private readonly ILogger<SkillMaintenanceHostedService> _logger;

    private readonly Dictionary<string, (int Failures, DateTimeOffset Until)> _backoff = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _quotaDay;
    private int _quotaUsed;
    private bool _quotaWarned;
    private bool _unavailableWarned;

    public SkillMaintenanceHostedService(
        ServerRegistry registry,
        ToolIndex toolIndex,
        SkillMaintainer maintainer,
        IOptions<SkillMaintenanceOptions> options,
        SkillMaintenanceSignal signal,
        ILogger<SkillMaintenanceHostedService> logger,
        TimeProvider? time = null)
    {
        _registry = registry;
        _toolIndex = toolIndex;
        _maintainer = maintainer;
        _options = options.Value;
        _signal = signal;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public SkillMaintenanceMode Mode => _options.Mode;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Mode == SkillMaintenanceMode.Off)
            return;

        _logger.LogInformation("Skill upkeep running in {Mode} mode, every {Interval}", Mode, _options.CheckInterval);
        try
        {
            await Task.Delay(_options.StartupDelay, _time, stoppingToken);
            var nextSweep = _time.GetUtcNow();
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_time.GetUtcNow() >= nextSweep)
                {
                    await RunSafelyAsync(() => SweepAsync(stoppingToken), "sweep", stoppingToken);
                    nextSweep = _time.GetUtcNow() + _options.CheckInterval;
                }

                // Sleep until the next sweep, waking early for a single-server request.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var remaining = nextSweep - _time.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                    wait.CancelAfter(remaining);
                try
                {
                    if (remaining > TimeSpan.Zero && await _signal.Reader.WaitToReadAsync(wait.Token))
                    {
                        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        while (_signal.Reader.TryRead(out var name))
                            requested.Add(name);
                        foreach (var name in requested)
                            await RunSafelyAsync(() => CheckServerAsync(name, stoppingToken), name, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Time for the next sweep.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Runs one unit of upkeep and logs whatever it throws, including a cancellation that isn't the
    /// host stopping (an HTTP timeout to a downstream surfaces as <see cref="OperationCanceledException"/>),
    /// so one bad server never faults the background service. Only shutdown propagates.
    /// </summary>
    internal async Task RunSafelyAsync(Func<Task> work, string what, CancellationToken stoppingToken)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Skill upkeep {What} failed", what);
        }
    }

    /// <summary>Checks every enabled server with a skill document, one at a time, and updates the stale-skill gauge.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        await _registry.EnsureLoadedAsync(ct);
        var stale = 0;
        foreach (var server in _registry.GetAll().Where(s => s.Enabled && s.HasSkillDocument).ToList())
        {
            try
            {
                if (await CheckServerAsync(server.Name, ct))
                    stale++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Skill upkeep could not check '{Server}'", server.Name);
            }
        }
        AggregatorTelemetry.StaleSkills = stale;
    }

    /// <summary>
    /// Checks one server and acts on it as the mode says. Returns whether its skill is still stale
    /// afterwards (stale and not just re-baselined or published).
    /// </summary>
    public async Task<bool> CheckServerAsync(string serverName, CancellationToken ct = default)
    {
        if (Mode == SkillMaintenanceMode.Off)
            return false;

        await _registry.EnsureLoadedAsync(ct);
        if (!_registry.TryGet(serverName, out var server) || server is null || !server.Enabled || !server.HasSkillDocument)
            return false;

        string? freshness;
        Models.SkillDrift? drift;
        try
        {
            (freshness, drift) = await _toolIndex.GetSkillStatusAsync(serverName, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Skill upkeep could not read '{Server}'", serverName);
            return false;
        }

        if (freshness != "stale" || drift is null)
            return false;
        if (Mode == SkillMaintenanceMode.Notify)
            return true;

        var apply = Mode == SkillMaintenanceMode.Auto;

        // Nothing in the catalog to revise from and no change sources to read: accept the document for
        // the new version (no model call). With sources, the release notes may say what changed.
        if (drift.CatalogUnchanged && !SkillMaintainer.HasSources(server))
            return !Succeeded(await RecordAsync(serverName, drift, () => _maintainer.RefreshAsync(serverName, apply: true, regenerate: false, ct)));

        // A draft for exactly this state already exists: never make another. Auto publishes it if it passed.
        var waiting = await _maintainer.GetCurrentDraftAsync(serverName, ct);
        if (waiting is not null)
        {
            if (apply && waiting.ValidationPassed && !waiting.Discarded)
                return !Succeeded(await RecordAsync(serverName, drift, () => _maintainer.ApplyDraftAsync(serverName, ct)));
            return true;
        }

        if (!_maintainer.IsAvailable)
        {
            if (!_unavailableWarned)
                _logger.LogWarning("Skill upkeep is in {Mode} mode but no model is configured (McpAggregator:AI); stale skills are only reported", Mode);
            _unavailableWarned = true;
            return true;
        }

        var now = _time.GetUtcNow();
        if (_backoff.TryGetValue(serverName, out var backoff) && now < backoff.Until)
            return true;

        if (!TryTakeQuota(now))
            return true;

        var result = await RecordAsync(serverName, drift, () => _maintainer.RefreshAsync(serverName, apply, regenerate: false, ct));
        if (result.Status == "failed")
        {
            var failures = (_backoff.TryGetValue(serverName, out var b) ? b.Failures : 0) + 1;
            var delay = TimeSpan.FromTicks(Math.Min(
                _options.MaxFailureBackoff.Ticks,
                _options.FailureBackoff.Ticks * (long)Math.Pow(2, Math.Min(failures - 1, 20))));
            _backoff[serverName] = (failures, now + delay);
            _logger.LogInformation("Skill upkeep backs off '{Server}' for {Delay} after {Failures} failure(s)", serverName, delay, failures);
        }
        else
        {
            _backoff.Remove(serverName);
        }

        return !Succeeded(result);
    }

    private static bool Succeeded(Models.SkillRefreshResult result) => result.Status is "applied" or "rebaselined";

    private async Task<Models.SkillRefreshResult> RecordAsync(
        string serverName, Models.SkillDrift drift, Func<Task<Models.SkillRefreshResult>> action)
    {
        var result = await action();
        var mode = Mode.ToString().ToLowerInvariant();
        AggregatorTelemetry.SkillRevisions.Add(1, new TagList
        {
            { "server_name", serverName },
            { "mode", mode },
            { "outcome", result.Status },
        });
        _logger.LogInformation(
            "Skill upkeep ({Mode}) for '{Server}' {FromVersion} -> {ToVersion}: {Outcome}{Problems}",
            mode, serverName, drift.RecordedVersion, drift.CurrentVersion, result.Status,
            result.Problems.Count > 0 ? " (" + string.Join("; ", result.Problems) + ")" : "");
        return result;
    }

    private bool TryTakeQuota(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (today != _quotaDay)
        {
            _quotaDay = today;
            _quotaUsed = 0;
            _quotaWarned = false;
        }

        if (_quotaUsed >= _options.MaxRevisionsPerDay)
        {
            if (!_quotaWarned)
                _logger.LogWarning("Skill upkeep reached MaxRevisionsPerDay ({Max}); stale skills wait until tomorrow (UTC)", _options.MaxRevisionsPerDay);
            _quotaWarned = true;
            return false;
        }

        _quotaUsed++;
        return true;
    }
}
