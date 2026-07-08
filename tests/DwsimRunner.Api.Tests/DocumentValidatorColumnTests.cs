// T031 — US2 column-specific structural checks (FR-VAL-001): feed stage must
// sit inside the stage count, reflux ratio must be positive, and the condenser
// cannot run at a higher pressure than the reboiler. Pure DocumentValidator
// unit tests against a catalog carrying both column types.

using System.Text.Json;
using DwsimRunner.Api;

namespace DwsimRunner.Api.Tests;

public class DocumentValidatorColumnTests
{
    private static readonly CatalogModel Catalog = CatalogModel.Parse("""
    {
      "engineVersion": "9.0.5.0",
      "unitOpTypes": [
        { "type": "distillationColumn", "displayName": "Distillation Column (rigorous)",
          "ports": [
            { "name": "Feed", "direction": "in", "accepts": "material", "required": true },
            { "name": "Distillate", "direction": "out", "accepts": "material", "required": true },
            { "name": "Bottoms", "direction": "out", "accepts": "material", "required": true },
            { "name": "Condenser Duty", "direction": "out", "accepts": "energy", "required": false },
            { "name": "Reboiler Duty", "direction": "in", "accepts": "energy", "required": false } ],
          "parameters": [
            { "name": "numberOfStages", "unitType": "integer", "required": true },
            { "name": "feedStage", "unitType": "integer", "required": true },
            { "name": "refluxRatio", "unitType": "dimensionless", "required": true },
            { "name": "condenserPressure", "unitType": "pressure", "required": true },
            { "name": "reboilerPressure", "unitType": "pressure", "required": true } ],
          "requiresReactionSet": false },
        { "type": "shortcutColumn", "displayName": "Shortcut Column (FUG)",
          "ports": [
            { "name": "Feed", "direction": "in", "accepts": "material", "required": true },
            { "name": "Distillate", "direction": "out", "accepts": "material", "required": true },
            { "name": "Bottoms", "direction": "out", "accepts": "material", "required": true } ],
          "parameters": [
            { "name": "refluxRatio", "unitType": "dimensionless", "required": true },
            { "name": "lightKey", "unitType": "string", "required": true },
            { "name": "heavyKey", "unitType": "string", "required": true },
            { "name": "condenserPressure", "unitType": "pressure", "required": true },
            { "name": "reboilerPressure", "unitType": "pressure", "required": true } ],
          "requiresReactionSet": false }
      ]
    }
    """);

    private static List<ValidationIssue> Validate(string json) =>
        DocumentValidator.ValidateStructural(JsonSerializer.Deserialize<JsonElement>(json), Catalog);

    private const string ColumnDoc = """
    {
      "schemaVersion": 1,
      "compounds": ["Methanol", "Water"],
      "propertyPackage": "NRTL",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 80, "unit": "C" },
                    "pressure": { "value": 1.2, "unit": "bar" },
                    "molarFlow": { "value": 100, "unit": "kmol/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Methanol": 0.4, "Water": 0.6 } } } },
        { "tag": "COL-1", "kind": "unitOp", "type": "distillationColumn",
          "parameters": {
            "numberOfStages": 10,
            "feedStage": 5,
            "refluxRatio": 2.5,
            "condenserPressure": { "value": 1.0, "unit": "bar" },
            "reboilerPressure": { "value": 1.2, "unit": "bar" } } },
        { "tag": "DIST", "kind": "materialStream" },
        { "tag": "BTMS", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "COL-1", "port": "Feed" },
        { "from": "COL-1", "to": "DIST", "port": "Distillate" },
        { "from": "COL-1", "to": "BTMS", "port": "Bottoms" }
      ]
    }
    """;

    [Fact]
    public void Valid_column_document_yields_no_errors()
    {
        var issues = Validate(ColumnDoc);
        Assert.DoesNotContain(issues, i => i.Severity == "error");
    }

    [Fact]
    public void Feed_stage_beyond_stage_count_is_invalid()
    {
        var doc = ColumnDoc.Replace("\"feedStage\": 5", "\"feedStage\": 11");
        var issues = Validate(doc);
        var issue = Assert.Single(issues, i => i.Code == "INVALID_PARAMETER_VALUE" && i.Severity == "error");
        Assert.Equal("COL-1", issue.Tag);
        Assert.Contains("feedStage", issue.Message);
    }

    [Fact]
    public void Feed_stage_zero_is_invalid()
    {
        var doc = ColumnDoc.Replace("\"feedStage\": 5", "\"feedStage\": 0");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "INVALID_PARAMETER_VALUE" && i.Tag == "COL-1"
                                     && i.Message.Contains("feedStage"));
    }

    [Fact]
    public void Non_positive_reflux_ratio_is_invalid()
    {
        var doc = ColumnDoc.Replace("\"refluxRatio\": 2.5", "\"refluxRatio\": 0");
        var issues = Validate(doc);
        var issue = Assert.Single(issues, i => i.Code == "INVALID_PARAMETER_VALUE" && i.Severity == "error");
        Assert.Equal("COL-1", issue.Tag);
        Assert.Contains("refluxRatio", issue.Message);
    }

    [Fact]
    public void Condenser_pressure_above_reboiler_pressure_is_invalid()
    {
        var doc = ColumnDoc
            .Replace("\"condenserPressure\": { \"value\": 1.0, \"unit\": \"bar\" }",
                     "\"condenserPressure\": { \"value\": 2.0, \"unit\": \"bar\" }");
        var issues = Validate(doc);
        var issue = Assert.Single(issues, i => i.Code == "INVALID_PARAMETER_VALUE" && i.Severity == "error");
        Assert.Equal("COL-1", issue.Tag);
        Assert.Contains("condenser", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pressure_ordering_compares_across_units()
    {
        // 90 kPa condenser vs 1.2 bar reboiler is fine; 130 kPa is not.
        var ok = ColumnDoc.Replace("\"condenserPressure\": { \"value\": 1.0, \"unit\": \"bar\" }",
                                   "\"condenserPressure\": { \"value\": 90, \"unit\": \"kPa\" }");
        Assert.DoesNotContain(Validate(ok), i => i.Severity == "error");

        var bad = ColumnDoc.Replace("\"condenserPressure\": { \"value\": 1.0, \"unit\": \"bar\" }",
                                    "\"condenserPressure\": { \"value\": 130, \"unit\": \"kPa\" }");
        Assert.Contains(Validate(bad), i => i.Code == "INVALID_PARAMETER_VALUE" && i.Tag == "COL-1");
    }

    [Fact]
    public void Shortcut_column_checks_reflux_and_pressure_ordering()
    {
        var shortcutDoc = ColumnDoc
            .Replace("\"type\": \"distillationColumn\"", "\"type\": \"shortcutColumn\"")
            .Replace("\"numberOfStages\": 10,", "")
            .Replace("\"feedStage\": 5,", "\"lightKey\": \"Methanol\", \"heavyKey\": \"Water\",");

        Assert.DoesNotContain(Validate(shortcutDoc), i => i.Severity == "error");

        var badReflux = shortcutDoc.Replace("\"refluxRatio\": 2.5", "\"refluxRatio\": -1");
        Assert.Contains(Validate(badReflux), i => i.Code == "INVALID_PARAMETER_VALUE" && i.Tag == "COL-1"
                                                  && i.Message.Contains("refluxRatio"));

        var badPressure = shortcutDoc.Replace("\"condenserPressure\": { \"value\": 1.0, \"unit\": \"bar\" }",
                                              "\"condenserPressure\": { \"value\": 5, \"unit\": \"bar\" }");
        Assert.Contains(Validate(badPressure), i => i.Code == "INVALID_PARAMETER_VALUE" && i.Tag == "COL-1");
    }

    [Fact]
    public void Missing_required_column_parameters_are_reported_per_parameter()
    {
        var doc = ColumnDoc.Replace("\"numberOfStages\": 10,", "").Replace("\"feedStage\": 5,", "");
        var issues = Validate(doc);
        Assert.Contains(issues, i => i.Code == "MISSING_REQUIRED_PARAMETER" && i.Message.Contains("numberOfStages"));
        Assert.Contains(issues, i => i.Code == "MISSING_REQUIRED_PARAMETER" && i.Message.Contains("feedStage"));
    }
}
