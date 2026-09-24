using System.Text.Json;
using McpAggregator.Core.Tools;

namespace McpAggregator.Core.Tests.Tools;

/// <summary>
/// <see cref="ToolProxyHandler.FindTypeMismatches"/> is the positive evidence behind the "declared as
/// X but you sent Y" hint (issue #50), so it must never report a mismatch that is not one.
/// </summary>
[TestClass]
public class ToolProxyTypeMismatchTests
{
    private static JsonElement Schema(string properties)
        => JsonDocument.Parse("""{"type":"object","properties":{""" + properties + "}}").RootElement;

    private static IReadOnlyDictionary<string, object?> Args(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kvp => kvp.Key, kvp => ToolProxyHandler.ConvertJsonElement(kvp.Value));

    [TestMethod]
    [DataRow("""{"type":"string"}""", """{"v":"x"}""")]
    [DataRow("""{"type":"array"}""", """{"v":["x"]}""")]
    [DataRow("""{"type":"object"}""", """{"v":{"a":1}}""")]
    [DataRow("""{"type":"boolean"}""", """{"v":true}""")]
    [DataRow("""{"type":"integer"}""", """{"v":3}""")]
    [DataRow("""{"type":"integer"}""", """{"v":3.0}""")]
    [DataRow("""{"type":"number"}""", """{"v":3}""")]
    [DataRow("""{"type":"number"}""", """{"v":3.5}""")]
    [DataRow("""{"type":["string","null"]}""", """{"v":"x"}""")]
    [DataRow("""{"type":"string"}""", """{"v":null}""")]
    [DataRow("""{"anyOf":[{"type":"string"},{"type":"array"}]}""", """{"v":42}""")]
    [DataRow("""{"type":"string"}""", """{"other":42}""")]
    public void Compatible_ReportsNothing(string property, string args)
    {
        var mismatches = ToolProxyHandler.FindTypeMismatches(Schema($"\"v\":{property}"), Args(args));

        Assert.AreEqual(0, mismatches.Count, string.Join("; ", mismatches));
    }

    [TestMethod]
    [DataRow("""{"type":"array"}""", """{"v":"x"}""", "array", "a string")]
    [DataRow("""{"type":"string"}""", """{"v":42}""", "string", "an integer")]
    [DataRow("""{"type":"integer"}""", """{"v":3.5}""", "integer", "a number")]
    [DataRow("""{"type":"boolean"}""", """{"v":"true"}""", "boolean", "a string")]
    [DataRow("""{"type":"object"}""", """{"v":[1]}""", "object", "an array")]
    [DataRow("""{"type":["array","null"]}""", """{"v":"x"}""", "array or null", "a string")]
    public void Incompatible_NamesParameterAndBothTypes(string property, string args, string declared, string sent)
    {
        var mismatches = ToolProxyHandler.FindTypeMismatches(Schema($"\"v\":{property}"), Args(args));

        Assert.AreEqual(1, mismatches.Count);
        Assert.AreEqual(("v", declared, sent), mismatches[0]);
    }
}
