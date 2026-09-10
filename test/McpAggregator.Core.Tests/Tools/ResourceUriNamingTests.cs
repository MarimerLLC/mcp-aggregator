using McpAggregator.Core.Tools;
using ModelContextProtocol.Protocol;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// The <c>mcp-aggregator://{server}/{uri}</c> rewrite (issue #45) must round-trip any downstream
/// URI verbatim and its template matcher must accept the expansions a client will send.
/// </summary>
[TestClass]
public class ResourceUriNamingTests
{
    [TestMethod]
    public void For_PrefixesTheDownstreamUriVerbatim()
    {
        Assert.AreEqual("mcp-aggregator://probe/file:///docs/readme.md", ResourceUriNaming.For("probe", "file:///docs/readme.md"));
        Assert.AreEqual("mcp-aggregator://probe/", ResourceUriNaming.Prefix("probe"));
    }

    [DataTestMethod]
    [DataRow("file:///docs/readme.md")]
    [DataRow("https://example.com/a/b?x=1&y=2")]
    [DataRow("https://example.com/a/b#section-2")]
    [DataRow("file:///C:/Users/me/read me.md")]
    [DataRow("file:///docs/{name}")]
    [DataRow("git://repo/{+path}{?rev}")]
    [DataRow("custom:opaque-thing")]
    public void TryParse_RoundTripsEveryDownstreamForm(string downstream)
    {
        var rewritten = ResourceUriNaming.For("probe", downstream);

        Assert.IsTrue(ResourceUriNaming.TryParse(rewritten, out var server, out var parsed));
        Assert.AreEqual("probe", server);
        Assert.AreEqual(downstream, parsed);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("file:///docs/readme.md")]
    [DataRow("mcp-aggregator://")]
    [DataRow("mcp-aggregator:///file:///x")]
    [DataRow("mcp-aggregator://probe")]
    [DataRow("mcp-aggregator://probe/")]
    [DataRow("other://probe/file:///x")]
    public void TryParse_RejectsAnythingThatIsNotAggregatorForm(string? value)
    {
        Assert.IsFalse(ResourceUriNaming.TryParse(value, out _, out _));
    }

    [TestMethod]
    public void IsFor_ComparesTheServerCaseInsensitively_AndRejectsOtherServers()
    {
        Assert.IsTrue(ResourceUriNaming.IsFor("mcp-aggregator://Probe/file:///x", "probe", out var downstream));
        Assert.AreEqual("file:///x", downstream);
        Assert.IsFalse(ResourceUriNaming.IsFor("mcp-aggregator://other/file:///x", "probe", out _));
        Assert.IsFalse(ResourceUriNaming.IsFor("file:///x", "probe", out _));
    }

    [TestMethod]
    public void RewriteContents_PrefixesEveryContentUri_AndLeavesAlreadyRewrittenOnesAlone()
    {
        var result = new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents { Uri = "file:///a.txt", Text = "a" },
                new BlobResourceContents { Uri = "mcp-aggregator://probe/file:///b.bin", Blob = new byte[] { 1 } },
            ]
        };

        ResourceUriNaming.RewriteContents("probe", result);

        Assert.AreEqual("mcp-aggregator://probe/file:///a.txt", result.Contents[0].Uri);
        Assert.AreEqual("mcp-aggregator://probe/file:///b.bin", result.Contents[1].Uri);
    }

    // ---------------------------------------------------------------- template matcher

    [DataTestMethod]
    [DataRow("file:///docs/{name}", "file:///docs/readme.md", true)]
    [DataRow("file:///docs/{name}", "file:///docs/a/b", false)]
    [DataRow("file:///docs/{name}", "file:///other/readme.md", false)]
    [DataRow("file:///docs/{name}.md", "file:///docs/readme.md", true)]
    [DataRow("file:///{a,b}", "file:///x,y", true)]
    [DataRow("git://repo/{+path}", "git://repo/src/a/b.cs", true)]
    [DataRow("git://repo/{+path}", "git://repo/", false)]
    [DataRow("page://{id}{#section}", "page://42#intro", true)]
    [DataRow("page://{id}{#section}", "page://42", true)]
    [DataRow("fs://root{/path}", "fs://root/a/b", true)]
    [DataRow("fs://root{/path}", "fs://root", true)]
    [DataRow("search://q{?term,limit}", "search://q?term=x&limit=2", true)]
    [DataRow("search://q{?term,limit}", "search://q", true)]
    [DataRow("search://q{?term}{&limit}", "search://q?term=x&limit=2", true)]
    [DataRow("host://name{.ext}", "host://name.txt", true)]
    [DataRow("host://name{.ext}", "host://name", true)]
    [DataRow("p://x{;v}", "p://x;v=1", true)]
    [DataRow("file:///docs/readme.md", "file:///docs/readme.md", true)]
    [DataRow("file:///docs/readme.md", "file:///docs/readme.md.bak", false)]
    [DataRow("file:///docs/{unclosed", "file:///docs/{unclosed", true)]
    public void BuildTemplateMatcher_ApproximatesRfc6570(string template, string candidate, bool expected)
    {
        var matcher = ResourceUriNaming.BuildTemplateMatcher(template);

        Assert.AreEqual(expected, matcher.IsMatch(candidate), $"'{template}' vs '{candidate}'");
    }

    [TestMethod]
    public void BuildTemplateMatcher_EscapesRegexMetacharactersInLiterals()
    {
        var matcher = ResourceUriNaming.BuildTemplateMatcher("q://a.b+c(d)/{x}");

        Assert.IsTrue(matcher.IsMatch("q://a.b+c(d)/1"));
        Assert.IsFalse(matcher.IsMatch("q://aXb+c(d)/1"));
    }
}
