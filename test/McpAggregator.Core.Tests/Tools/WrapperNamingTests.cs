using McpAggregator.Core.Tools;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// Wrapper names are the contract every consumer stores (issue #39, Q2/Q3/Q9): they must be
/// readable, unique across servers, and made only of characters hosts accept.
/// </summary>
[TestClass]
public class WrapperNamingTests
{
    [TestMethod]
    public void For_JoinsServerAndToolWithDoubleUnderscore()
    {
        Assert.AreEqual("microsoft-learn__microsoft_docs_search",
            WrapperNaming.For("microsoft-learn", "microsoft_docs_search"));
    }

    [TestMethod]
    public void For_TwoServersWithIdenticalToolNames_ProduceDistinctWrappers()
    {
        // The two OneDrive servers from the issue: prefixing is load-bearing for correctness.
        var marimer = WrapperNaming.For("onedrive-marimer", "list_files");
        var personal = WrapperNaming.For("onedrive-personal", "list_files");

        Assert.AreNotEqual(marimer, personal);
        Assert.AreEqual("onedrive-marimer__list_files", marimer);
        Assert.AreEqual("onedrive-personal__list_files", personal);
    }

    [TestMethod]
    public void For_ReplacesCharactersHostsReject()
    {
        // A downstream tool name with a space or slash would be rejected by the SDK's own name
        // check; sanitize rather than fail so the wrapper still exists.
        Assert.AreEqual("svc__get-user-by-id", WrapperNaming.For("svc", "get user/by:id"));
    }

    [TestMethod]
    public void For_KeepsAllowedPunctuation()
    {
        Assert.AreEqual("my.svc-1__tool_name.v2", WrapperNaming.For("my.svc-1", "tool_name.v2"));
    }

    [TestMethod]
    public void TryParse_RoundTrips()
    {
        var name = WrapperNaming.For("adjutant", "send_email");

        Assert.IsTrue(WrapperNaming.TryParse(name, out var server, out var tool));
        Assert.AreEqual("adjutant", server);
        Assert.AreEqual("send_email", tool);
    }

    [TestMethod]
    public void TryParse_SplitsOnFirstSeparatorOnly()
    {
        // Server names cannot contain "__" (ServerRegistry rejects them), so the first separator
        // is always the boundary and any later "__" belongs to the downstream tool name.
        Assert.IsTrue(WrapperNaming.TryParse("svc__weird__tool", out var server, out var tool));
        Assert.AreEqual("svc", server);
        Assert.AreEqual("weird__tool", tool);
    }

    [TestMethod]
    public void TryParse_RejectsNamesWithoutBothParts()
    {
        Assert.IsFalse(WrapperNaming.TryParse("no_separator", out _, out _));
        Assert.IsFalse(WrapperNaming.TryParse("__tool", out _, out _));
        Assert.IsFalse(WrapperNaming.TryParse("server__", out _, out _));
        Assert.IsFalse(WrapperNaming.TryParse("", out _, out _));
    }
}
