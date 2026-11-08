using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Services;

/// <summary>
/// Skill documents and what travels with them, all under the skills directory:
/// <list type="bullet">
/// <item><c>{name}.md</c> — the document;</item>
/// <item><c>{name}.baseline.json</c> — the catalog it was written against (issue #56);</item>
/// <item><c>history/{name}/{version}.md</c> (+ <c>.baseline.json</c>) — every replaced version (issue #57);</item>
/// <item><c>drafts/{name}.json</c> — the pending <c>refresh_skill</c> proposal (issue #57);</item>
/// <item><c>{self}.shipped.sha256</c> — for the aggregator's own document only: the
/// <see cref="ShippedSelfSkill.Hash"/> of the shipped version the live document equals, kept by every
/// save and read by <see cref="SelfSkillSeeder"/> (issue #72).</item>
/// </list>
/// </summary>
public class SkillStore
{
    private readonly string _skillsDir;
    private readonly string _selfName;
    private readonly int _historyLimit;
    private readonly ILogger<SkillStore> _logger;
    private const int MaxSkillSizeBytes = 256 * 1024; // 256KB

    /// <summary>History version ids: UTC time of the save that replaced the version, sortable.</summary>
    private const string VersionFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    public SkillStore(IOptions<AggregatorOptions> options, ILogger<SkillStore> logger)
    {
        _skillsDir = options.Value.SkillsDirectoryPath;
        _selfName = options.Value.SelfName;
        _historyLimit = Math.Max(0, options.Value.SkillHistoryLimit);
        _logger = logger;
        Directory.CreateDirectory(_skillsDir);
    }

    public static int MaxSkillSize => MaxSkillSizeBytes;

    /// <summary>
    /// The aggregator's own document as this build ships it (<see cref="ShippedSelfSkill.Text"/>);
    /// settable so tests can stand in a different release.
    /// </summary>
    internal string? ShippedSelfDocument { get; set; } = ShippedSelfSkill.Text;

    public async Task<string?> GetAsync(string serverName, CancellationToken ct = default)
    {
        var path = GetPath(serverName);
        if (!File.Exists(path))
            return null;

        return await File.ReadAllTextAsync(path, ct);
    }

    /// <summary>
    /// Saves the document. The version it replaces (with its baseline, if any) is kept in history
    /// first, so every save — by hand, by <c>refresh_skill</c> or by <c>restore_skill</c> — can be
    /// rolled back. Saving identical text keeps no history entry.
    /// </summary>
    public Task SetAsync(string serverName, string markdown, CancellationToken ct = default)
        => SetCoreAsync(serverName, markdown, expectedSha256: null, whileLocked: null, ct);

    /// <summary>
    /// Saves <paramref name="markdown"/> only if the current document still hashes to
    /// <paramref name="expectedSha256"/> (<see cref="ContentHash"/>); returns false, changing nothing,
    /// when someone saved in between. Used to publish a <c>refresh_skill</c> draft without
    /// overwriting an edit made while the model was working. <paramref name="whileLocked"/> runs after
    /// the write and before the document's lock is released, so the baseline that goes with the new
    /// text is recorded before any other save or restore can replace the text.
    /// </summary>
    public Task<bool> SetIfUnchangedAsync(
        string serverName, string expectedSha256, string markdown, Func<Task>? whileLocked = null, CancellationToken ct = default)
        => SetCoreAsync(serverName, markdown, expectedSha256, whileLocked, ct);

    /// <summary>
    /// Writes <paramref name="markdown"/> only if there is no document yet; returns false, changing
    /// nothing, when one exists. The file is opened with <see cref="FileMode.CreateNew"/>, so the
    /// existence check and the creation are one step even for another process writing to the same
    /// directory.
    /// </summary>
    public async Task<bool> TryCreateAsync(string serverName, string markdown, CancellationToken ct = default)
    {
        if (markdown.Length > MaxSkillSizeBytes)
            throw new AggregatorException($"Skill document exceeds maximum size of {MaxSkillSizeBytes} bytes.");

        var path = GetPath(serverName);
        var gate = LockFor(path);
        await gate.WaitAsync(ct);
        try
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(markdown.AsMemory(), ct);
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }

            if (IsSelf(serverName))
                await RecordShippedMarkerAsync(markdown, ct);
        }
        finally
        {
            gate.Release();
        }

        _logger.LogInformation("Saved skill document for '{Server}'", serverName);
        return true;
    }

    /// <summary>
    /// Runs <paramref name="action"/> only if the current document still hashes to
    /// <paramref name="expectedSha256"/>, holding the document's lock for the check and the action, so no
    /// save or restore can change the text in between. Returns false, without running it, otherwise.
    /// Used to record a reviewed-and-unchanged document as current.
    /// </summary>
    public async Task<bool> RunIfUnchangedAsync(
        string serverName, string expectedSha256, Func<Task> action, CancellationToken ct = default)
    {
        var path = GetPath(serverName);
        var gate = LockFor(path);
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path) || ContentHash(await File.ReadAllTextAsync(path, ct)) != expectedSha256)
                return false;
            await action();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reading the previous text, archiving it and writing the replacement happen under one
    /// per-document lock, so two concurrent saves can't both archive the same predecessor and lose
    /// the version in between, and a conditional save compares against what it actually replaces.
    /// </summary>
    private async Task<bool> SetCoreAsync(
        string serverName, string markdown, string? expectedSha256, Func<Task>? whileLocked, CancellationToken ct)
    {
        if (markdown.Length > MaxSkillSizeBytes)
            throw new AggregatorException($"Skill document exceeds maximum size of {MaxSkillSizeBytes} bytes.");

        var path = GetPath(serverName);
        var gate = LockFor(path);
        await gate.WaitAsync(ct);
        try
        {
            var previous = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
            if (expectedSha256 is not null && (previous is null || ContentHash(previous) != expectedSha256))
                return false;

            if (previous is not null && !string.Equals(previous, markdown, StringComparison.Ordinal))
                await ArchiveAsync(serverName, previous, ct);

            await File.WriteAllTextAsync(path, markdown, ct);
            if (IsSelf(serverName))
                await RecordShippedMarkerAsync(markdown, ct);
            if (whileLocked is not null)
                await whileLocked();
        }
        finally
        {
            gate.Release();
        }

        _logger.LogInformation("Saved skill document for '{Server}'", serverName);
        return true;
    }

    /// <summary>SHA-256 of a document's text, lowercase hex: the identity a draft records for what it revises.</summary>
    public static string ContentHash(string text)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Deletes the document, its baseline, its history and any pending draft.</summary>
    public bool Delete(string serverName)
    {
        DeleteBaseline(serverName);
        DeleteDraft(serverName);
        if (IsSelf(serverName))
            DeleteFile(GetShippedMarkerPath());
        var historyDir = GetHistoryDir(serverName);
        if (Directory.Exists(historyDir))
            Directory.Delete(historyDir, recursive: true);

        var path = GetPath(serverName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _logger.LogInformation("Deleted skill document for '{Server}'", serverName);
        return true;
    }

    public bool Exists(string serverName) => File.Exists(GetPath(serverName));

    // ---------------------------------------------------------------- shipped self document (issue #72)

    /// <summary>
    /// The shipped version the aggregator's own document was last saved as, or null when it was last
    /// saved with anything else (a deliberate edit) or was never saved since the marker existed.
    /// </summary>
    public async Task<string?> GetShippedMarkerAsync(CancellationToken ct = default)
    {
        var path = GetShippedMarkerPath();
        return File.Exists(path) ? (await File.ReadAllTextAsync(path, ct)).Trim() : null;
    }

    /// <summary>
    /// Every save of the aggregator's own document keeps the marker honest: equal to the shipped
    /// version (line endings aside) → marker set, so a later release may replace it; anything else →
    /// marker removed, so a deliberate edit is never overwritten.
    /// </summary>
    private async Task RecordShippedMarkerAsync(string markdown, CancellationToken ct)
    {
        var hash = ShippedSelfSkill.Hash(markdown);
        if (ShippedSelfDocument is { } shipped && ShippedSelfSkill.Hash(shipped) == hash)
            await File.WriteAllTextAsync(GetShippedMarkerPath(), hash, ct);
        else
            DeleteFile(GetShippedMarkerPath());
    }

    private bool IsSelf(string serverName) => string.Equals(serverName, _selfName, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- baseline (issue #56)

    /// <summary>
    /// The catalog snapshot recorded with the skill (issue #56), or null when none was recorded
    /// (skills saved before #56, or a capture that could not read the server). An unreadable file is
    /// treated as absent and logged, so freshness falls back to the fingerprint check.
    /// </summary>
    public Task<SkillBaseline?> GetBaselineAsync(string serverName, CancellationToken ct = default)
        => ReadJsonAsync<SkillBaseline>(GetBaselinePath(serverName), serverName, "skill baseline", ct);

    /// <summary>
    /// Writes the baseline atomically (temp file + rename), like the registry. Writes to one
    /// baseline are serialized (on Windows two concurrent renames onto the same file fail), and each
    /// stages in its own temp file, so two saves of the same skill never touch each other's file;
    /// the last one wins.
    /// </summary>
    public Task SetBaselineAsync(string serverName, SkillBaseline baseline, CancellationToken ct = default)
        => WriteJsonAtomicAsync(GetBaselinePath(serverName), baseline, ct);

    public bool DeleteBaseline(string serverName) => DeleteFile(GetBaselinePath(serverName));

    // ---------------------------------------------------------------- history (issue #57)

    /// <summary>Replaced versions of the document, newest first.</summary>
    public async Task<List<SkillHistoryEntry>> ListHistoryAsync(string serverName, CancellationToken ct = default)
    {
        var dir = GetHistoryDir(serverName);
        if (!Directory.Exists(dir))
            return [];

        var entries = new List<SkillHistoryEntry>();
        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            var version = Path.GetFileNameWithoutExtension(file);
            var baseline = await ReadJsonAsync<SkillBaseline>(
                Path.Combine(dir, version + ".baseline.json"), serverName, "history baseline", ct);
            entries.Add(new SkillHistoryEntry
            {
                Version = version,
                ReplacedAt = ParseVersion(version),
                Bytes = new FileInfo(file).Length,
                HasBaseline = baseline is not null,
                RecordedVersion = baseline?.RemoteVersion,
                RecordedAt = baseline?.RecordedAt,
            });
        }

        return entries.OrderByDescending(e => e.Version, StringComparer.Ordinal).ToList();
    }

    /// <summary>One history version's document and baseline, or null when there is no such version.</summary>
    public async Task<(string Markdown, SkillBaseline? Baseline)?> GetHistoryAsync(
        string serverName, string version, CancellationToken ct = default)
    {
        // Version ids come from callers; never let one walk out of the history directory.
        if (version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(".."))
            return null;

        var dir = GetHistoryDir(serverName);
        var path = Path.Combine(dir, version + ".md");
        if (!File.Exists(path))
            return null;

        var markdown = await File.ReadAllTextAsync(path, ct);
        var baseline = await ReadJsonAsync<SkillBaseline>(
            Path.Combine(dir, version + ".baseline.json"), serverName, "history baseline", ct);
        return (markdown, baseline);
    }

    private async Task ArchiveAsync(string serverName, string previous, CancellationToken ct)
    {
        if (_historyLimit == 0)
            return;

        var dir = GetHistoryDir(serverName);
        Directory.CreateDirectory(dir);

        var version = DateTimeOffset.UtcNow.ToString(VersionFormat, CultureInfo.InvariantCulture);
        for (var n = 1; File.Exists(Path.Combine(dir, version + ".md")); n++)
            version = DateTimeOffset.UtcNow.ToString(VersionFormat, CultureInfo.InvariantCulture) + "-" + n;

        await File.WriteAllTextAsync(Path.Combine(dir, version + ".md"), previous, ct);
        var baselinePath = GetBaselinePath(serverName);
        if (File.Exists(baselinePath))
            File.Copy(baselinePath, Path.Combine(dir, version + ".baseline.json"), overwrite: true);

        foreach (var old in Directory.GetFiles(dir, "*.md")
                     .Select(Path.GetFileNameWithoutExtension)
                     .OrderByDescending(v => v, StringComparer.Ordinal)
                     .Skip(_historyLimit))
        {
            DeleteFile(Path.Combine(dir, old + ".md"));
            DeleteFile(Path.Combine(dir, old + ".baseline.json"));
        }
    }

    private static DateTimeOffset? ParseVersion(string version)
        => DateTimeOffset.TryParseExact(version.Split('-')[0], VersionFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    // ---------------------------------------------------------------- drafts (issue #57)

    public Task<SkillDraft?> GetDraftAsync(string serverName, CancellationToken ct = default)
        => ReadJsonAsync<SkillDraft>(GetDraftPath(serverName), serverName, "skill draft", ct);

    public Task SetDraftAsync(string serverName, SkillDraft draft, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GetDraftPath(serverName))!);
        return WriteJsonAtomicAsync(GetDraftPath(serverName), draft, ct);
    }

    public bool DeleteDraft(string serverName) => DeleteFile(GetDraftPath(serverName));

    /// <summary>
    /// Marks the stored draft discarded, but only if it is still the draft the caller read
    /// (same <see cref="SkillDraft.CreatedAt"/> and text). The check and the write happen under the
    /// draft's write lock, so a draft saved in between (by the upkeep or <c>refresh_skill</c>) is
    /// never overwritten with the old one.
    /// </summary>
    public async Task<DraftDiscardOutcome> DiscardDraftIfUnchangedAsync(
        string serverName, DateTimeOffset createdAt, string markdownSha256, CancellationToken ct = default)
    {
        var path = GetDraftPath(serverName);
        var gate = LockFor(path);
        await gate.WaitAsync(ct);
        try
        {
            var current = await ReadJsonAsync<SkillDraft>(path, serverName, "skill draft", ct);
            if (current is null || current.Discarded)
                return DraftDiscardOutcome.NoDraft;
            if (current.CreatedAt != createdAt || ContentHash(current.Markdown) != markdownSha256)
                return DraftDiscardOutcome.Replaced;

            current.Discarded = true;
            await WriteJsonUnlockedAsync(path, current, ct);
            return DraftDiscardOutcome.Discarded;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Servers with a stored draft, by name.</summary>
    public List<string> ListDraftServers()
    {
        var dir = Path.Combine(_skillsDir, "drafts");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<T?> ReadJsonAsync<T>(string path, string serverName, string what, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable {What} for '{Server}'", what, serverName);
            return null;
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        var gate = LockFor(path);
        await gate.WaitAsync(ct);
        try
        {
            await WriteJsonUnlockedAsync(path, value, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Temp file + rename; the caller holds <see cref="LockFor"/> for <paramref name="path"/>.</summary>
    private static async Task WriteJsonUnlockedAsync<T>(string path, T value, CancellationToken ct)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static bool DeleteFile(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private static SemaphoreSlim LockFor(string path)
        => WriteLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

    /// <summary>Keyed by full path so every <see cref="SkillStore"/> on the same directory shares them.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private string GetPath(string serverName) =>
        Path.Combine(_skillsDir, $"{serverName}.md");

    private string GetBaselinePath(string serverName) =>
        Path.Combine(_skillsDir, $"{serverName}.baseline.json");

    private string GetHistoryDir(string serverName) =>
        Path.Combine(_skillsDir, "history", serverName);

    private string GetDraftPath(string serverName) =>
        Path.Combine(_skillsDir, "drafts", $"{serverName}.json");

    private string GetShippedMarkerPath() =>
        Path.Combine(_skillsDir, $"{_selfName}.shipped.sha256");
}
