using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.Inference;
using McpAggregator.Measure.Hosting;
using McpAggregator.Measure.Measurement;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using OpenAI;

namespace McpAggregator.Measure;

internal static class Program
{
    private const string Help = """
        McpAggregator.Measure — reliability and token-cost measurements for typed wrapper tools (issue #39).

        Hosts the aggregator in-process over stub downstreams (adjutant, onedrive-marimer, onedrive-personal,
        microsoft-learn) and drives it with a model under three conditions:
          eager        WrapperMode=Eager: every {server}__{tool} wrapper is in the tool list from the start
          lazy         WrapperMode=Lazy: find_tools / get_service_details activate wrappers; list refreshed each turn
          invoke_tool  the pre-#39 surface: no find_tools, no wrappers, only invoke_tool with a JSON-string argument

        Options (environment variable fallback in brackets):
          --endpoint <url>       OpenAI-compatible base URL, e.g. https://openrouter.ai/api/v1 or http://host:11434/v1
                                 For --provider azure: the Foundry .../models URL           [MEASURE_LLM_ENDPOINT]
          --api-key <key>        API key; any value for servers that do not check it         [MEASURE_LLM_APIKEY]
          --model <id>           Model id / deployment name, e.g. qwen/qwen3-8b or qwen3:8b  [MEASURE_LLM_MODEL]
          --provider <p>         openai (default) | azure | scripted                         [MEASURE_LLM_PROVIDER]
          --runs <n>             Runs per task per condition (default 10)
          --conditions <list>    Comma list of eager,lazy,invoke_tool (default all)
          --tasks <list>         Comma list of task ids (default all): send_email, calendar_tomorrow,
                                 list_files_marimer, list_files_personal, docs_search
          --max-steps <n>        Tool calls allowed per run before giving up (default 8)
          --no-think             Turn model thinking off at the request level (see --no-think-json)
          --no-think-json <json> JSON object merged into every chat request when --no-think is set
                                 (default {"chat_template_kwargs":{"enable_thinking":false}}, llama.cpp/vLLM;
                                 use {"reasoning_effort":"none"} for OpenAI-style servers)
          --real-docs            Use the real Microsoft Learn MCP server instead of its stub
          --out <path>           Write the full JSON record here (default measure-<timestamp>.json)
          --md <path>            Also write the markdown summary here
          --verbose              Print every model turn and tool call
          --dry-run              Build the rigs and print tools/list sizes; no model calls
          --help

        The "scripted" provider is a fake model that always makes the right call; use it to validate the harness.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Help);
            return 0;
        }

        var a = ParseArgs(args);
        string Opt(string name, string envName, string? fallback = null)
            => a.TryGetValue(name, out var v) ? v : Environment.GetEnvironmentVariable(envName) ?? fallback ?? string.Empty;

        var provider = Opt("provider", "MEASURE_LLM_PROVIDER", "openai").ToLowerInvariant();
        var endpoint = Opt("endpoint", "MEASURE_LLM_ENDPOINT");
        var apiKey = Opt("api-key", "MEASURE_LLM_APIKEY");
        var model = Opt("model", "MEASURE_LLM_MODEL");
        var runs = a.TryGetValue("runs", out var r) ? int.Parse(r) : 10;
        var maxSteps = a.TryGetValue("max-steps", out var ms) ? int.Parse(ms) : 8;
        var noThink = a.ContainsKey("no-think");
        var noThinkJson = a.TryGetValue("no-think-json", out var ntj) ? ntj : "{\"chat_template_kwargs\":{\"enable_thinking\":false}}";
        var realDocs = a.ContainsKey("real-docs");
        var verbose = a.ContainsKey("verbose");
        var dryRun = a.ContainsKey("dry-run");
        var conditions = (a.TryGetValue("conditions", out var c) ? c : "eager,lazy,invoke_tool")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseCondition).ToList();
        var taskIds = a.TryGetValue("tasks", out var t)
            ? t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var tasks = MeasureTasks.All.Where(x => taskIds is null || taskIds.Contains(x.Id)).ToList();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outPath = a.TryGetValue("out", out var o) ? o : $"measure-{stamp}.json";
        var mdPath = a.TryGetValue("md", out var m) ? m : null;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;

        if (dryRun)
        {
            foreach (var condition in conditions)
            {
                await using var rig = await AggregatorRig.CreateAsync(condition, realDocs, ct);
                var tools = await rig.ListModelToolsAsync(ct);
                Console.WriteLine($"{condition}: {tools.Count} tools, {ToolsListBytes(tools)} bytes");
                foreach (var tool in tools.OrderBy(x => x.Name, StringComparer.Ordinal))
                    Console.WriteLine($"  {tool.Name}");
            }
            return 0;
        }

        if (provider != "scripted" && (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(model)))
        {
            Console.Error.WriteLine("Need --endpoint and --model (or MEASURE_LLM_ENDPOINT / MEASURE_LLM_MODEL). See --help.");
            return 2;
        }

        var scripted = provider == "scripted" ? new ScriptedChatClient() : null;
        using var chat = scripted ?? BuildChatClient(provider, endpoint, apiKey, model, noThink ? noThinkJson : null);

        var report = new MeasurementReport
        {
            Model = provider == "scripted" ? "scripted" : model,
            Provider = provider,
            Endpoint = provider == "scripted" ? "-" : endpoint,
            ModelInfo = provider == "openai" ? await TryDescribeModelAsync(endpoint, apiKey, model, ct) : null,
            NoThink = noThink,
            RealDocs = realDocs,
            RunsPerTask = runs,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var settings = new RunnerSettings(maxSteps, noThink, verbose);

        Console.WriteLine($"Model {report.Model} via {provider}; {runs} runs x {tasks.Count} tasks x {conditions.Count} conditions{(noThink ? "; thinking off" : "")}{(realDocs ? "; real Microsoft Learn" : "")}");

        foreach (var condition in conditions)
        {
            Console.WriteLine($"== {condition}");

            // Activation in Lazy mode is process-wide and sticks, so a shared rig would be Eager
            // from the second run on. Lazy therefore gets a cold aggregator per run; the other
            // two conditions never change and share one.
            var coldPerRun = condition == Condition.Lazy;
            var shared = coldPerRun ? null : await AggregatorRig.CreateAsync(condition, realDocs, ct);
            await using var _ = shared;

            int initialCount, initialBytes, finalCount = 0, finalBytes = 0;
            {
                await using var probe = coldPerRun ? await AggregatorRig.CreateAsync(condition, realDocs, ct) : null;
                var initial = await (shared ?? probe!).ListModelToolsAsync(ct);
                initialCount = initial.Count;
                initialBytes = ToolsListBytes(initial);
            }
            Console.WriteLine($"   tools/list: {initialCount} tools, {initialBytes} bytes{(coldPerRun ? " (cold aggregator per run)" : "")}");

            foreach (var task in tasks)
            {
                var ok = 0;
                var done = 0;
                for (var i = 1; i <= runs; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    if (scripted is not null) scripted.CurrentTask = task;
                    if (verbose) Console.WriteLine($"   {task.Id} #{i}");

                    var rig = shared ?? await AggregatorRig.CreateAsync(condition, realDocs, ct);
                    try
                    {
                        var record = await Runner.RunAsync(rig, chat, task, i, settings, ct);
                        report.Runs.Add(record);
                        if (record.FirstAttemptSuccess) ok++;
                        if (record.Completed) done++;

                        var final = await rig.ListModelToolsAsync(ct);
                        finalCount = final.Count;
                        finalBytes = ToolsListBytes(final);

                        if (!verbose)
                            Console.Write(record.Error is not null ? 'x' : record.FirstAttemptSuccess ? '.' : record.Completed ? 'r' : '!');
                    }
                    finally
                    {
                        if (coldPerRun) await rig.DisposeAsync();
                    }
                }
                if (!verbose) Console.WriteLine();
                Console.WriteLine($"   {task.Id,-22} first-call {ok}/{runs}  completed {done}/{runs}");
            }

            report.Conditions.Add(new ConditionStats(condition.ToString(), initialCount, initialBytes, finalCount, finalBytes));
        }

        var markdown = report.ToMarkdown();
        Console.WriteLine();
        Console.WriteLine(markdown);

        await File.WriteAllTextAsync(outPath, report.ToJson(), ct);
        Console.WriteLine($"Wrote {outPath}");
        if (mdPath is not null)
        {
            await File.WriteAllTextAsync(mdPath, markdown, ct);
            Console.WriteLine($"Wrote {mdPath}");
        }

        return 0;
    }

    private static IChatClient BuildChatClient(string provider, string endpoint, string apiKey, string model, string? extraRequestJson)
    {
        switch (provider)
        {
            case "azure":
                return new ChatCompletionsClient(new Uri(endpoint), new AzureKeyCredential(apiKey)).AsIChatClient(model);

            case "openai":
                var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
                if (extraRequestJson is not null)
                {
                    var http = new HttpClient(new RequestJsonMergeHandler(extraRequestJson)) { Timeout = TimeSpan.FromMinutes(10) };
                    options.Transport = new HttpClientPipelineTransport(http);
                }
                return new OpenAIClient(new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey), options)
                    .GetChatClient(model)
                    .AsIChatClient();

            default:
                throw new ArgumentException($"Unknown provider '{provider}'. Use openai, azure, or scripted.");
        }
    }

    /// <summary>GET {endpoint}/models and return the entry for <paramref name="model"/>, compacted.</summary>
    private static async Task<string?> TryDescribeModelAsync(string endpoint, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            if (!string.IsNullOrEmpty(apiKey))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var json = await http.GetStringAsync(endpoint.TrimEnd('/') + "/models", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.TryGetProperty("id", out var id) && id.GetString() == model)
                {
                    // llama.cpp puts the interesting facts (parameters, quantization, context) under "meta".
                    return entry.TryGetProperty("meta", out var meta) ? meta.GetRawText() : entry.GetRawText();
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"(GET /models failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Merges a JSON object into the body of every chat-completions POST. How thinking is switched
    /// off differs per server (llama.cpp and vLLM read chat_template_kwargs, OpenAI-style servers
    /// read reasoning_effort) and the OpenAI client has no first-class way to add such fields.
    /// </summary>
    private sealed class RequestJsonMergeHandler(string extraJson) : DelegatingHandler(new HttpClientHandler())
    {
        private readonly JsonElement _extra = JsonDocument.Parse(extraJson).RootElement.Clone();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.Content is not null
                && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                var node = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
                if (node is not null)
                {
                    foreach (var property in _extra.EnumerateObject())
                        node[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
                    request.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Size of the tools array as it would appear in a tools/list response.</summary>
    private static int ToolsListBytes(IEnumerable<McpClientTool> tools)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool).ToList(), McpJsonUtilities.DefaultOptions));

    private static Condition ParseCondition(string s) => s.ToLowerInvariant() switch
    {
        "eager" => Condition.Eager,
        "lazy" => Condition.Lazy,
        "invoke_tool" or "invoke-tool" or "invoketool" => Condition.InvokeTool,
        _ => throw new ArgumentException($"Unknown condition '{s}'. Use eager, lazy, invoke_tool."),
    };

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{args[i]}'. See --help.");
            var name = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            result[name] = hasValue ? args[++i] : "true";
        }
        return result;
    }
}
