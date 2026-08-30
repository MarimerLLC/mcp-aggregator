using McpAggregator.Core.Models;

namespace McpAggregator.Core.Tests.Models;

[TestClass]
public class TransportConfigTests
{
    [TestMethod]
    public void ToRedacted_MasksHeaderValues_KeepingKeys()
    {
        var config = new TransportConfig
        {
            Type = TransportType.Http,
            Url = "http://localhost:8080",
            ConnectionTimeout = TimeSpan.FromSeconds(45),
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer abc123",
                ["X-Tenant"] = "contoso"
            }
        };

        var redacted = config.ToRedacted();

        Assert.IsNotNull(redacted.Headers);
        CollectionAssert.AreEquivalent(
            new[] { "Authorization", "X-Tenant" },
            redacted.Headers.Keys.ToArray());
        Assert.AreEqual("***", redacted.Headers["Authorization"]);
        Assert.AreEqual("***", redacted.Headers["X-Tenant"]);
        Assert.AreEqual("http://localhost:8080", redacted.Url);
        Assert.AreEqual(TimeSpan.FromSeconds(45), redacted.ConnectionTimeout);
    }

    [TestMethod]
    public void ToRedacted_MasksEnvironmentValues()
    {
        var config = new TransportConfig
        {
            Type = TransportType.Stdio,
            Command = "node",
            Arguments = ["server.js"],
            Environment = new Dictionary<string, string> { ["API_KEY"] = "secret" }
        };

        var redacted = config.ToRedacted();

        Assert.IsNotNull(redacted.Environment);
        Assert.AreEqual("***", redacted.Environment["API_KEY"]);
        Assert.AreEqual("node", redacted.Command);
        CollectionAssert.AreEqual(new[] { "server.js" }, redacted.Arguments);
    }

    [TestMethod]
    public void ToRedacted_DoesNotMutateOriginal()
    {
        var config = new TransportConfig
        {
            Type = TransportType.Http,
            Url = "http://localhost:8080",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc123" }
        };

        _ = config.ToRedacted();

        Assert.AreEqual("Bearer abc123", config.Headers["Authorization"]);
    }

    [TestMethod]
    public void ToRedacted_LeavesNullCollectionsNull()
    {
        var config = new TransportConfig { Type = TransportType.Http, Url = "http://localhost:8080" };

        var redacted = config.ToRedacted();

        Assert.IsNull(redacted.Headers);
        Assert.IsNull(redacted.Environment);
    }
}
