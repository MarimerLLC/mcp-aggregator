using McpAggregator.Core.Configuration;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;
using McpAggregator.Core.Tests.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rocks;

namespace McpAggregator.Core.Tests.Services;

[TestClass]
public class SummaryGeneratorTests
{
    private static SummaryGenerator CreateGenerator(
        IChatClient? chatClient = null,
        bool enabled = true,
        TimeSpan? timeout = null)
    {
        var services = new ServiceCollection();
        if (chatClient is not null)
            services.AddSingleton(chatClient);

        var sp = services.BuildServiceProvider();
        var aiOptions = new AiOptions
        {
            Enabled = enabled,
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };

        return new SummaryGenerator(
            sp,
            TestHelpers.OptionsOf(aiOptions),
            TestHelpers.NullLoggerOf<SummaryGenerator>());
    }

    private static RegisteredServer SampleServer() => new()
    {
        Name = "test-srv",
        DisplayName = "Test Service",
        Description = "A test service",
        Transport = new TransportConfig { Type = TransportType.Stdio, Command = "node" }
    };

    private static IReadOnlyList<ToolSummary> SampleTools() =>
    [
        new() { Name = "tool_a", Description = "Does A" },
        new() { Name = "tool_b", Description = "Does B" }
    ];

    // --- IsAvailable ---

    [TestMethod]
    public void IsAvailable_True_WhenClientAndEnabled()
    {
        var expectations = new IChatClientCreateExpectations();
        var client = expectations.Instance();
        var gen = CreateGenerator(chatClient: client, enabled: true);

        Assert.IsTrue(gen.IsAvailable);
    }

    [TestMethod]
    public void IsAvailable_False_WhenNoClient()
    {
        var gen = CreateGenerator(chatClient: null, enabled: true);

        Assert.IsFalse(gen.IsAvailable);
    }

    [TestMethod]
    public void IsAvailable_False_WhenDisabled()
    {
        var expectations = new IChatClientCreateExpectations();
        var client = expectations.Instance();
        var gen = CreateGenerator(chatClient: client, enabled: false);

        Assert.IsFalse(gen.IsAvailable);
    }

    // --- GenerateSummaryAsync ---

    [TestMethod]
    public async Task GenerateSummaryAsync_ReturnsNull_WhenUnavailable()
    {
        var gen = CreateGenerator(chatClient: null, enabled: true);

        var result = await gen.GenerateSummaryAsync(SampleServer(), SampleTools());

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GenerateSummaryAsync_ReturnsTrimmedSummary()
    {
        var expectations = new IChatClientCreateExpectations();
        expectations.Setups.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.IsDefault<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "  A great summary.  "))));
        var client = expectations.Instance();
        var gen = CreateGenerator(chatClient: client, enabled: true);

        var result = await gen.GenerateSummaryAsync(SampleServer(), SampleTools());

        Assert.AreEqual("A great summary.", result);
    }

    [TestMethod]
    public async Task GenerateSummaryAsync_ReturnsNull_OnEmptyResponse()
    {
        var expectations = new IChatClientCreateExpectations();
        expectations.Setups.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.IsDefault<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "   "))));
        var client = expectations.Instance();
        var gen = CreateGenerator(chatClient: client, enabled: true);

        var result = await gen.GenerateSummaryAsync(SampleServer(), SampleTools());

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GenerateSummaryAsync_ReturnsNull_OnException()
    {
        var expectations = new IChatClientCreateExpectations();
        expectations.Setups.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.IsDefault<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Callback((_, _, _) => throw new HttpRequestException("Service unavailable"));
        var client = expectations.Instance();
        var gen = CreateGenerator(chatClient: client, enabled: true);

        var result = await gen.GenerateSummaryAsync(SampleServer(), SampleTools());

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GenerateSummaryAsync_SendsCorrectPromptContent()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        var expectations = new IChatClientCreateExpectations();
        expectations.Setups.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.IsDefault<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Callback((messages, _, _) =>
            {
                capturedMessages = messages;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary")));
            });
        var client = expectations.Instance();
        var gen = CreateGenerator(chatClient: client, enabled: true);

        await gen.GenerateSummaryAsync(SampleServer(), SampleTools());

        Assert.IsNotNull(capturedMessages);
        var messageList = capturedMessages!.ToList();
        Assert.AreEqual(2, messageList.Count);
        Assert.AreEqual(ChatRole.System, messageList[0].Role);
        Assert.AreEqual(ChatRole.User, messageList[1].Role);

        var userText = messageList[1].Text;
        Assert.IsNotNull(userText);
        Assert.IsTrue(userText!.Contains("test-srv"), "User prompt should contain server name");
        Assert.IsTrue(userText.Contains("tool_a"), "User prompt should contain tool names");
        Assert.IsTrue(userText.Contains("tool_b"), "User prompt should contain tool names");
    }
}
