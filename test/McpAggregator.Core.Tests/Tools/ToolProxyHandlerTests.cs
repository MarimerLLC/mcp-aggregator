using System.Reflection;
using System.Text.Json;
using McpAggregator.Core.Tools;

namespace McpAggregator.Core.Tests.Tools;

[TestClass]
public class ToolProxyHandlerTests
{
    private static readonly MethodInfo ConvertMethod =
        typeof(ToolProxyHandler).GetMethod("ConvertJsonElement",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object? ConvertJsonElement(JsonElement element)
        => ConvertMethod.Invoke(null, [element]);

    [TestMethod]
    public void ConvertJsonElement_String_ReturnsString()
    {
        var element = JsonDocument.Parse("\"hello\"").RootElement;
        Assert.AreEqual("hello", ConvertJsonElement(element));
    }

    [TestMethod]
    public void ConvertJsonElement_Integer_ReturnsLong()
    {
        var element = JsonDocument.Parse("42").RootElement;
        var result = ConvertJsonElement(element);
        Assert.IsInstanceOfType<long>(result);
        Assert.AreEqual(42L, result);
    }

    [TestMethod]
    public void ConvertJsonElement_Double_ReturnsDouble()
    {
        var element = JsonDocument.Parse("3.14").RootElement;
        var result = ConvertJsonElement(element);
        Assert.IsInstanceOfType<double>(result);
        Assert.AreEqual(3.14, result);
    }

    [TestMethod]
    public void ConvertJsonElement_Boolean_ReturnsBool()
    {
        var trueElement = JsonDocument.Parse("true").RootElement;
        var falseElement = JsonDocument.Parse("false").RootElement;
        Assert.AreEqual(true, ConvertJsonElement(trueElement));
        Assert.AreEqual(false, ConvertJsonElement(falseElement));
    }

    [TestMethod]
    public void ConvertJsonElement_Null_ReturnsNull()
    {
        var element = JsonDocument.Parse("null").RootElement;
        Assert.IsNull(ConvertJsonElement(element));
    }

    [TestMethod]
    public void ConvertJsonElement_SimpleArray_ReturnsList()
    {
        var element = JsonDocument.Parse("[1, 2, 3]").RootElement;
        var result = ConvertJsonElement(element) as List<object?>;
        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(new object[] { 1L, 2L, 3L }, result);
    }

    [TestMethod]
    public void ConvertJsonElement_SimpleObject_ReturnsDictionary()
    {
        var element = JsonDocument.Parse("""{"key":"value","num":5}""").RootElement;
        var result = ConvertJsonElement(element) as Dictionary<string, object?>;
        Assert.IsNotNull(result);
        Assert.AreEqual("value", result["key"]);
        Assert.AreEqual(5L, result["num"]);
    }

    [TestMethod]
    public void ConvertJsonElement_ArrayOfObjects_ReturnsListOfDictionaries()
    {
        // This is the exact pattern used by bulk email operations
        var json = """
            [
                {"messageId": "msg-001", "folderId": "inbox"},
                {"messageId": "msg-002", "folderId": "archive"}
            ]
            """;
        var element = JsonDocument.Parse(json).RootElement;
        var result = ConvertJsonElement(element) as List<object?>;

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Count);

        var first = result[0] as Dictionary<string, object?>;
        Assert.IsNotNull(first);
        Assert.AreEqual("msg-001", first["messageId"]);
        Assert.AreEqual("inbox", first["folderId"]);

        var second = result[1] as Dictionary<string, object?>;
        Assert.IsNotNull(second);
        Assert.AreEqual("msg-002", second["messageId"]);
        Assert.AreEqual("archive", second["folderId"]);
    }

    [TestMethod]
    public void ConvertJsonElement_NestedStructure_RecursivelyConverts()
    {
        var json = """
            {
                "items": [
                    {"id": "1", "tags": ["a", "b"]},
                    {"id": "2", "active": true}
                ],
                "count": 2
            }
            """;
        var element = JsonDocument.Parse(json).RootElement;
        var result = ConvertJsonElement(element) as Dictionary<string, object?>;

        Assert.IsNotNull(result);
        Assert.AreEqual(2L, result["count"]);

        var items = result["items"] as List<object?>;
        Assert.IsNotNull(items);
        Assert.AreEqual(2, items.Count);

        var first = items[0] as Dictionary<string, object?>;
        Assert.IsNotNull(first);
        Assert.AreEqual("1", first["id"]);

        var tags = first["tags"] as List<object?>;
        Assert.IsNotNull(tags);
        CollectionAssert.AreEqual(new object[] { "a", "b" }, tags);

        var second = items[1] as Dictionary<string, object?>;
        Assert.IsNotNull(second);
        Assert.AreEqual(true, second["active"]);
    }

    [TestMethod]
    public void FullArgumentsJson_BulkEmailPayload_ConvertsCorrectly()
    {
        // Simulate the full arguments JSON that would come in for bulk_move_emails
        var argumentsJson = """
            {
                "provider": "m365",
                "destinationFolderId": "folder-123",
                "emails": [
                    {"messageId": "msg-001"},
                    {"messageId": "msg-002"},
                    {"messageId": "msg-003"}
                ]
            }
            """;

        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson)!;
        var args = raw.ToDictionary(kvp => kvp.Key, kvp => ConvertJsonElement(kvp.Value));

        Assert.AreEqual("m365", args["provider"]);
        Assert.AreEqual("folder-123", args["destinationFolderId"]);

        var emails = args["emails"] as List<object?>;
        Assert.IsNotNull(emails);
        Assert.AreEqual(3, emails.Count);

        foreach (var email in emails)
        {
            var dict = email as Dictionary<string, object?>;
            Assert.IsNotNull(dict);
            Assert.IsTrue(dict.ContainsKey("messageId"));
            Assert.IsInstanceOfType<string>(dict["messageId"]);
        }
    }
}
