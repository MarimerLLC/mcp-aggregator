using McpAggregator.Measure.Stubs;

namespace McpAggregator.Measure.Measurement;

/// <summary>
/// One task in the fixed set: a user request whose correct handling is a single downstream tool
/// call with usable arguments. <see cref="ScriptedArgs"/> is what the scripted fake model sends,
/// and doubles as documentation of what "usable" means.
/// </summary>
public sealed record MeasureTask(
    string Id,
    string Prompt,
    string Server,
    string Tool,
    IReadOnlyDictionary<string, object?> ScriptedArgs);

public static class MeasureTasks
{
    /// <summary>The fixed task set from issue #39's "What to measure".</summary>
    public static IReadOnlyList<MeasureTask> All { get; } =
    [
        new("send_email",
            "Send an email to alice@example.com with the subject \"Lunch Thursday?\" and the message \"Are you free for lunch on Thursday at noon?\"",
            StubDownstreams.Adjutant, "send_email",
            new Dictionary<string, object?>
            {
                ["to"] = new[] { "alice@example.com" },
                ["subject"] = "Lunch Thursday?",
                ["body"] = "Are you free for lunch on Thursday at noon?",
            }),

        new("calendar_tomorrow",
            "What is on my calendar tomorrow?",
            StubDownstreams.Adjutant, "get_calendar_events",
            new Dictionary<string, object?>
            {
                ["start"] = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd"),
                ["end"] = DateTime.UtcNow.Date.AddDays(2).ToString("yyyy-MM-dd"),
            }),

        new("list_files_marimer",
            "List the files in the /Documents/Reports folder of the Marimer company OneDrive.",
            StubDownstreams.OneDriveMarimer, "list_files",
            new Dictionary<string, object?> { ["path"] = "/Documents/Reports" }),

        new("list_files_personal",
            "Show me what is in the Photos folder of my personal OneDrive.",
            StubDownstreams.OneDrivePersonal, "list_files",
            new Dictionary<string, object?> { ["path"] = "/Photos" }),

        new("docs_search",
            "Search the Microsoft documentation for how to configure the options pattern in ASP.NET Core.",
            StubDownstreams.MicrosoftLearn, "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "options pattern ASP.NET Core configuration" }),
    ];
}
