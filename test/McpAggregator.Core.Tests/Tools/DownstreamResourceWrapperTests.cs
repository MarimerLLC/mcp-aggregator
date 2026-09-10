using SysDescription = System.ComponentModel.DescriptionAttribute;
using System.Diagnostics.Metrics;
using System.Text;
using McpAggregator.Core;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tests.Helpers;
using McpAggregator.Core.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// The bridged resource (issue #45). Each test hosts a real <see cref="DownstreamResourceWrapper"/>
/// in an in-process MCP server and reads it through a real client, so the whole path — JSON-RPC
/// in, URI strip, <see cref="ToolProxyHandler.ReadResourceAsync"/>, downstream round-trip, URI
/// rewrite, JSON-RPC out — is exercised.
/// </summary>
[TestClass]
public class DownstreamResourceWrapperTests
{
    private const string Downstream = "probe";

    private string _dataDir = null!;
    private static int _readmeReads;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        Interlocked.Exchange(ref _readmeReads, 0);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- downstream doubles

    [SysDescription("The project readme.")]
    private static string Readme()
    {
        Interlocked.Increment(ref _readmeReads);
        return "# Readme";
    }

    [SysDescription("One documentation page by name.")]
    private static string Doc([SysDescription("Page name")] string name)
        => name == "missing" ? throw new FileNotFoundException("no such page: " + name) : "doc:" + name;

    [SysDescription("A small binary.")]
    private static BlobResourceContents Logo()
        => new() { Uri = "file:///logo.png", MimeType = "image/png", Blob = Encoding.ASCII.GetBytes("PNG") };

    internal static McpServerResource ReadmeResource()
        => McpServerResource.Create(Readme, new McpServerResourceCreateOptions
        {
            UriTemplate = "file:///readme.md",
            Name = "readme",
            Title = "Project readme",
            MimeType = "text/markdown"
        });

    internal static McpServerResource DocTemplate()
        => McpServerResource.Create(Doc, new McpServerResourceCreateOptions { UriTemplate = "file:///docs/{name}", Name = "doc", MimeType = "text/plain" });

    internal static McpServerResource LogoResource()
        => McpServerResource.Create(Logo, new McpServerResourceCreateOptions { UriTemplate = "file:///logo.png", Name = "logo", MimeType = "image/png" });

    private static McpServerTool EchoTool()
        => McpServerTool.Create(([SysDescription("Message")] string message) => message, new McpServerToolCreateOptions { Name = "echo" });

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private sealed record Rig(
        WrapperHarness Harness,
        InMemoryMcpServer Aggregator,
        McpClient Client,
        IReadOnlyList<DownstreamResourceWrapper> Wrappers) : IAsyncDisposable
    {
        public DownstreamResourceWrapper Wrapper(string downstreamUri) => Wrappers.Single(w => w.DownstreamUri == downstreamUri);

        public async ValueTask DisposeAsync()
        {
            await Aggregator.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    /// <summary>Builds the wrappers for one downstream's resources and hosts them in their own MCP server.</summary>
    private async Task<Rig> HostWrappersAsync(params McpServerResource[] downstreamResources)
    {
        var harness = await WrapperHarness.CreateWithResourcesAsync(_dataDir, WrapperToolMode.Eager,
            (Downstream, [EchoTool()], null, downstreamResources));
        var wrappers = await harness.Catalog.GetResourceWrappersAsync(Downstream, TestTimeout);

        var aggregator = new InMemoryMcpServer("aggregator", wrappers.Cast<McpServerResource>().ToArray());
        var client = await aggregator.CreateClientAsync(TestTimeout);
        return new Rig(harness, aggregator, client, wrappers);
    }

    private static MeterListener CaptureResourceReads(List<Dictionary<string, object?>> captured)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AggregatorTelemetry.ServiceName && instrument.Name == "mcp_resource_reads_total")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value;
            lock (captured) captured.Add(dict);
        });
        listener.Start();
        return listener;
    }

    // ---------------------------------------------------------------- fidelity

    [TestMethod]
    public async Task Wrapper_RewritesTheUri_AndCarriesMetadataThrough()
    {
        await using var rig = await HostWrappersAsync(ReadmeResource(), DocTemplate());

        var readme = rig.Wrapper("file:///readme.md");
        Assert.AreEqual("mcp-aggregator://probe/file:///readme.md", readme.Uri);
        Assert.IsFalse(readme.IsTemplate);
        Assert.IsFalse(readme.IsTemplated);
        Assert.AreEqual("readme", readme.ProtocolResourceTemplate.Name);
        Assert.AreEqual("Project readme", readme.ProtocolResourceTemplate.Title);
        Assert.AreEqual("text/markdown", readme.ProtocolResourceTemplate.MimeType);
        StringAssert.StartsWith(readme.ProtocolResourceTemplate.Description, "[probe]");
        Assert.IsNotNull(readme.ProtocolResource, "A plain resource must be listed by resources/list.");
        Assert.AreEqual(readme.Uri, readme.ProtocolResource.Uri);

        var doc = rig.Wrapper("file:///docs/{name}");
        Assert.AreEqual("mcp-aggregator://probe/file:///docs/{name}", doc.Uri);
        Assert.IsTrue(doc.IsTemplate);
        Assert.IsTrue(doc.IsTemplated, "The SDK must see the template as templated so it lands in resources/templates/list.");
        Assert.IsNull(doc.ProtocolResource);
    }

    [TestMethod]
    public void Wrapper_KeepsSizeOnThePlainResource()
    {
        var server = TestHelpers.StdioServer(Downstream);
        var downstream = new Resource { Uri = "file:///big.bin", Name = "big", Size = 12345 };

        var wrapper = new DownstreamResourceWrapper(server, downstream, proxy: null!, logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.AreEqual(12345, wrapper.ProtocolResource!.Size);
    }

    [TestMethod]
    public async Task Wrapper_MetaIdentifiesTheDownstream()
    {
        await using var rig = await HostWrappersAsync(ReadmeResource());

        var meta = rig.Wrapper("file:///readme.md").ProtocolResourceTemplate.Meta!["mcpAggregator"]!;
        Assert.AreEqual(Downstream, (string?)meta["serverName"]);
        Assert.AreEqual("file:///readme.md", (string?)meta["uri"]);
        Assert.AreEqual(rig.Harness.Registry.Get(Downstream).Id, (string?)meta["serverId"]);
    }

    [TestMethod]
    public void Fingerprint_ChangesWhenTheResourceChanges_AndIsStableOtherwise()
    {
        var a = new Resource { Uri = "file:///a", Name = "a", MimeType = "text/plain" };
        var same = new Resource { Uri = "file:///a", Name = "a", MimeType = "text/plain" };
        var different = new Resource { Uri = "file:///a", Name = "a", MimeType = "text/html" };

        Assert.AreEqual(DownstreamResourceWrapper.ComputeFingerprint(a), DownstreamResourceWrapper.ComputeFingerprint(same));
        Assert.AreNotEqual(DownstreamResourceWrapper.ComputeFingerprint(a), DownstreamResourceWrapper.ComputeFingerprint(different));
    }

    [TestMethod]
    public async Task IsMatch_AcceptsTheExactUri_AndTemplateExpansions_ForThisServerOnly()
    {
        await using var rig = await HostWrappersAsync(ReadmeResource(), DocTemplate());

        var readme = rig.Wrapper("file:///readme.md");
        Assert.IsTrue(readme.IsMatch("mcp-aggregator://probe/file:///readme.md"));
        Assert.IsFalse(readme.IsMatch("mcp-aggregator://probe/file:///readme.md.bak"));
        Assert.IsFalse(readme.IsMatch("mcp-aggregator://other/file:///readme.md"));
        Assert.IsFalse(readme.IsMatch("file:///readme.md"));

        var doc = rig.Wrapper("file:///docs/{name}");
        Assert.IsTrue(doc.IsMatch("mcp-aggregator://probe/file:///docs/intro.md"));
        Assert.IsFalse(doc.IsMatch("mcp-aggregator://probe/file:///docs/a/b.md"));
        Assert.IsFalse(doc.IsMatch("mcp-aggregator://probe/file:///readme.md"));
    }

    // ---------------------------------------------------------------- round-trips

    [TestMethod]
    public async Task Read_ReturnsTheDownstreamText_WithTheUriRewritten()
    {
        await using var rig = await HostWrappersAsync(ReadmeResource());

        var listed = await rig.Client.ListResourcesAsync(cancellationToken: TestTimeout);
        Assert.AreEqual("mcp-aggregator://probe/file:///readme.md", listed.Single().Uri);

        var result = await rig.Client.ReadResourceAsync("mcp-aggregator://probe/file:///readme.md", cancellationToken: TestTimeout);

        var text = result.Contents.OfType<TextResourceContents>().Single();
        Assert.AreEqual("# Readme", text.Text);
        Assert.AreEqual("mcp-aggregator://probe/file:///readme.md", text.Uri, "Content URIs must come back in aggregator form.");
        Assert.AreEqual(1, _readmeReads);
    }

    [TestMethod]
    public async Task Read_ExpandsATemplate_AndForwardsTheExpandedUri()
    {
        await using var rig = await HostWrappersAsync(DocTemplate());

        var templates = await rig.Client.ListResourceTemplatesAsync(cancellationToken: TestTimeout);
        Assert.AreEqual("mcp-aggregator://probe/file:///docs/{name}", templates.Single().UriTemplate);

        var result = await rig.Client.ReadResourceAsync("mcp-aggregator://probe/file:///docs/intro", cancellationToken: TestTimeout);

        var text = result.Contents.OfType<TextResourceContents>().Single();
        Assert.AreEqual("doc:intro", text.Text);
        Assert.AreEqual("mcp-aggregator://probe/file:///docs/intro", text.Uri);
    }

    [TestMethod]
    public async Task Read_ReturnsBlobContents_WithTheUriRewritten()
    {
        await using var rig = await HostWrappersAsync(LogoResource());

        var result = await rig.Client.ReadResourceAsync("mcp-aggregator://probe/file:///logo.png", cancellationToken: TestTimeout);

        var blob = result.Contents.OfType<BlobResourceContents>().Single();
        Assert.AreEqual("PNG", Encoding.ASCII.GetString(blob.Blob.ToArray()));
        Assert.AreEqual("image/png", blob.MimeType);
        Assert.AreEqual("mcp-aggregator://probe/file:///logo.png", blob.Uri);
    }

    [TestMethod]
    public async Task Read_RecordsTheWrapperPath_InTelemetry()
    {
        var captured = new List<Dictionary<string, object?>>();
        using var listener = CaptureResourceReads(captured);
        await using var rig = await HostWrappersAsync(ReadmeResource());

        await rig.Client.ReadResourceAsync("mcp-aggregator://probe/file:///readme.md", cancellationToken: TestTimeout);

        Dictionary<string, object?>? last;
        lock (captured)
            last = captured.LastOrDefault(t => Equals(t.GetValueOrDefault("uri"), "file:///readme.md") && Equals(t.GetValueOrDefault("server_name"), Downstream));
        Assert.IsNotNull(last);
        Assert.AreEqual(InvocationPath.Wrapper, last["via"]);
        Assert.AreEqual("success", last["result"]);
    }

    // ---------------------------------------------------------------- errors

    [TestMethod]
    public async Task UnreachableServer_SurfacesTheUnavailableMessage()
    {
        await using var rig = await HostWrappersAsync(ReadmeResource());

        await rig.Harness.Connections.DisconnectAsync(Downstream);
        rig.Harness.DownstreamTools.Remove(Downstream);

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await rig.Client.ReadResourceAsync("mcp-aggregator://probe/file:///readme.md", cancellationToken: TestTimeout));

        StringAssert.Contains(ex.Message, "unavailable");
    }

    [TestMethod]
    public async Task TemplateExpansionTheDownstreamRejects_SurfacesTheDownstreamError()
    {
        // The URI matches the template, so it is a known resource and the downstream's own fault
        // propagates rather than being replaced by an unknown-resource hint.
        await using var rig = await HostWrappersAsync(DocTemplate());

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await rig.Client.ReadResourceAsync("mcp-aggregator://probe/file:///docs/missing", cancellationToken: TestTimeout));

        Assert.IsFalse(ex.Message.Contains("Unknown resource"), ex.Message);
    }

    [TestMethod]
    public async Task ServerWithoutResourceSupport_YieldsNoWrappers()
    {
        await using var harness = await WrapperHarness.CreateWithResourcesAsync(_dataDir, WrapperToolMode.Eager,
            ("toolsonly", [EchoTool()], null, null));

        var wrappers = await harness.Catalog.GetResourceWrappersAsync("toolsonly", TestTimeout);

        Assert.AreEqual(0, wrappers.Count);
    }
}
