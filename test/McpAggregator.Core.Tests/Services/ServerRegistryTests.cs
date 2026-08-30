using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tests.Helpers;
using Rocks;

namespace McpAggregator.Core.Tests.Services;

[TestClass]
public class ServerRegistryTests
{
    private static ServerRegistry CreateRegistry(IRegistryPersistence persistence)
    {
        return new ServerRegistry(
            persistence,
            TestHelpers.OptionsOf(new AggregatorOptions()),
            TestHelpers.NullLoggerOf<ServerRegistry>());
    }

    private static IRegistryPersistence EmptyPersistence()
    {
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData()));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);
        return expectations.Instance();
    }

    private static IRegistryPersistence PersistenceWith(params RegisteredServer[] servers)
    {
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData { Servers = [.. servers] }));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);
        return expectations.Instance();
    }

    // --- EnsureLoadedAsync ---

    [TestMethod]
    public async Task EnsureLoadedAsync_LoadsFromPersistence()
    {
        var server = TestHelpers.StdioServer();
        var persistence = PersistenceWith(server);
        var registry = CreateRegistry(persistence);

        await registry.EnsureLoadedAsync();

        var all = registry.GetAll();
        Assert.AreEqual(1, all.Count);
        Assert.AreEqual("test-server", all[0].Name);
    }

    [TestMethod]
    public async Task EnsureLoadedAsync_IsIdempotent()
    {
        var callCount = 0;
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .Callback(_ =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new RegistryData());
            });
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);
        var persistence = expectations.Instance();
        var registry = CreateRegistry(persistence);

        await registry.EnsureLoadedAsync();
        await registry.EnsureLoadedAsync();
        await registry.EnsureLoadedAsync();

        Assert.AreEqual(1, callCount);
    }

    [TestMethod]
    public async Task EnsureLoadedAsync_ThreadSafe_ConcurrentCalls()
    {
        var callCount = 0;
        var tcs = new TaskCompletionSource<RegistryData>();
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .Callback(_ =>
            {
                Interlocked.Increment(ref callCount);
                return tcs.Task;
            });
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);
        var persistence = expectations.Instance();
        var registry = CreateRegistry(persistence);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => registry.EnsureLoadedAsync()))
            .ToArray();

        tcs.SetResult(new RegistryData());
        await Task.WhenAll(tasks);

        Assert.AreEqual(1, callCount);
    }

    // --- RegisterAsync ---

    [TestMethod]
    public async Task RegisterAsync_StdioTransport_Succeeds()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = TestHelpers.StdioServer();
        await registry.RegisterAsync(server);

        Assert.IsTrue(registry.TryGet("test-server", out var found));
        Assert.AreEqual("test-server", found!.Name);
    }

    [TestMethod]
    public async Task RegisterAsync_HttpTransport_Succeeds()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = TestHelpers.HttpServer();
        await registry.RegisterAsync(server);

        Assert.IsTrue(registry.TryGet("http-server", out _));
    }

    [TestMethod]
    public async Task RegisterAsync_SetsRegisteredAt()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = TestHelpers.StdioServer();
        var before = DateTimeOffset.UtcNow;
        await registry.RegisterAsync(server);
        var after = DateTimeOffset.UtcNow;

        Assert.IsTrue(server.RegisteredAt >= before && server.RegisteredAt <= after);
    }

    [TestMethod]
    public async Task RegisterAsync_Duplicate_ThrowsServerAlreadyExists()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer());

        await Assert.ThrowsExceptionAsync<ServerAlreadyExistsException>(
            () => registry.RegisterAsync(TestHelpers.StdioServer()));
    }

    [TestMethod]
    public async Task RegisterAsync_StdioMissingCommand_ThrowsInvalidTransport()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = new RegisteredServer
        {
            Name = "bad",
            Transport = new TransportConfig { Type = TransportType.Stdio, Command = null }
        };

        await Assert.ThrowsExceptionAsync<InvalidTransportConfigException>(
            () => registry.RegisterAsync(server));
    }

    [TestMethod]
    public async Task RegisterAsync_HttpMissingUrl_ThrowsInvalidTransport()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = new RegisteredServer
        {
            Name = "bad",
            Transport = new TransportConfig { Type = TransportType.Http, Url = null }
        };

        await Assert.ThrowsExceptionAsync<InvalidTransportConfigException>(
            () => registry.RegisterAsync(server));
    }

    [TestMethod]
    public async Task RegisterAsync_HttpBadUrl_ThrowsInvalidTransport()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = new RegisteredServer
        {
            Name = "bad",
            Transport = new TransportConfig { Type = TransportType.Http, Url = "not-a-url" }
        };

        await Assert.ThrowsExceptionAsync<InvalidTransportConfigException>(
            () => registry.RegisterAsync(server));
    }

    [TestMethod]
    public async Task RegisterAsync_StdioWithHeaders_ThrowsInvalidTransport()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = new RegisteredServer
        {
            Name = "bad",
            Transport = new TransportConfig
            {
                Type = TransportType.Stdio,
                Command = "node",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer x" }
            }
        };

        await Assert.ThrowsExceptionAsync<InvalidTransportConfigException>(
            () => registry.RegisterAsync(server));
    }

    [TestMethod]
    public async Task RegisterAsync_StdioWithConnectionTimeout_ThrowsInvalidTransport()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = new RegisteredServer
        {
            Name = "bad",
            Transport = new TransportConfig
            {
                Type = TransportType.Stdio,
                Command = "node",
                ConnectionTimeout = TimeSpan.FromSeconds(30)
            }
        };

        await Assert.ThrowsExceptionAsync<InvalidTransportConfigException>(
            () => registry.RegisterAsync(server));
    }

    [TestMethod]
    public async Task RegisterAsync_HttpBlankHeaderName_ThrowsInvalidTransport()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = new RegisteredServer
        {
            Name = "bad",
            Transport = new TransportConfig
            {
                Type = TransportType.Http,
                Url = "http://localhost:8080",
                Headers = new Dictionary<string, string> { ["   "] = "value" }
            }
        };

        await Assert.ThrowsExceptionAsync<InvalidTransportConfigException>(
            () => registry.RegisterAsync(server));
    }

    [TestMethod]
    public async Task RegisterAsync_HttpNonPositiveConnectionTimeout_ThrowsInvalidTransport()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = new RegisteredServer
        {
            Name = "bad",
            Transport = new TransportConfig
            {
                Type = TransportType.Http,
                Url = "http://localhost:8080",
                ConnectionTimeout = TimeSpan.Zero
            }
        };

        await Assert.ThrowsExceptionAsync<InvalidTransportConfigException>(
            () => registry.RegisterAsync(server));
    }

    [TestMethod]
    public async Task RegisterAsync_HttpWithHeadersAndTimeout_Succeeds()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        var server = new RegisteredServer
        {
            Name = "good",
            Transport = new TransportConfig
            {
                Type = TransportType.Http,
                Url = "http://localhost:8080",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer ${TOKEN}" },
                ConnectionTimeout = TimeSpan.FromSeconds(45)
            }
        };

        await registry.RegisterAsync(server);

        var stored = registry.Get("good");
        Assert.AreEqual("Bearer ${TOKEN}", stored.Transport.Headers!["Authorization"]);
        Assert.AreEqual(TimeSpan.FromSeconds(45), stored.Transport.ConnectionTimeout);
    }

    // --- UpdateServerAsync ---

    [TestMethod]
    public async Task UpdateServerAsync_ReplacesTransport()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.HttpServer("api"));

        await registry.UpdateServerAsync("api", new TransportConfig
        {
            Type = TransportType.Http,
            Url = "https://api.example.com/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer rotated" }
        }, null, null);

        var server = registry.Get("api");
        Assert.AreEqual("https://api.example.com/mcp", server.Transport.Url);
        Assert.AreEqual("Bearer rotated", server.Transport.Headers!["Authorization"]);
    }

    [TestMethod]
    public async Task UpdateServerAsync_UpdatesMetadataOnly_WhenTransportNull()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.HttpServer("api"));

        await registry.UpdateServerAsync("api", null, "New Name", "New description");

        var server = registry.Get("api");
        Assert.AreEqual("New Name", server.DisplayName);
        Assert.AreEqual("New description", server.Description);
        Assert.AreEqual("http://localhost:8080", server.Transport.Url);
    }

    [TestMethod]
    public async Task UpdateServerAsync_PreservesSummarySkillAndRegistration()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.HttpServer("api"));
        await registry.UpdateSummaryAsync("api", "An AI summary");
        await registry.UpdateSkillFlagAsync("api", true);
        await registry.SetEnabledAsync("api", false);
        await registry.UpdateSkillSnapshotAsync("api", "1.0.0", "fp", DateTimeOffset.UtcNow);
        var registeredAt = registry.Get("api").RegisteredAt;
        var recordedAt = registry.Get("api").SkillRecordedAt;

        await registry.UpdateServerAsync("api", new TransportConfig
        {
            Type = TransportType.Http,
            Url = "https://api.example.com/mcp"
        }, null, null);

        var server = registry.Get("api");
        Assert.AreEqual("An AI summary", server.AiSummary);
        Assert.IsTrue(server.HasSkillDocument);
        Assert.IsFalse(server.Enabled);
        Assert.AreEqual(registeredAt, server.RegisteredAt);
        Assert.AreEqual("1.0.0", server.SkillRecordedVersion);
        Assert.AreEqual("fp", server.SkillRecordedFingerprint);
        Assert.AreEqual(recordedAt, server.SkillRecordedAt);
    }

    [TestMethod]
    public async Task UpdateServerAsync_InvalidTransport_Throws()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.HttpServer("api"));

        await Assert.ThrowsExceptionAsync<InvalidTransportConfigException>(
            () => registry.UpdateServerAsync("api", new TransportConfig
            {
                Type = TransportType.Http,
                Url = "not-a-url"
            }, null, null));
    }

    [TestMethod]
    public async Task UpdateServerAsync_UnknownServer_ThrowsServerNotFound()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        await Assert.ThrowsExceptionAsync<ServerNotFoundException>(
            () => registry.UpdateServerAsync("missing", null, "name", null));
    }

    [TestMethod]
    public async Task UpdateServerAsync_PersistsAndFiresRegistryChanged()
    {
        var saveCount = 0;
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData()));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .Callback((_, _) =>
            {
                Interlocked.Increment(ref saveCount);
                return Task.CompletedTask;
            });
        var registry = CreateRegistry(expectations.Instance());
        await registry.RegisterAsync(TestHelpers.HttpServer("api"));
        var fired = false;
        registry.RegistryChanged += () => fired = true;

        await registry.UpdateServerAsync("api", null, "New Name", null);

        Assert.AreEqual(2, saveCount);
        Assert.IsTrue(fired);
    }

    [TestMethod]
    public async Task RegisterAsync_FiresRegistryChanged()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        var fired = false;
        registry.RegistryChanged += () => fired = true;

        await registry.RegisterAsync(TestHelpers.StdioServer());

        Assert.IsTrue(fired);
    }

    [TestMethod]
    public async Task RegisterAsync_PersistsData()
    {
        RegistryData? saved = null;
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData()));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .Callback((data, _) =>
            {
                saved = data;
                return Task.CompletedTask;
            });
        var persistence = expectations.Instance();
        var registry = CreateRegistry(persistence);

        await registry.RegisterAsync(TestHelpers.StdioServer());

        Assert.IsNotNull(saved);
        Assert.AreEqual(1, saved!.Servers.Count);
        Assert.AreEqual("test-server", saved.Servers[0].Name);
    }

    // --- UnregisterAsync ---

    [TestMethod]
    public async Task UnregisterAsync_RemovesExistingServer()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer());

        await registry.UnregisterAsync("test-server");

        Assert.IsFalse(registry.TryGet("test-server", out _));
    }

    [TestMethod]
    public async Task UnregisterAsync_UnknownServer_ThrowsServerNotFound()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        await Assert.ThrowsExceptionAsync<ServerNotFoundException>(
            () => registry.UnregisterAsync("nonexistent"));
    }

    [TestMethod]
    public async Task UnregisterAsync_FiresRegistryChanged()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer());

        var fired = false;
        registry.RegistryChanged += () => fired = true;
        await registry.UnregisterAsync("test-server");

        Assert.IsTrue(fired);
    }

    // --- Get / TryGet ---

    [TestMethod]
    public async Task Get_ReturnsServerWhenExists()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer());

        var server = registry.Get("test-server");

        Assert.AreEqual("test-server", server.Name);
    }

    [TestMethod]
    public void Get_ThrowsServerNotFound_WhenMissing()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        Assert.ThrowsException<ServerNotFoundException>(() => registry.Get("missing"));
    }

    [TestMethod]
    public void TryGet_ReturnsFalse_WhenMissing()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        Assert.IsFalse(registry.TryGet("missing", out var server));
        Assert.IsNull(server);
    }

    [TestMethod]
    public async Task Get_IsCaseInsensitive()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer("MyServer"));

        var server = registry.Get("myserver");

        Assert.AreEqual("MyServer", server.Name);
    }

    // --- UpdateSkillFlagAsync / UpdateSummaryAsync ---

    [TestMethod]
    public async Task UpdateSkillFlagAsync_UpdatesAndPersists()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer());

        await registry.UpdateSkillFlagAsync("test-server", true);

        Assert.IsTrue(registry.Get("test-server").HasSkillDocument);
    }

    [TestMethod]
    public async Task UpdateSummaryAsync_UpdatesAndPersists()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer());

        await registry.UpdateSummaryAsync("test-server", "A great server");

        Assert.AreEqual("A great server", registry.Get("test-server").AiSummary);
    }

    [TestMethod]
    public async Task UpdateSkillSnapshotAsync_StoresAllFields()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer());
        var when = DateTimeOffset.UtcNow;

        await registry.UpdateSkillSnapshotAsync("test-server", "1.2.3", "abc123", when);

        var server = registry.Get("test-server");
        Assert.AreEqual("1.2.3", server.SkillRecordedVersion);
        Assert.AreEqual("abc123", server.SkillRecordedFingerprint);
        Assert.AreEqual(when, server.SkillRecordedAt);
    }

    [TestMethod]
    public async Task UpdateSkillSnapshotAsync_ClearsExistingSnapshot_WhenNullsPassed()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer());
        await registry.UpdateSkillSnapshotAsync("test-server", "1.0.0", "fp", DateTimeOffset.UtcNow);

        await registry.UpdateSkillSnapshotAsync("test-server", null, null, null);

        var server = registry.Get("test-server");
        Assert.IsNull(server.SkillRecordedVersion);
        Assert.IsNull(server.SkillRecordedFingerprint);
        Assert.IsNull(server.SkillRecordedAt);
    }

    [TestMethod]
    public void UpdateSkillSnapshotAsync_UnknownServer_ThrowsServerNotFound()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        Assert.ThrowsException<Exceptions.ServerNotFoundException>(
            () => registry.UpdateSkillSnapshotAsync("missing", "v", "fp", DateTimeOffset.UtcNow).GetAwaiter().GetResult());
    }

    // --- GetAll ---

    [TestMethod]
    public async Task GetAll_ReturnsAllRegistered()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);
        await registry.RegisterAsync(TestHelpers.StdioServer("a"));
        await registry.RegisterAsync(TestHelpers.HttpServer("b"));

        var all = registry.GetAll();

        Assert.AreEqual(2, all.Count);
    }

    [TestMethod]
    public void GetAll_ReturnsEmpty_WhenNoneRegistered()
    {
        var persistence = EmptyPersistence();
        var registry = CreateRegistry(persistence);

        Assert.AreEqual(0, registry.GetAll().Count);
    }
}
