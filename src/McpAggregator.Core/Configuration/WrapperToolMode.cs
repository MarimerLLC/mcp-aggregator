namespace McpAggregator.Core.Configuration;

/// <summary>
/// Controls when downstream tools appear in the aggregator's own <c>tools/list</c> as typed
/// wrapper tools (<c>{server}__{tool}</c>). See <c>docs/typed-wrapper-tools.md</c>.
/// </summary>
public enum WrapperToolMode
{
    /// <summary>
    /// Every tool of every enabled downstream server is a wrapper tool from startup. The whole
    /// surface is visible in one <c>tools/list</c>, at the cost of a large tool list — Claude
    /// Desktop caps the total at roughly 44 tools across all servers.
    /// </summary>
    Eager,

    /// <summary>
    /// Only the aggregator's meta-tools are listed until a wrapper is activated by
    /// <c>find_tools</c> or <c>get_service_details</c>. Activation adds the wrappers to the tool
    /// collection and the SDK sends <c>notifications/tools/list_changed</c>. Activation is
    /// process-wide, not per session. The "Search" mode from issue #39 folds into this one:
    /// <c>find_tools</c> is the search, and activation is what makes a result callable.
    /// </summary>
    Lazy,
}
