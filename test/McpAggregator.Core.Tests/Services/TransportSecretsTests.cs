using McpAggregator.Core.Exceptions;
using McpAggregator.Core.Models;
using McpAggregator.Core.Services;

namespace McpAggregator.Core.Tests.Services;

[TestClass]
public class TransportSecretsTests
{
    private readonly List<string> _setVariables = [];

    private void SetVar(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _setVariables.Add(name);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var name in _setVariables)
            Environment.SetEnvironmentVariable(name, null);
        _setVariables.Clear();
    }

    // --- Resolve ---

    [TestMethod]
    public void Resolve_PassesThroughLiteralValue()
    {
        Assert.AreEqual("plain-secret", TransportSecrets.Resolve("plain-secret"));
    }

    [TestMethod]
    public void Resolve_ExpandsWholeValueReference()
    {
        SetVar("MCPAGG_TEST_TOKEN", "abc123");

        Assert.AreEqual("abc123", TransportSecrets.Resolve("${MCPAGG_TEST_TOKEN}"));
    }

    [TestMethod]
    public void Resolve_ExpandsEmbeddedReference()
    {
        SetVar("MCPAGG_TEST_TOKEN", "abc123");

        Assert.AreEqual("Bearer abc123", TransportSecrets.Resolve("Bearer ${MCPAGG_TEST_TOKEN}"));
    }

    [TestMethod]
    public void Resolve_ExpandsMultipleReferencesInOneValue()
    {
        SetVar("MCPAGG_TEST_USER", "rocky");
        SetVar("MCPAGG_TEST_PASS", "s3cret");

        Assert.AreEqual("rocky:s3cret", TransportSecrets.Resolve("${MCPAGG_TEST_USER}:${MCPAGG_TEST_PASS}"));
    }

    [TestMethod]
    public void Resolve_EscapeYieldsLiteralPlaceholder()
    {
        Assert.AreEqual("${NOT_A_VAR}", TransportSecrets.Resolve("$${NOT_A_VAR}"));
    }

    [TestMethod]
    public void Resolve_UndefinedVariable_ThrowsNamingTheVariable()
    {
        Environment.SetEnvironmentVariable("MCPAGG_TEST_MISSING", null);

        var ex = Assert.ThrowsExactly<InvalidTransportConfigException>(
            () => TransportSecrets.Resolve("Bearer ${MCPAGG_TEST_MISSING}"));

        StringAssert.Contains(ex.Message, "MCPAGG_TEST_MISSING");
    }

    [TestMethod]
    public void Resolve_EmptyReference_Throws()
    {
        Assert.ThrowsExactly<InvalidTransportConfigException>(() => TransportSecrets.Resolve("${}"));
    }

    // --- ResolveHeaders ---

    [TestMethod]
    public void ResolveHeaders_ReturnsNull_WhenHeadersNull()
    {
        var config = new TransportConfig { Type = TransportType.Http, Url = "http://localhost:8080" };

        Assert.IsNull(TransportSecrets.ResolveHeaders(config));
    }

    [TestMethod]
    public void ResolveHeaders_ReturnsNull_WhenHeadersEmpty()
    {
        var config = new TransportConfig
        {
            Type = TransportType.Http,
            Url = "http://localhost:8080",
            Headers = []
        };

        Assert.IsNull(TransportSecrets.ResolveHeaders(config));
    }

    [TestMethod]
    public void ResolveHeaders_ResolvesEachValue()
    {
        SetVar("MCPAGG_TEST_TOKEN", "abc123");
        var config = new TransportConfig
        {
            Type = TransportType.Http,
            Url = "http://localhost:8080",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer ${MCPAGG_TEST_TOKEN}",
                ["X-Tenant"] = "contoso"
            }
        };

        var resolved = TransportSecrets.ResolveHeaders(config);

        Assert.IsNotNull(resolved);
        Assert.AreEqual("Bearer abc123", resolved["Authorization"]);
        Assert.AreEqual("contoso", resolved["X-Tenant"]);
    }
}
