using System.Diagnostics;
using System.Text.Json;
using McpAggregator.Core.Tools;
using McpAggregator.Measure.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace McpAggregator.Measure.Measurement;

/// <summary>What the model did on one tool call.</summary>
public sealed record StepRecord(
    int Index,
    string Tool,
    string ArgsJson,
    string Kind,          // meta | wrapper | invoke_tool | unknown
    bool TargetHit,       // the call addressed the task's downstream tool
    bool IsError,
    string ResultPreview);

/// <summary>One run of one task under one condition.</summary>
public sealed class RunRecord
{
    public required string Condition { get; init; }
    public required string TaskId { get; init; }
    public required int Run { get; init; }
    public List<StepRecord> Steps { get; } = [];

    /// <summary>The first call that tried to reach a downstream (wrapper or invoke_tool), if any.</summary>
    public StepRecord? FirstDownstreamAttempt { get; set; }

    /// <summary>The first downstream attempt addressed the right tool and did not error.</summary>
    public bool FirstAttemptSuccess { get; set; }

    /// <summary>The right downstream tool received usable arguments at some point in the run.</summary>
    public bool Completed { get; set; }

    public int? StepsToCompletion { get; set; }
    public int ModelTurns { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double DurationMs { get; set; }
    public string? Error { get; set; }
    public string? FinalText { get; set; }
}

public sealed record RunnerSettings(int MaxSteps, bool NoThink, bool Verbose);

/// <summary>
/// A deliberately plain agent loop: send the conversation with the tools, execute whatever the
/// model calls through the aggregator's MCP client, feed results back, repeat. No
/// FunctionInvokingChatClient, because the point is to observe each call, not to hide it.
/// </summary>
public static class Runner
{
    private const int MaxResultChars = 4000;

    public static async Task<RunRecord> RunAsync(
        AggregatorRig rig,
        IChatClient chat,
        MeasureTask task,
        int runIndex,
        RunnerSettings settings,
        CancellationToken ct)
    {
        var record = new RunRecord { Condition = rig.Condition.ToString(), TaskId = task.Id, Run = runIndex };
        var expectedWrapper = WrapperNaming.For(task.Server, task.Tool);
        var sw = Stopwatch.StartNew();

        try
        {
            var tools = await rig.ListModelToolsAsync(ct);
            var options = new ChatOptions { Tools = tools.Cast<AITool>().ToList(), ToolMode = ChatToolMode.Auto };

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, BuildSystemPrompt(rig.Instructions)),
                new(ChatRole.User, task.Prompt),
            };

            var stepIndex = 0;
            while (stepIndex < settings.MaxSteps)
            {
                var response = await chat.GetResponseAsync(messages, options, ct);
                record.ModelTurns++;
                record.InputTokens += response.Usage?.InputTokenCount ?? 0;
                record.OutputTokens += response.Usage?.OutputTokenCount ?? 0;
                messages.AddMessages(response);

                var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
                if (calls.Count == 0)
                {
                    record.FinalText = response.Text;
                    if (settings.Verbose) Console.WriteLine($"      model: {Truncate(response.Text, 160)}");
                    break;
                }

                foreach (var call in calls)
                {
                    stepIndex++;
                    var step = await ExecuteAsync(rig, call, stepIndex, task, expectedWrapper, ct);
                    record.Steps.Add(step);

                    if (settings.Verbose)
                        Console.WriteLine($"      [{step.Index}] {step.Tool}({Truncate(step.ArgsJson, 120)}) -> {(step.IsError ? "ERROR" : "ok")}{(step.TargetHit ? " [target]" : "")}");

                    if (step.Kind is "wrapper" or "invoke_tool" && record.FirstDownstreamAttempt is null)
                    {
                        record.FirstDownstreamAttempt = step;
                        record.FirstAttemptSuccess = step.TargetHit && !step.IsError;
                    }

                    if (step.TargetHit && !step.IsError && !record.Completed)
                    {
                        record.Completed = true;
                        record.StepsToCompletion = step.Index;
                    }

                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, step.IsError ? "ERROR: " + step.ResultPreview : step.ResultPreview)]));

                    if (stepIndex >= settings.MaxSteps)
                        break;
                }

                // The task is done once the downstream has what it needs; the model's closing
                // sentence is not part of the measurement and would only cost tokens.
                if (record.Completed)
                    break;

                // A host that honors tools/list_changed sees newly activated wrappers on its next
                // turn. Re-list in Lazy mode to model that; Eager never changes, InvokeTool never may.
                if (rig.Condition == Condition.Lazy)
                {
                    tools = await rig.ListModelToolsAsync(ct);
                    options.Tools = tools.Cast<AITool>().ToList();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Error = $"{ex.GetType().Name}: {ex.Message}";
            if (settings.Verbose) Console.WriteLine($"      error: {record.Error}");
        }

        record.DurationMs = sw.Elapsed.TotalMilliseconds;
        return record;
    }

    private static async Task<StepRecord> ExecuteAsync(
        AggregatorRig rig, FunctionCallContent call, int index, MeasureTask task, string expectedWrapper, CancellationToken ct)
    {
        var args = call.Arguments is null
            ? new Dictionary<string, object?>()
            : call.Arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var argsJson = JsonSerializer.Serialize(args);

        var kind = call.Name == "invoke_tool" ? "invoke_tool"
            : rig.MetaToolNames.Contains(call.Name) ? "meta"
            : call.Name.Contains(WrapperNaming.Separator) ? "wrapper"
            : "unknown";

        var targetHit = kind switch
        {
            "wrapper" => string.Equals(call.Name, expectedWrapper, StringComparison.Ordinal),
            "invoke_tool" => string.Equals(ArgString(args, "serverName"), task.Server, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(ArgString(args, "toolName"), task.Tool, StringComparison.Ordinal),
            _ => false,
        };

        bool isError;
        string text;
        try
        {
            var result = await rig.Client.CallToolAsync(call.Name, args, cancellationToken: ct);
            isError = result.IsError ?? false;
            text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            if (string.IsNullOrEmpty(text) && result.StructuredContent is { } structured)
                text = structured.GetRawText();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A JSON-RPC fault (should not happen for known shapes, but the model may invent one).
            isError = true;
            text = ex.Message;
        }

        return new StepRecord(index, call.Name, argsJson, kind, targetHit, isError, Truncate(text, MaxResultChars));
    }

    private static string? ArgString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement e => e.GetRawText(),
            _ => value.ToString(),
        };
    }

    private static string BuildSystemPrompt(string instructions)
    {
        var today = DateTime.UtcNow;
        return $"""
            You are an assistant that completes the user's request by calling the tools available to you.
            Today is {today:yyyy-MM-dd} ({today.DayOfWeek}); treat times as UTC. Make reasonable assumptions instead
            of asking clarifying questions. Call the tool that carries out the request; when it has been carried out,
            reply with one short sentence confirming what was done.

            The tools come from an MCP server that provided these instructions:

            {instructions}
            """;
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max] + "…";
}
