using SysDescription = System.ComponentModel.DescriptionAttribute;
using System.Net;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rocks;

namespace McpAggregator.Core.Tests.Services;

/// <summary>
/// A tools-only downstream served over Streamable HTTP answers an optional method it does not
/// implement (<c>prompts/list</c>, <c>resources/list</c>) with HTTP 404 and the JSON-RPC -32601
/// error as the response body. The SDK client surfaces that as an <see cref="HttpRequestException"/>,
/// not an <see cref="McpProtocolException"/>, so the aggregator used to read it as a broken
/// connection: it logged a warning, reconnected, failed again, never cached the empty result, and
/// repeated all of that on every discovery call. It also made <c>update_skill</c> report the server
/// as unreachable (issue #53). Observed live against todo-mcp and csla on aggregator 1.0.4.
/// </summary>
[TestClass]
public class HttpOptionalMethodTests
{
    private const string DownstreamName = "http-tools-only";

    private string _dataDir = null!;
    private WebApplication? _downstream;

    [TestInitialize]
    public void Initialize()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mcpagg-http-optional-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_downstream is not null)
            await _downstream.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [McpServerToolType]
    private sealed class EchoTools
    {
        [McpServerTool(Name = "echo"), SysDescription("Echoes a message back.")]
        public static string Echo([SysDescription("Message to echo")] string message) => message;
    }

    private sealed record Harness(
        ServerRegistry Registry, ToolIndex Index, ConnectionManager Connections, CountingLoggerProvider Logs)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connections.DisposeAsync();
    }

    /// <summary>
    /// Starts a real Streamable HTTP MCP server (SDK defaults, tools only) on a loopback port and
    /// registers it with a fresh aggregator over the production HTTP transport path.
    /// </summary>
    private async Task<Harness> CreateHarnessAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<EchoTools>();
        _downstream = builder.Build();
        _downstream.MapMcp();
        await _downstream.StartAsync();
        var address = _downstream.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var server = TestHelpers.HttpServer(DownstreamName);
        server.Transport.Url = address;

        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData { Servers = [server] }));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var options = TestHelpers.OptionsOf(TestHelpers.DefaultAggregatorOptions(_dataDir));
        var registry = new ServerRegistry(expectations.Instance(), options, TestHelpers.NullLoggerOf<ServerRegistry>());
        await registry.EnsureLoadedAsync();

        var logs = new CountingLoggerProvider();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Warning));
        var connections = new ConnectionManager(registry, options, NullLoggerFactory.Instance,
            loggerFactory.CreateLogger<ConnectionManager>());
        var skillStore = new SkillStore(options, TestHelpers.NullLoggerOf<SkillStore>());
        var index = new ToolIndex(registry, connections, skillStore, options, TestHelpers.NullLoggerOf<ToolIndex>());
        return new Harness(registry, index, connections, logs);
    }

    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [TestMethod]
    public async Task GetPromptsForServerAsync_HttpServerWithoutPrompts_ReturnsCachedEmptyWithoutReconnecting()
    {
        await using var harness = await CreateHarnessAsync();

        var first = await harness.Index.GetPromptsForServerAsync(DownstreamName, TestTimeout);
        var second = await harness.Index.GetPromptsForServerAsync(DownstreamName, TestTimeout);

        Assert.AreEqual(0, first.Count, "A server without prompt support must index as zero prompts.");
        Assert.AreSame(first, second, "The empty result must be cached, not re-probed.");
        Assert.AreEqual(0, harness.Logs.WarningsOrWorse,
            "An unimplemented optional method is not a broken connection: " + string.Join(" | ", harness.Logs.Messages));
    }

    [TestMethod]
    public async Task GetResourcesForServerAsync_HttpServerWithoutResources_ReturnsCachedEmptyWithoutReconnecting()
    {
        await using var harness = await CreateHarnessAsync();

        var first = await harness.Index.GetResourcesForServerAsync(DownstreamName, TestTimeout);
        var second = await harness.Index.GetResourcesForServerAsync(DownstreamName, TestTimeout);

        Assert.AreEqual(0, first.Count);
        Assert.AreSame(first, second, "The empty result must be cached, not re-probed.");
        Assert.AreEqual(0, harness.Logs.WarningsOrWorse,
            "An unimplemented optional method is not a broken connection: " + string.Join(" | ", harness.Logs.Messages));
    }

    [TestMethod]
    public async Task GetToolsForServerAsync_HttpServerWithoutPrompts_StillListsTools()
    {
        await using var harness = await CreateHarnessAsync();

        var tools = await harness.Index.GetToolsForServerAsync(DownstreamName, TestTimeout);

        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("echo", tools[0].Name);
    }

    [TestMethod]
    public async Task SkillSnapshot_HttpServerWithoutPrompts_RecordsBaseline()
    {
        // Issue #53: update_skill said "the server could not be reached" for todo-mcp because the
        // prompts/list 404 failed the snapshot, so freshness stayed 'unknown' forever.
        await using var harness = await CreateHarnessAsync();

        var recorded = await SkillSnapshot.CaptureAsync(harness.Registry, harness.Index, DownstreamName, TestTimeout);

        Assert.IsTrue(recorded, "A reachable tools-only server must get a freshness baseline.");
        Assert.IsNotNull(harness.Registry.Get(DownstreamName).SkillRecordedFingerprint);
    }

    // ---------------------------------------------------------------- classification

    [TestMethod]
    public void IsUnsupportedCapability_TrueFor404CarryingMethodNotFound()
    {
        var ex = new HttpRequestException(
            "Response status code does not indicate success: 404 (Not Found). Response body: event: message\n" +
            "data: {\"error\":{\"code\":-32601,\"message\":\"Method 'prompts/list' is not available.\"},\"id\":3,\"jsonrpc\":\"2.0\"}",
            inner: null,
            statusCode: HttpStatusCode.NotFound);

        Assert.IsTrue(ConnectionManager.IsUnsupportedCapability(ex));
    }

    [TestMethod]
    public void IsUnsupportedCapability_FalseForPlain404()
    {
        // A 404 without a JSON-RPC -32601 body is a wrong URL or an expired session, which is a
        // connection problem and must still be retried.
        var ex = new HttpRequestException("Response status code does not indicate success: 404 (Not Found).",
            inner: null, statusCode: HttpStatusCode.NotFound);

        Assert.IsFalse(ConnectionManager.IsUnsupportedCapability(ex));
    }

    [TestMethod]
    public void IsUnsupportedCapability_FalseForOtherStatusCarryingMethodNotFound()
    {
        var ex = new HttpRequestException("Response body: {\"error\":{\"code\":-32601}}",
            inner: null, statusCode: HttpStatusCode.InternalServerError);

        Assert.IsFalse(ConnectionManager.IsUnsupportedCapability(ex));
    }

    // ---------------------------------------------------------------- log capture

    private sealed class CountingLoggerProvider : ILoggerProvider
    {
        private int _warnings;
        public int WarningsOrWorse => _warnings;
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CountingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning)
                    return;
                Interlocked.Increment(ref owner._warnings);
                lock (owner.Messages)
                    owner.Messages.Add(formatter(state, exception));
            }
        }
    }
}
