using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpAggregator.Measure.Measurement;

public sealed record ConditionStats(string Condition, int InitialToolCount, int InitialToolsListBytes, int FinalToolCount, int FinalToolsListBytes);

public sealed class MeasurementReport
{
    public required string Model { get; init; }
    public required string Provider { get; init; }
    public required string Endpoint { get; init; }

    /// <summary>The server's own description of the model (from GET /models), when it offers one.</summary>
    public string? ModelInfo { get; init; }
    public required bool NoThink { get; init; }
    public required bool RealDocs { get; init; }
    public required int RunsPerTask { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public List<ConditionStats> Conditions { get; } = [];
    public List<RunRecord> Runs { get; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Markdown in the shape of the tables in docs/typed-wrapper-tools.md, "Measurements".</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Run: `{Model}` via {Provider} ({Endpoint}){(NoThink ? ", thinking off" : ", thinking on")}, {RunsPerTask} runs/task, {StartedAt:yyyy-MM-dd HH:mm} UTC");
        if (ModelInfo is not null)
            sb.AppendLine($"Model info: `{ModelInfo}`");
        sb.AppendLine();

        sb.AppendLine("#### Reliability by condition and task");
        sb.AppendLine();
        sb.AppendLine("| Condition | Task | Runs | First-call success | Completed | Wrong tool first | Avg steps to done | Avg turns | Avg input tok | Avg output tok | Errors |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in Runs.GroupBy(r => (r.Condition, r.TaskId)).OrderBy(g => g.Key.Condition).ThenBy(g => g.Key.TaskId))
        {
            var runs = group.ToList();
            var n = runs.Count;
            var first = runs.Count(r => r.FirstAttemptSuccess);
            var done = runs.Count(r => r.Completed);
            var wrongFirst = runs.Count(r => r.FirstDownstreamAttempt is { TargetHit: false });
            var avgSteps = runs.Where(r => r.StepsToCompletion is not null).Select(r => (double)r.StepsToCompletion!).DefaultIfEmpty(double.NaN).Average();
            sb.AppendLine($"| {group.Key.Condition} | {group.Key.TaskId} | {n} | {Pct(first, n)} | {Pct(done, n)} | {wrongFirst} | {Fmt(avgSteps)} | {runs.Average(r => r.ModelTurns):0.0} | {runs.Average(r => r.InputTokens):0} | {runs.Average(r => r.OutputTokens):0} | {runs.Count(r => r.Error is not null)} |");
        }
        sb.AppendLine();

        sb.AppendLine("#### Reliability by condition (all tasks)");
        sb.AppendLine();
        sb.AppendLine("| Condition | Runs | First-call success | Completed | Avg input tok / run | Avg output tok / run | Avg ms / run |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var group in Runs.GroupBy(r => r.Condition).OrderBy(g => g.Key))
        {
            var runs = group.ToList();
            var n = runs.Count;
            sb.AppendLine($"| {group.Key} | {n} | {Pct(runs.Count(r => r.FirstAttemptSuccess), n)} | {Pct(runs.Count(r => r.Completed), n)} | {runs.Average(r => r.InputTokens):0} | {runs.Average(r => r.OutputTokens):0} | {runs.Average(r => r.DurationMs):0} |");
        }
        sb.AppendLine();

        sb.AppendLine("#### tools/list size by condition");
        sb.AppendLine();
        sb.AppendLine("| Condition | Tools at start | Bytes at start | Tools at end | Bytes at end |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var c in Conditions)
            sb.AppendLine($"| {c.Condition} | {c.InitialToolCount} | {c.InitialToolsListBytes} | {c.FinalToolCount} | {c.FinalToolsListBytes} |");
        sb.AppendLine();

        var failures = Runs.Where(r => !r.FirstAttemptSuccess).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine("#### First-attempt failures (what the model actually sent)");
            sb.AppendLine();
            sb.AppendLine("| Condition | Task | Run | First downstream call | Result |");
            sb.AppendLine("|---|---|---:|---|---|");
            foreach (var r in failures)
            {
                var a = r.FirstDownstreamAttempt;
                var call = a is null ? "(never reached a downstream)" : $"`{a.Tool}({Escape(Trunc(a.ArgsJson, 140))})`";
                var result = a is null ? (r.Error ?? Trunc(r.FinalText, 100)) : (a.IsError ? "error: " + Escape(Trunc(a.ResultPreview, 140)) : "ok, wrong tool");
                sb.AppendLine($"| {r.Condition} | {r.TaskId} | {r.Run} | {call} | {result} |");
            }
        }

        return sb.ToString();
    }

    private static string Pct(int n, int of) => of == 0 ? "-" : $"{n}/{of} ({100.0 * n / of:0}%)";
    private static string Fmt(double d) => double.IsNaN(d) ? "-" : d.ToString("0.0");
    private static string Trunc(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
