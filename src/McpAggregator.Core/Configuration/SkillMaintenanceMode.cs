namespace McpAggregator.Core.Configuration;

/// <summary>How the aggregator keeps skill documents current (issue #58; <c>docs/skill-maintenance.md</c>).</summary>
public enum SkillMaintenanceMode
{
    /// <summary>Nothing: no background checks and no stale notice in <c>get_service_skill</c>.</summary>
    Off,

    /// <summary>The stale notice in <c>get_service_skill</c> and the stale-skill metric; no revisions.</summary>
    Notify,

    /// <summary>
    /// Stale skills get a draft (<c>list_skill_drafts</c>, <c>apply_skill_draft</c>,
    /// <c>discard_skill_draft</c>); version-only changes are re-baselined.
    /// </summary>
    Propose,

    /// <summary>
    /// Like <see cref="Propose"/>, but a draft that passes validation is published straight away. A
    /// failing draft stays waiting for review.
    /// </summary>
    Auto,
}
