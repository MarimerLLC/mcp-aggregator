using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace McpAggregator.Core.Services;

/// <summary>
/// Reads a server's <see cref="SkillSources"/> for skill revision (issue #59): release notes from an
/// allow-listed https host or a GitHub location, and the server's own documentation through its guide
/// tool and resources. Everything is size-capped, and every failure becomes a
/// <see cref="SkillSourceReport.Error"/> rather than an exception: a revision goes ahead with whatever
/// could be read. What comes back is untrusted text, and <see cref="SkillMaintainer"/> hands it to the
/// model as data.
/// </summary>
public sealed partial class SkillSourceReader
{
    private readonly ToolProxyHandler _proxy;
    private readonly SkillMaintenanceOptions _options;
    private readonly ILogger<SkillSourceReader> _logger;
    private readonly HttpClient _http;

    public SkillSourceReader(
        ToolProxyHandler proxy,
        IOptions<SkillMaintenanceOptions> options,
        ILogger<SkillSourceReader> logger,
        HttpClient? http = null)
    {
        _proxy = proxy;
        _options = options.Value;
        _logger = logger;
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// Checks a change-notes location without fetching it: an https URL on an allowed host, or a
    /// well-formed GitHub location with <c>api.github.com</c> allowed. Null when it is acceptable.
    /// </summary>
    public string? ValidateChangeNotes(string location)
    {
        if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "Change notes must be fetched over https.";
        if (location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(location, UriKind.Absolute, out var uri) && IsAllowed(uri)
                ? null
                : $"The host of '{location}' is not in SkillMaintenance:AllowedChangeNoteHosts ({string.Join(", ", _options.AllowedChangeNoteHosts)}).";
        }

        if (!GitHubLocation().IsMatch(location))
            return $"'{location}' is neither an https URL nor a GitHub location like 'owner/repo:path' or 'owner/repo@ref:path'.";
        return IsAllowedHost("api.github.com")
            ? null
            : "GitHub change notes need 'api.github.com' in SkillMaintenance:AllowedChangeNoteHosts.";
    }

    /// <param name="since">The skill's baseline date: dated changelog files before it are skipped.</param>
    public async Task<SkillSourceMaterial> ReadAsync(RegisteredServer server, DateTimeOffset? since, CancellationToken ct)
    {
        var sources = server.SkillSources;
        if (sources is null || sources.IsEmpty)
            return SkillSourceMaterial.None;

        var budget = new Budget(_options.MaxSourceChars, _options.MaxSourcesTotalChars);
        var reports = new List<SkillSourceReport>();
        var guides = new StringBuilder();
        var notes = new StringBuilder();

        // The server's own documentation first: it is the more authoritative of the two, and the
        // budget runs out in order.
        if (!string.IsNullOrWhiteSpace(sources.GuideTool))
        {
            var calls = sources.GuideArguments is { Count: > 0 } a ? a : [JsonSerializer.SerializeToElement(new { })];
            foreach (var args in calls)
                await ReadGuideToolAsync(server.Name, sources.GuideTool!, args, budget, guides, reports, ct);
        }
        foreach (var uri in sources.GuideResources ?? [])
            await ReadGuideResourceAsync(server.Name, uri, budget, guides, reports, ct);

        if (!string.IsNullOrWhiteSpace(sources.ChangeNotes))
            await ReadChangeNotesAsync(sources.ChangeNotes!.Trim(), since, budget, notes, reports, ct);

        return new SkillSourceMaterial
        {
            ChangeNotes = notes.ToString().TrimEnd(),
            Guides = guides.ToString().TrimEnd(),
            Reports = reports,
        };
    }

    // ---------------------------------------------------------------- guides (the server's own documentation)

    private async Task ReadGuideToolAsync(
        string server, string tool, JsonElement args, Budget budget, StringBuilder into, List<SkillSourceReport> reports, CancellationToken ct)
    {
        var label = $"{tool} {args.GetRawText()}";
        var report = new SkillSourceReport { Kind = "guide_tool", Source = label };
        reports.Add(report);
        try
        {
            if (args.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("guide arguments must be JSON objects");

            using var cts = Linked(ct);
            var dict = args.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            var result = await _proxy.InvokeAsync(server, tool, dict, InvocationPath.SkillMaintenance, cts.Token);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            if (result.IsError ?? false)
                throw new InvalidOperationException(Shorten(text));
            Append(into, label, text, budget, report);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            report.Error = Describe(ex);
            _logger.LogInformation("Skill source {Source} on '{Server}' could not be read: {Error}", label, server, report.Error);
        }
    }

    private async Task ReadGuideResourceAsync(
        string server, string uri, Budget budget, StringBuilder into, List<SkillSourceReport> reports, CancellationToken ct)
    {
        var report = new SkillSourceReport { Kind = "guide_resource", Source = uri };
        reports.Add(report);
        try
        {
            using var cts = Linked(ct);
            var result = await _proxy.ReadResourceAsync(server, uri, InvocationPath.SkillMaintenance, cts.Token);
            var text = string.Join("\n", result.Contents.OfType<TextResourceContents>().Select(c => c.Text));
            Append(into, uri, text, budget, report);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            report.Error = Describe(ex);
            _logger.LogInformation("Skill source {Source} on '{Server}' could not be read: {Error}", uri, server, report.Error);
        }
    }

    // ---------------------------------------------------------------- change notes (outside the server)

    private async Task ReadChangeNotesAsync(
        string location, DateTimeOffset? since, Budget budget, StringBuilder into, List<SkillSourceReport> reports, CancellationToken ct)
    {
        if (ValidateChangeNotes(location) is { } invalid)
        {
            reports.Add(new SkillSourceReport { Kind = "change_notes", Source = location, Error = invalid });
            return;
        }

        if (location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var report = new SkillSourceReport { Kind = "change_notes", Source = location };
            reports.Add(report);
            await FetchTextAsync(new Uri(location), location, budget, into, report, ct);
            return;
        }

        var m = GitHubLocation().Match(location);
        var owner = m.Groups["owner"].Value;
        var repo = m.Groups["repo"].Value;
        var gitRef = m.Groups["ref"].Success ? m.Groups["ref"].Value : null;
        var path = m.Groups["path"].Value.Trim('/');
        var api = new Uri($"https://api.github.com/repos/{owner}/{repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/")}"
                          + (gitRef is null ? "" : "?ref=" + Uri.EscapeDataString(gitRef)));

        JsonElement listing;
        try
        {
            using var cts = Linked(ct);
            using var request = GitHubRequest(api);
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
            listing = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement.Clone();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            reports.Add(new SkillSourceReport { Kind = "change_notes", Source = location, Error = Describe(ex) });
            return;
        }

        if (listing.ValueKind == JsonValueKind.Object)
        {
            // One file: the contents API returns it base64-encoded.
            var report = new SkillSourceReport { Kind = "change_notes", Source = location };
            reports.Add(report);
            try
            {
                var content = listing.GetProperty("content").GetString() ?? "";
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
                Append(into, location, text, budget, report);
            }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
            {
                report.Error = "GitHub did not return the file's content: " + ex.Message;
            }
            return;
        }

        // A folder: dated changelog files, newest first, from the baseline date on.
        var files = listing.EnumerateArray()
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "file")
            .Select(e => (Name: e.GetProperty("name").GetString() ?? "", Url: e.TryGetProperty("download_url", out var u) ? u.GetString() : null))
            .Where(f => f.Url is not null && (f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            .Select(f => (f.Name, f.Url, Date: DatePrefix(f.Name)))
            .ToList();
        var chosen = since is { } from
            ? files.Where(f => f.Date is { } d && d >= DateOnly.FromDateTime(from.UtcDateTime)).ToList()
            : files;
        if (chosen.Count == 0)
        {
            reports.Add(new SkillSourceReport
            {
                Kind = "change_notes",
                Source = location,
                Error = since is null ? "the folder has no .md or .txt files" : $"no dated changelog files on or after {since:yyyy-MM-dd}",
            });
            return;
        }

        foreach (var file in chosen.OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            var report = new SkillSourceReport { Kind = "change_notes", Source = $"{location}/{file.Name}" };
            reports.Add(report);
            if (budget.TotalLeft <= 0)
            {
                report.Error = "skipped: the size limit for all sources was reached";
                continue;
            }
            if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var raw) || !IsAllowed(raw))
            {
                report.Error = $"download host is not allowed ({raw?.Host})";
                continue;
            }
            await FetchTextAsync(raw, report.Source, budget, into, report, ct);
        }
    }

    private async Task FetchTextAsync(Uri uri, string label, Budget budget, StringBuilder into, SkillSourceReport report, CancellationToken ct)
    {
        try
        {
            using var cts = Linked(ct);
            using var request = uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ? GitHubRequest(uri) : PlainRequest(uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{uri.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");

            // Read no more than the per-source cap (plus a little, to know it was cut).
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var buffer = new char[Math.Max(0, budget.SourceMax) + 1];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cts.Token);
            Append(into, label, new string(buffer, 0, read), budget, report);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            report.Error = Describe(ex);
            _logger.LogInformation("Skill source {Source} could not be read: {Error}", label, report.Error);
        }
    }

    private static HttpRequestMessage PlainRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.TryParseAdd("mcp-aggregator");
        return request;
    }

    private HttpRequestMessage GitHubRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.TryParseAdd("mcp-aggregator");
        if (!string.IsNullOrWhiteSpace(_options.GitHubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.GitHubToken);
        return request;
    }

    // ---------------------------------------------------------------- helpers

    private sealed class Budget(int sourceMax, int totalMax)
    {
        public int SourceMax { get; } = sourceMax;
        public int TotalLeft { get; set; } = totalMax;
    }

    private static void Append(StringBuilder into, string label, string text, Budget budget, SkillSourceReport report)
    {
        var limit = Math.Min(budget.SourceMax, budget.TotalLeft);
        if (limit <= 0)
        {
            report.Error = "skipped: the size limit for all sources was reached";
            return;
        }

        var taken = text.Length > limit ? text[..limit] : text;
        report.Truncated = text.Length > limit;
        report.Chars = taken.Length;
        budget.TotalLeft -= taken.Length;
        into.Append("### ").Append(label).Append('\n').Append(taken.TrimEnd());
        if (report.Truncated)
            into.Append("\n[... cut to the size limit]");
        into.Append("\n\n");
    }

    private bool IsAllowed(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && IsAllowedHost(uri.Host);

    private bool IsAllowedHost(string host)
        => _options.AllowedChangeNoteHosts.Any(h => string.Equals(h.Trim(), host, StringComparison.OrdinalIgnoreCase));

    private CancellationTokenSource Linked(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.SourceTimeout);
        return cts;
    }

    private static DateOnly? DatePrefix(string name)
        => name.Length >= 10 && DateOnly.TryParseExact(name[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static string Describe(Exception ex)
        => ex is OperationCanceledException ? "timed out" : Shorten(ex.Message);

    private static string Shorten(string text)
        => text.Length > 300 ? text[..300] + "…" : text;

    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9-]{1,39})/(?<repo>[A-Za-z0-9._-]{1,100})(?:@(?<ref>[A-Za-z0-9._/-]{1,200}))?:(?<path>[A-Za-z0-9._/ -]{0,400})$")]
    private static partial Regex GitHubLocation();
}
