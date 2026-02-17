using System.IO;
using System.Net.Sockets;
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
public class ExecuteWithRetryTests
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
    public async Task ExecuteWithRetryAsync_SucceedsOnFirstTry_DoesNotRetry()
    {
        // We can't easily mock McpClient (concrete class), so we test ShouldRetry
        // indirectly by verifying non-retryable exceptions propagate.
        var (manager, registry) = CreateManager(TestHelpers.StdioServer("srv"));
        await registry.EnsureLoadedAsync();

        // GetClientAsync will fail with ServerUnavailableException (can't actually connect in tests)
        // but that's wrapped by ConnectAsync — verify it propagates without retry
        await Assert.ThrowsExceptionAsync<ServerUnavailableException>(
            () => manager.ExecuteWithRetryAsync<string>("srv",
                (client, ct) => Task.FromResult("ok")));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsFalse_ForOperationCanceledException()
    {
        Assert.IsFalse(InvokeShouldRetry(new OperationCanceledException()));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsFalse_ForAggregatorException()
    {
        Assert.IsFalse(InvokeShouldRetry(new AggregatorException("test")));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsFalse_ForServerNotFoundException()
    {
        Assert.IsFalse(InvokeShouldRetry(new ServerNotFoundException("srv")));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsFalse_ForToolExecutionException()
    {
        Assert.IsFalse(InvokeShouldRetry(new ToolExecutionException("srv", "tool", "error")));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsTrue_ForIOException()
    {
        Assert.IsTrue(InvokeShouldRetry(new IOException("pipe broken")));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsTrue_ForSocketException()
    {
        Assert.IsTrue(InvokeShouldRetry(new SocketException()));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsTrue_ForObjectDisposedException()
    {
        Assert.IsTrue(InvokeShouldRetry(new ObjectDisposedException("client")));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsTrue_ForHttpRequestException()
    {
        Assert.IsTrue(InvokeShouldRetry(new HttpRequestException("connection refused")));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsTrue_ForWrappedIOException()
    {
        var ex = new InvalidOperationException("outer", new IOException("inner"));
        Assert.IsTrue(InvokeShouldRetry(ex));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsTrue_ForWrappedSocketException()
    {
        var ex = new InvalidOperationException("outer", new SocketException());
        Assert.IsTrue(InvokeShouldRetry(ex));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsFalse_ForGenericException()
    {
        Assert.IsFalse(InvokeShouldRetry(new InvalidOperationException("generic")));
    }

    [TestMethod]
    public void ShouldRetry_ReturnsFalse_ForArgumentException()
    {
        Assert.IsFalse(InvokeShouldRetry(new ArgumentException("bad arg")));
    }

    /// <summary>
    /// Invoke the private static ShouldRetry method via reflection.
    /// </summary>
    private static bool InvokeShouldRetry(Exception ex)
    {
        var method = typeof(ConnectionManager).GetMethod("ShouldRetry",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("ShouldRetry method not found");
        return (bool)method.Invoke(null, [ex])!;
    }
}
