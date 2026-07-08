// T011 — 002 structural document validation (FR-VAL-001, FR-VAL-003): pure
// API-process checks, no engine. Collect-all (never fail-fast); every issue
// carries code, severity, tag/path, message. Codes per data-model.md.

using System.Text.Json;
using DwsimRunner.Api;

namespace DwsimRunner.Api.Tests;

public class DocumentValidatorTests
{
    // Minimal catalog: the same shape the worker's `catalog` mode emits.
    private static readonly CatalogModel Catalog = CatalogModel.Parse("""
    {
      "engineVersion": "9.0.5.0",
      "unitOpTypes": [
        { "type": "separator", "displayName": "Gas-Liquid Separator",
          "ports": [
            { "name": "Inlet", "direction": "in", "accepts": "material", "required": true },
            { "name": "Vapor Outlet", "direction": "out", "accepts": "material", "required": true },
            { "name": "Liquid Outlet", "direction": "out", "accepts": "material", "required": true } ],
          "parameters": [], "requiresReactionSet": false },
        { "type": "heater", "displayName": "Heater",
          "ports": [
            { "name": "Inlet", "direction": "in", "accepts": "material", "required": true },
            { "name": "Outlet", "direction": "out", "accepts": "material", "required": true },
            { "name": "Energy Inlet", "direction": "in", "accepts": "energy", "required": false } ],
          "parameters": [
            { "name": "outletTemperature", "unitType": "temperature", "required": true },
            { "name": "heatDuty", "unitType": "power", "required": false } ],
          "requiresReactionSet": false },
        { "type": "cooler", "displayName": "Cooler",
          "ports": [
            { "name": "Inlet", "direction": "in", "accepts": "material", "required": true },
            { "name": "Outlet", "direction": "out", "accepts": "material", "required": true },
            { "name": "Energy Outlet", "direction": "out", "accepts": "energy", "required": false } ],
          "parameters": [
            { "name": "outletTemperature", "unitType": "temperature", "required": false },
            { "name": "heatDuty", "unitType": "power", "required": false } ],
          "requiresReactionSet": false },
        { "type": "reactorConversion", "displayName": "Conversion Reactor",
          "ports": [
            { "name": "Inlet", "direction": "in", "accepts": "material", "required": true },
            { "name": "Outlet", "direction": "out", "accepts": "material", "required": true } ],
          "parameters": [], "requiresReactionSet": true }
      ]
    }
    """);

    private static List<ValidationIssue> Validate(string json) =>
        DocumentValidator.ValidateStructural(JsonSerializer.Deserialize<JsonElement>(json), Catalog);

    private const string ValidDoc = """
    {
      "schemaVersion": 1,
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 0, "unit": "C" },
                    "pressure": { "value": 50, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
        { "tag": "V-1", "kind": "unitOp", "type": "separator" },
        { "tag": "VAP", "kind": "materialStream" },
        { "tag": "LIQ", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "V-1", "port": "Inlet" },
        { "from": "V-1", "to": "VAP", "port": "Vapor Outlet" },
        { "from": "V-1", "to": "LIQ", "port": "Liquid Outlet" }
      ]
    }
    """;

    [Fact]
    public void Valid_document_yields_no_errors()
    {
        var issues = Validate(ValidDoc);
        Assert.DoesNotContain(issues, i => i.Severity == "error");
    }

    [Fact]
    public void Unsupported_schema_version()
    {
        var issues = Validate("""{ "schemaVersion": 99, "compounds": ["Methane"], "propertyPackage": "PR", "objects": [], "connections": [] }""");
        Assert.Contains(issues, i => i.Code == "UNSUPPORTED_SCHEMA");
    }

    [Fact]
    public void Duplicate_tags_are_named()
    {
        var doc = ValidDoc.Replace("\"tag\": \"VAP\"", "\"tag\": \"FEED\"");
        var issues = Validate(doc);
        var dup = Assert.Single(issues, i => i.Code == "DUPLICATE_TAG");
        Assert.Equal("FEED", dup.Tag);
    }

    [Fact]
    public void Unknown_unit_op_type()
    {
        var doc = ValidDoc.Replace("\"type\": \"separator\"", "\"type\": \"warpDrive\"");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "UNKNOWN_UNIT_OP_TYPE" && i.Tag == "V-1");
    }

    [Fact]
    public void Unknown_port_lists_valid_ports_in_message()
    {
        var doc = ValidDoc.Replace("\"port\": \"Vapor Outlet\"", "\"port\": \"Vapour Out\"");
        var issues = Validate(doc);
        var issue = Assert.Single(issues, i => i.Code == "UNKNOWN_PORT");
        Assert.Equal("V-1", issue.Tag);
        Assert.Contains("Vapor Outlet", issue.Message);
    }

    [Fact]
    public void Unresolved_connection_reference()
    {
        var doc = ValidDoc.Replace("\"from\": \"FEED\"", "\"from\": \"GHOST\"");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "UNRESOLVED_REFERENCE");
    }

    [Fact]
    public void Stream_connected_to_two_inlets_is_a_port_conflict()
    {
        var doc = ValidDoc.Replace(
            "{ \"from\": \"V-1\", \"to\": \"LIQ\", \"port\": \"Liquid Outlet\" }",
            "{ \"from\": \"V-1\", \"to\": \"LIQ\", \"port\": \"Liquid Outlet\" }," +
            "{ \"from\": \"FEED\", \"to\": \"V-1\", \"port\": \"Inlet\" }");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "PORT_CONFLICT");
    }

    [Fact]
    public void Missing_required_parameter()
    {
        var doc = ValidDoc.Replace("\"type\": \"separator\"", "\"type\": \"heater\"");
        // heater requires outletTemperature and has different ports — expect the
        // missing parameter to be reported alongside any port issues (collect-all).
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "MISSING_REQUIRED_PARAMETER" && i.Tag == "V-1");
    }

    [Fact]
    public void Reactor_without_reaction_set()
    {
        var doc = ValidDoc.Replace("\"type\": \"separator\"", "\"type\": \"reactorConversion\"");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "MISSING_REACTION_SET" && i.Tag == "V-1");
    }

    [Fact]
    public void Composition_must_normalize()
    {
        var doc = ValidDoc.Replace("\"Methane\": 0.5, \"Ethane\": 0.5", "\"Methane\": 0.7, \"Ethane\": 0.5");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "COMPOSITION_NOT_NORMALIZED" && i.Tag == "FEED");
    }

    [Fact]
    public void Invalid_unit_is_rejected()
    {
        var doc = ValidDoc.Replace("\"unit\": \"bar\"", "\"unit\": \"furlongs\"");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "INVALID_UNIT" && i.Tag == "FEED");
    }

    [Fact]
    public void Document_too_large_is_rejected()
    {
        var objects = string.Join(",", Enumerable.Range(0, 101)
            .Select(i => $$"""{ "tag": "S{{i}}", "kind": "materialStream" }"""));
        var issues = Validate($$"""{ "schemaVersion": 1, "compounds": ["Methane"], "propertyPackage": "PR", "objects": [{{objects}}], "connections": [] }""");
        Assert.Contains(issues, i => i.Code == "DOCUMENT_TOO_LARGE");
    }

    // 005-unitop-parameter-application T006 / FR-FIX-004: outletTemperature
    // and heatDuty on the same heater/cooler are mutually exclusive — the
    // engine's CalcMode can only honor one of them.
    private static string UnitOpDoc(string type, string parameters) => $$"""
    {
      "schemaVersion": 1,
      "compounds": ["Water"],
      "propertyPackage": "STEAM",
      "objects": [
        { "tag": "FEED", "kind": "materialStream" },
        { "tag": "U-1", "kind": "unitOp", "type": "{{type}}",
          "parameters": { {{parameters}} } },
        { "tag": "PROD", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "U-1", "port": "Inlet" },
        { "from": "U-1", "to": "PROD", "port": "Outlet" }
      ]
    }
    """;

    [Theory]
    [InlineData("heater")]
    [InlineData("cooler")]
    public void OutletTemperature_and_heatDuty_together_conflict(string type)
    {
        var issues = Validate(UnitOpDoc(type, """
            "outletTemperature": { "value": 353.15, "unit": "K" },
                          "heatDuty": { "value": 230, "unit": "kW" }
            """));
        var conflict = Assert.Single(issues, i => i.Code == "CONFLICTING_PARAMETERS");
        Assert.Equal("error", conflict.Severity);
        Assert.Equal("U-1", conflict.Tag);
        Assert.Contains("outletTemperature", conflict.Message);
        Assert.Contains("heatDuty", conflict.Message);
    }

    [Theory]
    [InlineData("heater")]
    [InlineData("cooler")]
    public void OutletTemperature_alone_does_not_conflict(string type)
    {
        var issues = Validate(UnitOpDoc(type, """
            "outletTemperature": { "value": 353.15, "unit": "K" }
            """));
        Assert.DoesNotContain(issues, i => i.Code == "CONFLICTING_PARAMETERS");
    }

    [Theory]
    [InlineData("heater")]
    [InlineData("cooler")]
    public void HeatDuty_alone_does_not_conflict(string type)
    {
        var issues = Validate(UnitOpDoc(type, """
            "heatDuty": { "value": 230, "unit": "kW" }
            """));
        Assert.DoesNotContain(issues, i => i.Code == "CONFLICTING_PARAMETERS");
    }

    [Fact]
    public void Collects_all_issues_never_fail_fast()
    {
        var doc = ValidDoc
            .Replace("\"type\": \"separator\"", "\"type\": \"warpDrive\"")
            .Replace("\"Methane\": 0.5, \"Ethane\": 0.5", "\"Methane\": 0.7, \"Ethane\": 0.5");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "UNKNOWN_UNIT_OP_TYPE");
        Assert.Contains(issues, i => i.Code == "COMPOSITION_NOT_NORMALIZED");
    }
}
