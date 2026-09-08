using McpAggregator.Core.Tools;
using Microsoft.Extensions.AI;

namespace McpAggregator.Measure.Measurement;

/// <summary>
/// A fake model that always does the right thing for the current task: calls the typed wrapper
/// when it is offered, calls find_tools first when it is not (Lazy), and falls back to invoke_tool
/// with a stringified JSON object when nothing else is available. Exists to validate the harness
/// plumbing (every condition should score 100%), not to say anything about models.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private int _callCounter;

    /// <summary>Set by the driver before each run.</summary>
    public MeasureTask? CurrentTask { get; set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var task = CurrentTask ?? throw new InvalidOperationException("CurrentTask is not set.");
        var history = messages.ToList();
        var toolNames = (options?.Tools ?? []).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var wrapper = WrapperNaming.For(task.Server, task.Tool);

        // Already got a non-error result for the target? Then we are done.
        var alreadyDone = history
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Any(r => r.Result is string s && !s.StartsWith("ERROR:", StringComparison.Ordinal)
                      && history.SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                          .Any(c => c.CallId == r.CallId && (c.Name == wrapper || c.Name == "invoke_tool")));
        if (alreadyDone)
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

        FunctionCallContent call;
        var id = $"call_{Interlocked.Increment(ref _callCounter)}";

        if (toolNames.Contains(wrapper))
        {
            call = new FunctionCallContent(id, wrapper, new Dictionary<string, object?>(task.ScriptedArgs));
        }
        else if (toolNames.Contains("find_tools")
                 && !history.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Any(c => c.Name == "find_tools"))
        {
            call = new FunctionCallContent(id, "find_tools", new Dictionary<string, object?> { ["query"] = task.Tool });
        }
        else
        {
            call = new FunctionCallContent(id, "invoke_tool", new Dictionary<string, object?>
            {
                ["serverName"] = task.Server,
                ["toolName"] = task.Tool,
                ["arguments"] = System.Text.Json.JsonSerializer.Serialize(task.ScriptedArgs),
            });
        }

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
        {
            Usage = new UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 },
        };
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
