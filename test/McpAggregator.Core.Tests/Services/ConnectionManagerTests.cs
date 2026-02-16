using McpAggregator.Core.Configuration;
using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Storage;
using McpAggregator.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Rocks;

namespace McpAggregator.Core.Tests.Services;

[TestClass]
public class ConnectionManagerTests
{
    private static (ConnectionManager Manager, ServerRegistry Registry) CreateManager(
        params RegisteredServer[] servers)
    {
        var expectations = new IRegistryPersistenceCreateExpectations();
        expectations.Setups.LoadAsync(Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new RegistryData { Servers = [.. servers] }));
        expectations.Setups.SaveAsync(Arg.Any<RegistryData>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);
        var persistence = expectations.Instance();

        var registry = new ServerRegistry(
            persistence,
            TestHelpers.OptionsOf(new AggregatorOptions()),
            TestHelpers.NullLoggerOf<ServerRegistry>());

        var manager = new ConnectionManager(
            registry,
            TestHelpers.OptionsOf(new AggregatorOptions()),
            NullLoggerFactory.Instance,
            TestHelpers.NullLoggerOf<ConnectionManager>());

        return (manager, registry);
    }

    [TestMethod]
    public void IsConnected_ReturnsFalse_ForUnknownServer()
    {
        var (manager, _) = CreateManager();

        Assert.IsFalse(manager.IsConnected("nonexistent"));
    }

    [TestMethod]
    public async Task DisconnectAsync_NoOp_ForUnknownServer()
    {
        var (manager, _) = CreateManager();

        // Should not throw
        await manager.DisconnectAsync("nonexistent");
    }

    [TestMethod]
    public async Task GetClientAsync_ThrowsServerUnavailable_ForDisabledServer()
    {
        var disabled = new RegisteredServer
        {
            Name = "disabled-srv",
            Enabled = false,
            Transport = new TransportConfig { Type = TransportType.Stdio, Command = "node" }
        };
        var (manager, registry) = CreateManager(disabled);
        await registry.EnsureLoadedAsync();

        await Assert.ThrowsExceptionAsync<ServerUnavailableException>(
            () => manager.GetClientAsync("disabled-srv"));
    }

    [TestMethod]
    public async Task GetClientAsync_ThrowsServerNotFound_ForMissingServer()
    {
        var (manager, registry) = CreateManager();
        await registry.EnsureLoadedAsync();

        await Assert.ThrowsExceptionAsync<ServerNotFoundException>(
            () => manager.GetClientAsync("nonexistent"));
    }
}
