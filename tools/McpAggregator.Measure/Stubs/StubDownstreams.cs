using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using McpAggregator.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Measure.Stubs;

/// <summary>One downstream tool invocation as the stub saw it, after SDK argument binding.</summary>
public sealed record RecordedCall(string Server, string Tool, IReadOnlyDictionary<string, object?> Args, bool Valid, string? Problem);

/// <summary>Collects every call the stub downstreams receive, across all servers.</summary>
public sealed class CallRecorder
{
    private readonly ConcurrentQueue<RecordedCall> _calls = new();

    public IReadOnlyList<RecordedCall> Calls => [.. _calls];

    public int Count => _calls.Count;

    public void Add(RecordedCall call) => _calls.Enqueue(call);

    /// <summary>Calls recorded after position <paramref name="since"/>.</summary>
    public IReadOnlyList<RecordedCall> Since(int since) => Calls.Skip(since).ToList();
}

/// <summary>
/// In-memory stand-ins for the downstream servers in issue #39's inventory. The schemas are what
/// matter — they are what the model has to author arguments against — so each tool declares the
/// real parameter shape (arrays, optionals, dates) and validates the values it receives. Every
/// call is recorded so the harness can tell whether the model hit the right tool with usable
/// arguments, independent of what the model says afterwards.
/// </summary>
public static class StubDownstreams
{
    public const string Adjutant = "adjutant";
    public const string OneDriveMarimer = "onedrive-marimer";
    public const string OneDrivePersonal = "onedrive-personal";
    public const string MicrosoftLearn = "microsoft-learn";

    public static IReadOnlyList<RegisteredServer> Registrations(bool realDocs) =>
    [
        Server(Adjutant, "Adjutant", "Email and calendar assistant for the user's Microsoft 365 accounts."),
        Server(OneDriveMarimer, "OneDrive (Marimer)", "Files in the Marimer LLC company OneDrive."),
        Server(OneDrivePersonal, "OneDrive (Personal)", "Files in the user's personal OneDrive."),
        realDocs
            ? new RegisteredServer
            {
                Name = MicrosoftLearn,
                DisplayName = "Microsoft Learn Docs",
                Description = "Official Microsoft Learn MCP Server - search and fetch Microsoft documentation and code samples.",
                Transport = new TransportConfig { Type = TransportType.Http, Url = "https://learn.microsoft.com/api/mcp" }
            }
            : Server(MicrosoftLearn, "Microsoft Learn Docs", "Search and fetch Microsoft/Azure official documentation articles and code samples."),
    ];

    private static RegisteredServer Server(string name, string displayName, string description) => new()
    {
        Name = name,
        DisplayName = displayName,
        Description = description,
        Transport = new TransportConfig { Type = TransportType.Stdio, Command = "stub" }
    };

    /// <summary>Builds the tool collection for one stub server, wired to <paramref name="recorder"/>.</summary>
    public static McpServerPrimitiveCollection<McpServerTool> ToolsFor(string server, CallRecorder recorder)
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();

        switch (server)
        {
            case Adjutant:
                tools.Add(Tool("send_email", "Send an email from one of the user's accounts.",
                    ([Description("Recipient email addresses")] string[] to,
                     [Description("Subject line")] string subject,
                     [Description("Message body (plain text)")] string body,
                     [Description("Account to send from; omit for the default account")] string? accountId = null,
                     [Description("CC email addresses")] string[]? cc = null) =>
                    {
                        var problem = to is not { Length: > 0 } ? "'to' must contain at least one address"
                            : to.Any(a => !a.Contains('@')) ? "'to' contains an invalid address"
                            : string.IsNullOrWhiteSpace(subject) ? "'subject' is empty"
                            : string.IsNullOrWhiteSpace(body) ? "'body' is empty"
                            : null;
                        return Record(recorder, Adjutant, "send_email",
                            Args(("to", to), ("subject", subject), ("body", body), ("accountId", accountId), ("cc", cc)),
                            problem, $"Sent to {string.Join(", ", to ?? [])}: '{subject}'. Message id msg_{Guid.NewGuid():N}.");
                    }));

                tools.Add(Tool("get_calendar_events", "List calendar events between two instants.",
                    ([Description("Range start, ISO 8601 date or date-time")] string start,
                     [Description("Range end, ISO 8601 date or date-time")] string end,
                     [Description("Account whose calendar to read; omit for the default account")] string? accountId = null) =>
                    {
                        var problem = !DateTimeOffset.TryParse(start, out var s) ? "'start' is not an ISO 8601 date"
                            : !DateTimeOffset.TryParse(end, out var e) ? "'end' is not an ISO 8601 date"
                            : e <= s ? "'end' must be after 'start'"
                            : null;
                        return Record(recorder, Adjutant, "get_calendar_events",
                            Args(("start", start), ("end", end), ("accountId", accountId)),
                            problem, """[{"subject":"Team sync","start":"09:00","end":"09:30"},{"subject":"1:1 with Sam","start":"14:00","end":"14:30"}]""");
                    }));

                tools.Add(Tool("list_accounts", "List the mail/calendar accounts the assistant can act on.",
                    () => Record(recorder, Adjutant, "list_accounts", Args(), null,
                        """[{"id":"work","address":"rocky@marimer.example"},{"id":"personal","address":"rocky@example.com"}]""")));

                tools.Add(Tool("create_calendar_event", "Create a calendar event.",
                    ([Description("Event title")] string title,
                     [Description("Start, ISO 8601 date-time")] string start,
                     [Description("End, ISO 8601 date-time")] string end,
                     [Description("Attendee email addresses")] string[]? attendees = null) =>
                        Record(recorder, Adjutant, "create_calendar_event",
                            Args(("title", title), ("start", start), ("end", end), ("attendees", attendees)),
                            string.IsNullOrWhiteSpace(title) ? "'title' is empty" : null,
                            $"Created '{title}'.")));
                break;

            case OneDriveMarimer:
            case OneDrivePersonal:
                tools.Add(Tool("list_files", "List the files and folders at a path.",
                    ([Description("Folder path, e.g. '/Documents/Reports'")] string path,
                     [Description("Include subfolders")] bool? recursive = null) =>
                        Record(recorder, server, "list_files",
                            Args(("path", path), ("recursive", recursive)),
                            string.IsNullOrWhiteSpace(path) ? "'path' is empty" : null,
                            """[{"name":"Q3-report.docx","size":48213},{"name":"budget.xlsx","size":9021},{"name":"archive","folder":true}]""")));

                tools.Add(Tool("get_file", "Download a file's content as text.",
                    ([Description("File path")] string path) =>
                        Record(recorder, server, "get_file", Args(("path", path)),
                            string.IsNullOrWhiteSpace(path) ? "'path' is empty" : null, "(file content)")));

                tools.Add(Tool("search_files", "Search file names and content.",
                    ([Description("Search text")] string query) =>
                        Record(recorder, server, "search_files", Args(("query", query)),
                            string.IsNullOrWhiteSpace(query) ? "'query' is empty" : null, "[]")));
                break;

            case MicrosoftLearn:
                tools.Add(Tool("microsoft_docs_search", "Search official Microsoft/Azure documentation for a query.",
                    ([Description("A query or topic about Microsoft/Azure products, services, platforms, developer tools, frameworks, or APIs")] string query) =>
                        Record(recorder, MicrosoftLearn, "microsoft_docs_search", Args(("query", query)),
                            string.IsNullOrWhiteSpace(query) ? "'query' is empty" : null,
                            """{"results":[{"title":"Options pattern in ASP.NET Core","url":"https://learn.microsoft.com/aspnet/core/fundamentals/configuration/options","content":"..."}]}""")));

                tools.Add(Tool("microsoft_docs_fetch", "Fetch a Microsoft Learn page as markdown.",
                    ([Description("Page URL")] string url) =>
                        Record(recorder, MicrosoftLearn, "microsoft_docs_fetch", Args(("url", url)),
                            Uri.TryCreate(url, UriKind.Absolute, out _) ? null : "'url' is not absolute", "# page")));

                tools.Add(Tool("microsoft_code_sample_search", "Search official Microsoft code samples.",
                    ([Description("Search query")] string query,
                     [Description("Programming language filter")] string? language = null) =>
                        Record(recorder, MicrosoftLearn, "microsoft_code_sample_search", Args(("query", query), ("language", language)),
                            string.IsNullOrWhiteSpace(query) ? "'query' is empty" : null, "[]")));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(server), server, "No stub for this server");
        }

        return tools;
    }

    private static McpServerTool Tool(string name, string description, Delegate method)
        => McpServerTool.Create(method, new McpServerToolCreateOptions { Name = name, Description = description });

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static CallToolResult Record(CallRecorder recorder, string server, string tool,
        Dictionary<string, object?> args, string? problem, string okText)
    {
        recorder.Add(new RecordedCall(server, tool, args, problem is null, problem));
        return problem is null
            ? new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = okText }] }
            : new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"Invalid arguments: {problem}." }] };
    }
}
