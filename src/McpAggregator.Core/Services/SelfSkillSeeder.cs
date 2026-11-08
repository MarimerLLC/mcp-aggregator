using McpAggregator.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

/// <summary>What <see cref="SelfSkillSeeder.SeedAsync"/> did with the aggregator's own document.</summary>
public enum SelfSkillSeedOutcome
{
    /// <summary>This build ships no document; nothing was touched.</summary>
    NotShipped,

    /// <summary>There was no document; the shipped one was written.</summary>
    Created,

    /// <summary>The document already is the shipped one.</summary>
    Current,

    /// <summary>The document was an untouched earlier shipped version and was replaced (it went to history).</summary>
    Upgraded,

    /// <summary>
    /// The document differs from the shipped one and is not known to be an earlier shipped version
    /// (a deliberate edit, or a deployment from before the marker existed); it was left alone.
    /// </summary>
    Kept,
}

/// <summary>
/// Keeps the live copy of the aggregator's own skill document current across releases (issue #72).
/// Runs once at startup. The deployment's skills directory is persistent and no deploy step copies
/// the document, so without this the live copy stays whatever was first put there. The rule, using
/// the marker <see cref="SkillStore"/> keeps on every save: replace the live document only when it is
/// missing or is exactly an earlier shipped version; never overwrite an edit.
/// </summary>
public sealed class SelfSkillSeeder(
    SkillStore skillStore,
    IOptions<AggregatorOptions> options,
    ILogger<SelfSkillSeeder> logger) : IHostedService
{
    public async Task<SelfSkillSeedOutcome> SeedAsync(CancellationToken ct = default)
    {
        var selfName = options.Value.SelfName;
        if (skillStore.ShippedSelfDocument is not { } shipped)
            return SelfSkillSeedOutcome.NotShipped;

        var shippedHash = ShippedSelfSkill.Hash(shipped);
        var live = await skillStore.GetAsync(selfName, ct);

        if (live is null)
        {
            await skillStore.SetAsync(selfName, shipped, ct);
            logger.LogInformation("Wrote the shipped skill document for '{Self}'", selfName);
            return SelfSkillSeedOutcome.Created;
        }

        var liveHash = ShippedSelfSkill.Hash(live);
        if (liveHash == shippedHash)
        {
            // Saving the same text records the marker (and keeps no history entry).
            if (await skillStore.GetShippedMarkerAsync(ct) != shippedHash)
                await skillStore.SetAsync(selfName, live, ct);
            return SelfSkillSeedOutcome.Current;
        }

        if (await skillStore.GetShippedMarkerAsync(ct) == liveHash)
        {
            await skillStore.SetAsync(selfName, shipped, ct);
            logger.LogInformation(
                "Replaced the skill document for '{Self}' with the version this build ships; the previous one is in its skill history",
                selfName);
            return SelfSkillSeedOutcome.Upgraded;
        }

        logger.LogWarning(
            "The skill document for '{Self}' differs from the version this build ships and was edited or saved before " +
            "release tracking, so it was left as is. To adopt the shipped version, save it with " +
            "update_skill(serverName: \"{Self}\", ...); later releases then keep it current",
            selfName, selfName);
        return SelfSkillSeedOutcome.Kept;
    }

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SeedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never stop the server over its own documentation.
            logger.LogWarning(ex, "Could not check the shipped skill document for '{Self}'", options.Value.SelfName);
        }
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
