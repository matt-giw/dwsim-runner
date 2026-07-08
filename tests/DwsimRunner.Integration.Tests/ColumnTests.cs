// T033 — US2 Tier B (SC-003, quickstart Scenario 6): a 10-stage
// methanol/water rigorous column document converges, the distillate is
// methanol-enriched relative to the feed, and the overall mass balance
// closes to better than 99 %.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Column")]
public class ColumnTests
{
    private const string ColumnDoc = """
    {
      "schemaVersion": 1,
      "name": "methanol/water column integration",
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
            "bottomsMolarFlow": { "value": 60, "unit": "kmol/h" },
            "condenserPressure": { "value": 1.0, "unit": "bar" },
            "reboilerPressure": { "value": 1.2, "unit": "bar" } } },
        { "tag": "DIST", "kind": "materialStream" },
        { "tag": "BTMS", "kind": "materialStream" },
        { "tag": "Q-COND", "kind": "energyStream" },
        { "tag": "Q-REB", "kind": "energyStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "COL-1", "port": "Feed" },
        { "from": "COL-1", "to": "DIST", "port": "Distillate" },
        { "from": "COL-1", "to": "BTMS", "port": "Bottoms" },
        { "from": "COL-1", "to": "Q-COND", "port": "Condenser Duty" },
        { "from": "Q-REB", "to": "COL-1", "port": "Reboiler Duty" }
      ]
    }
    """;

    [SkippableFact]
    public async Task Ten_stage_methanol_water_column_converges_and_enriches_the_distillate()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(ColumnDoc, timeoutSeconds: 300));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(r.GetProperty("converged").GetBoolean(),
            "column did not converge: " + r.GetProperty("warnings"));

        var feed = BuildSolveTests.MassFlow(r, "FEED");
        var dist = BuildSolveTests.MassFlow(r, "DIST");
        var btms = BuildSolveTests.MassFlow(r, "BTMS");
        Assert.InRange((dist + btms) / feed, 0.99, 1.01);   // SC-003: > 99 % closure

        static double MethanolFrac(JsonElement result, string stream) =>
            result.GetProperty("streams").EnumerateArray()
                  .First(s => s.GetProperty("name").GetString() == stream)
                  .GetProperty("compositionMol").TryGetProperty("Methanol", out var f)
                      ? f.GetDouble() : 0.0;

        var feedMeoh = MethanolFrac(r, "FEED");
        var distMeoh = MethanolFrac(r, "DIST");
        Assert.True(distMeoh > feedMeoh,
            $"distillate methanol fraction {distMeoh} is not enriched over feed {feedMeoh}");
    }
}
