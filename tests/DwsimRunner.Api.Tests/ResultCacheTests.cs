// T013 — 002 canonicalized-document cache keys (FR-BUILD-005): identical
// documents hash identically regardless of key order/whitespace; engine
// version partitions the key space; any value change changes the key.

using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class ResultCacheTests
{
    private static string Key(string json, string engineVersion = "9.0.5.0") =>
        ResultCache.KeyForDocument(JsonSerializer.Deserialize<JsonElement>(json), engineVersion);

    [Fact]
    public void Key_is_stable_under_property_order_and_whitespace()
    {
        var a = Key("""{ "schemaVersion": 1, "compounds": ["Methane"], "propertyPackage": "PR" }""");
        var b = Key("""{"propertyPackage":"PR","compounds":["Methane"],"schemaVersion":1}""");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Array_order_is_significant()
    {
        var a = Key("""{ "compounds": ["Methane", "Ethane"] }""");
        var b = Key("""{ "compounds": ["Ethane", "Methane"] }""");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Value_changes_change_the_key()
    {
        var a = Key("""{ "objects": [ { "tag": "FEED", "spec": { "temperature": { "value": 25 } } } ] }""");
        var b = Key("""{ "objects": [ { "tag": "FEED", "spec": { "temperature": { "value": 26 } } } ] }""");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Engine_version_partitions_keys()
    {
        var doc = """{ "schemaVersion": 1 }""";
        Assert.NotEqual(Key(doc, "9.0.5.0"), Key(doc, "9.0.6.0"));
    }
}
