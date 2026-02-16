using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace McpAggregator.Core.Tests.Helpers;

internal static class TestHelpers
{
    public static IOptions<T> OptionsOf<T>(T value) where T : class
        => Options.Create(value);

    public static ILogger<T> NullLoggerOf<T>()
        => NullLogger<T>.Instance;

    public static AggregatorOptions DefaultAggregatorOptions(string dataDir) => new()
    {
        DataDirectory = dataDir,
        RegistryFile = "registry.json",
        SkillsDirectory = "skills"
    };

    public static RegisteredServer StdioServer(string name = "test-server") => new()
    {
        Name = name,
        DisplayName = "Test Server",
        Description = "A test server",
        Transport = new TransportConfig
        {
            Type = TransportType.Stdio,
            Command = "node",
            Arguments = ["server.js"]
        }
    };

    public static RegisteredServer HttpServer(string name = "http-server") => new()
    {
        Name = name,
        DisplayName = "HTTP Server",
        Description = "An HTTP server",
        Transport = new TransportConfig
        {
            Type = TransportType.Http,
            Url = "http://localhost:8080"
        }
    };
}
