using System.Text.Json;
using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tests.Helpers;

namespace McpAggregator.Core.Tests.Storage;

[TestClass]
public class JsonFilePersistenceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonFilePersistence CreatePersistence(string? fileName = null)
    {
        var options = new AggregatorOptions
        {
            DataDirectory = _tempDir,
            RegistryFile = fileName ?? "registry.json"
        };
        return new JsonFilePersistence(
            TestHelpers.OptionsOf(options),
            TestHelpers.NullLoggerOf<JsonFilePersistence>());
    }

    // --- LoadAsync ---

    [TestMethod]
    public async Task LoadAsync_ReturnsEmpty_WhenFileMissing()
    {
        var persistence = CreatePersistence();

        var data = await persistence.LoadAsync();

        Assert.IsNotNull(data);
        Assert.AreEqual(0, data.Servers.Count);
        Assert.AreEqual("1.0", data.Version);
    }

    [TestMethod]
    public async Task LoadAsync_DeserializesValidJson()
    {
        var filePath = Path.Combine(_tempDir, "registry.json");
        var json = """
            {
              "version": "1.0",
              "servers": [
                {
                  "name": "test-server",
                  "displayName": "Test",
                  "description": "Desc",
                  "transport": { "type": "Stdio", "command": "node" },
                  "enabled": true,
                  "hasSkillDocument": false
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(filePath, json);
        var persistence = CreatePersistence();

        var data = await persistence.LoadAsync();

        Assert.AreEqual(1, data.Servers.Count);
        Assert.AreEqual("test-server", data.Servers[0].Name);
        Assert.AreEqual("Test", data.Servers[0].DisplayName);
        Assert.AreEqual(TransportType.Stdio, data.Servers[0].Transport.Type);
        Assert.AreEqual("node", data.Servers[0].Transport.Command);
    }

    // --- SaveAsync ---

    [TestMethod]
    public async Task SaveAsync_CreatesDirectoryIfNeeded()
    {
        var subDir = Path.Combine(_tempDir, "subdir");
        var options = new AggregatorOptions
        {
            DataDirectory = subDir,
            RegistryFile = "registry.json"
        };
        var persistence = new JsonFilePersistence(
            TestHelpers.OptionsOf(options),
            TestHelpers.NullLoggerOf<JsonFilePersistence>());

        await persistence.SaveAsync(new RegistryData());

        Assert.IsTrue(File.Exists(Path.Combine(subDir, "registry.json")));
    }

    [TestMethod]
    public async Task SaveAsync_WritesIndentedCamelCaseJson()
    {
        var persistence = CreatePersistence();
        var data = new RegistryData
        {
            Servers =
            [
                new RegisteredServer
                {
                    Name = "my-server",
                    Transport = new TransportConfig { Type = TransportType.Http, Url = "http://localhost" }
                }
            ]
        };

        await persistence.SaveAsync(data);

        var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, "registry.json"));
        Assert.IsTrue(json.Contains("\"name\""), "Should use camelCase naming");
        Assert.IsTrue(json.Contains("\"transport\""), "Should use camelCase naming");
        Assert.IsTrue(json.Contains('\n'), "Should be indented");

        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("my-server", doc.RootElement.GetProperty("servers")[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task SaveAsync_AtomicWrite_NoTempFileRemains()
    {
        var persistence = CreatePersistence();

        await persistence.SaveAsync(new RegistryData());

        var tmpFile = Path.Combine(_tempDir, "registry.json.tmp");
        Assert.IsFalse(File.Exists(tmpFile), "Temp file should not remain after save");
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "registry.json")));
    }

    [TestMethod]
    public async Task SaveAsync_OverwritesExistingFile()
    {
        var persistence = CreatePersistence();

        await persistence.SaveAsync(new RegistryData { Servers = [TestHelpers.StdioServer("first")] });
        await persistence.SaveAsync(new RegistryData { Servers = [TestHelpers.StdioServer("second")] });

        var data = await persistence.LoadAsync();
        Assert.AreEqual(1, data.Servers.Count);
        Assert.AreEqual("second", data.Servers[0].Name);
    }

    // --- Concurrency ---

    [TestMethod]
    public async Task SaveAsync_ParallelCalls_DoNotCorrupt()
    {
        var persistence = CreatePersistence();

        var tasks = Enumerable.Range(0, 20).Select(i =>
        {
            var data = new RegistryData
            {
                Servers = [TestHelpers.StdioServer($"server-{i}")]
            };
            return persistence.SaveAsync(data);
        });

        await Task.WhenAll(tasks);

        // File should be valid JSON and loadable
        var result = await persistence.LoadAsync();
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Servers.Count);
    }

    // --- Round-trip ---

    [TestMethod]
    public async Task SaveAndLoad_RoundTrip()
    {
        var persistence = CreatePersistence();
        var original = new RegistryData
        {
            Servers =
            [
                new RegisteredServer
                {
                    Name = "srv",
                    DisplayName = "Server",
                    Description = "Description",
                    Transport = new TransportConfig
                    {
                        Type = TransportType.Stdio,
                        Command = "dotnet",
                        Arguments = ["run"]
                    },
                    Enabled = true,
                    HasSkillDocument = true,
                    AiSummary = "A summary"
                }
            ]
        };

        await persistence.SaveAsync(original);
        var loaded = await persistence.LoadAsync();

        Assert.AreEqual(1, loaded.Servers.Count);
        var s = loaded.Servers[0];
        Assert.AreEqual("srv", s.Name);
        Assert.AreEqual("Server", s.DisplayName);
        Assert.AreEqual("Description", s.Description);
        Assert.AreEqual(TransportType.Stdio, s.Transport.Type);
        Assert.AreEqual("dotnet", s.Transport.Command);
        Assert.IsTrue(s.HasSkillDocument);
        Assert.AreEqual("A summary", s.AiSummary);
    }
}
