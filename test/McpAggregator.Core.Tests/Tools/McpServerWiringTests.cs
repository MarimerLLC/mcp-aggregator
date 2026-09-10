using McpAggregator.Core.Configuration;
using McpAggregator.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// The SDK's stateless HTTP handler creates a fresh <see cref="McpServerOptions"/> per request via
/// <see cref="IOptionsFactory{TOptions}"/>, and its own options setup does
/// <c>ToolCollection ??= []</c> on each one. If the aggregator only post-configured the
/// <see cref="IOptions{TOptions}"/> singleton, every stateless request would list from a throwaway
/// collection and never see the typed wrappers (found in the manual HTTP check for issue #39).
/// </summary>
[TestClass]
public class McpServerWiringTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddAggregatorCore(configuration);
        services.AddAggregatorMcpServer()
            .WithToolsFromAssembly(typeof(ConsumerTools).Assembly);
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void EveryOptionsInstance_SharesOneToolCollection()
    {
        using var provider = BuildProvider();

        var singleton = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();
        var perRequest1 = factory.Create(Options.DefaultName);
        var perRequest2 = factory.Create(Options.DefaultName);

        Assert.AreNotSame(perRequest1, perRequest2, "The factory must hand out fresh options (that is the stateless path).");
        Assert.IsNotNull(singleton.ToolCollection);
        Assert.AreSame(singleton.ToolCollection, perRequest1.ToolCollection);
        Assert.AreSame(singleton.ToolCollection, perRequest2.ToolCollection);
    }

    [TestMethod]
    public void SharedToolCollection_ContainsTheAttributedTools_Once()
    {
        using var provider = BuildProvider();

        var collection = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();
        var before = collection.Count;

        // Repeated per-request configuration must not duplicate or lose tools.
        _ = factory.Create(Options.DefaultName);
        _ = factory.Create(Options.DefaultName);

        Assert.AreEqual(before, collection.Count);
        Assert.IsTrue(collection.TryGetPrimitive("find_tools", out _));
        Assert.IsTrue(collection.TryGetPrimitive("invoke_tool", out _));
        Assert.IsTrue(collection.TryGetPrimitive("list_services", out _));
        Assert.IsFalse(collection.TryGetPrimitive("register_server", out _), "Lazy (the default) keeps admin tools out of the shared list.");
    }

    [TestMethod]
    public void WrapperAddedToTheSharedCollection_IsVisibleToAFreshOptionsInstance()
    {
        using var provider = BuildProvider();

        var shared = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        var marker = McpServerTool.Create(() => "x", new McpServerToolCreateOptions { Name = "probe__marker" });
        shared.Add(marker);

        var perRequest = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create(Options.DefaultName);

        Assert.IsTrue(perRequest.ToolCollection!.TryGetPrimitive("probe__marker", out var seen));
        Assert.AreSame(marker, seen);
    }

    // ---------------------------------------------------------------- prompts (issue #40)

    [TestMethod]
    public void EveryOptionsInstance_SharesOnePromptCollection()
    {
        using var provider = BuildProvider();

        var singleton = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();
        var perRequest1 = factory.Create(Options.DefaultName);
        var perRequest2 = factory.Create(Options.DefaultName);

        Assert.IsNotNull(singleton.PromptCollection, "A non-null collection is what advertises the prompts capability.");
        Assert.AreSame(singleton.PromptCollection, perRequest1.PromptCollection);
        Assert.AreSame(singleton.PromptCollection, perRequest2.PromptCollection);
        Assert.AreEqual(0, singleton.PromptCollection.Count, "The aggregator has no prompts of its own.");
    }

    [TestMethod]
    public void PromptAddedToTheSharedCollection_IsVisibleToAFreshOptionsInstance()
    {
        using var provider = BuildProvider();

        var shared = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.PromptCollection!;
        var marker = McpServerPrompt.Create(() => "x", new McpServerPromptCreateOptions { Name = "probe__marker" });
        shared.Add(marker);

        var perRequest = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create(Options.DefaultName);

        Assert.IsTrue(perRequest.PromptCollection!.TryGetPrimitive("probe__marker", out var seen));
        Assert.AreSame(marker, seen);
        Assert.AreEqual(1, shared.Count, "Repeated per-request configuration must not duplicate prompts.");
    }
}
