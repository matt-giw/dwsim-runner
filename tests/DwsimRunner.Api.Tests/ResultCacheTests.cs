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

    // ISK-442 — the document-CASE key (/compare, /optimize over a document).
    private static string CaseKey(params PropertyOverride[] overrides) =>
        ResultCache.KeyForDocumentCase(JsonSerializer.Deserialize<JsonElement>("""{ "schemaVersion": 1 }"""),
            overrides, "9.0.5.0");

    [Fact]
    public void Document_case_key_ignores_the_order_of_overrides_on_different_targets()
    {
        var t = new PropertyOverride("FEED", "temperature", 30, "C");
        var p = new PropertyOverride("FEED", "pressure", 5, "bar");
        Assert.Equal(CaseKey(t, p), CaseKey(p, t));
    }

    [Fact]
    public void Document_case_key_keeps_the_order_of_overrides_on_one_target()
    {
        // Applied in order, last wins — so [30, 40] and [40, 30] are different requests.
        var a = new PropertyOverride("FEED", "temperature", 30, null);
        var b = new PropertyOverride("FEED", "temperature", 40, null);
        Assert.NotEqual(CaseKey(a, b), CaseKey(b, a));
    }

    [Fact]
    public void Document_case_key_separates_fields_unambiguously()
    {
        // With a plain-character join these two collapse to the same string.
        Assert.NotEqual(
            CaseKey(new PropertyOverride("A|B", "C", 1, null)),
            CaseKey(new PropertyOverride("A", "B|C", 1, null)));
    }

    [Fact]
    public void Document_case_key_is_partitioned_from_the_plain_document_key()
    {
        var doc = JsonSerializer.Deserialize<JsonElement>("""{ "schemaVersion": 1 }""");
        Assert.NotEqual(ResultCache.KeyForDocument(doc, "9.0.5.0"), CaseKey());
    }

    [Fact]
    public void Canonical_overrides_carry_no_control_characters()
    {
        var canon = ResultCache.CanonicalOverrides([new PropertyOverride("FEED", "temperature", 30.5, "C")]);
        Assert.DoesNotContain(canon, char.IsControl);
    }
}
