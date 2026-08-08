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

    // ── 143: the solver is selectable, and the selection is observable ──────
    // These live in the INTEGRATION tier rather than beside the other catalog assertions
    // because the API unit tests serve a canned catalog from FakeWorker — asserting there
    // would prove the fixture, not `UnitOpCatalog`.

    private static string WithSolver(string method) =>
        ColumnDoc.Replace("\"numberOfStages\": 10", $"\"solvingMethod\": \"{method}\", \"numberOfStages\": 10");

    [SkippableFact]
    public async Task Catalog_offers_the_solver_knobs_as_optional_parameters()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.GetAsync("/catalog/unit-op-types");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var column = body.GetProperty("unitOpTypes").EnumerateArray()
            .Single(t => t.GetProperty("type").GetString() == "distillationColumn");
        var byName = column.GetProperty("parameters").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, p => p);

        Assert.Equal("string", byName["solvingMethod"].GetProperty("unitType").GetString());
        Assert.Equal("integer", byName["maxIterations"].GetProperty("unitType").GetString());
        // Optional, always. A required solver would make every existing column document invalid,
        // and the whole point of 143 FR-006 is that the default keeps working.
        Assert.False(byName["solvingMethod"].GetProperty("required").GetBoolean());
        Assert.False(byName["maxIterations"].GetProperty("required").GetBoolean());
    }

    [SkippableFact]
    public async Task A_named_solver_reaches_the_engine_object()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(WithSolver("naphtaliSandholm"), timeoutSeconds: 300));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

        // The assertion is the READ-BACK, deliberately, and NOT the converged flag or the
        // products. A solver that is accepted and ignored produces exactly the results the
        // default would, so an outcome-shaped assertion here would pass without the feature.
        // `Column.Calculate` dispatches on a substring of this string, so what it says is what
        // ran. (The spelling is DWSIM's own.)
        var column = r.GetProperty("unitOps").EnumerateArray()
            .Single(u => u.GetProperty("type").GetString() == "distillationColumn");
        Assert.Contains("Napthali", column.GetProperty("solvingMethod").GetString());
        Assert.Equal(100, column.GetProperty("maxIterations").GetInt32());   // the engine default, untouched
    }

    [SkippableFact]
    public async Task An_unknown_solver_is_refused_before_the_solve()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(WithSolver("gaussSeidelWishfulThinking"), timeoutSeconds: 60));

        // DWSIM's own failure for an unrecognised name is a bare Exception thrown from INSIDE
        // Calculate, which would surface as a non-convergence. 141 FR-001's rule is that a
        // setting the runner cannot bind is a typed refusal — so the vocabulary is closed here.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_PARAMETER_VALUE", body);
        Assert.Contains("naphtaliSandholm", body);   // the refusal names what IS available
    }

    [SkippableFact]
    public async Task The_iteration_limit_is_settable()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var doc = ColumnDoc.Replace("\"numberOfStages\": 10", "\"maxIterations\": 250, \"numberOfStages\": 10");
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(doc, timeoutSeconds: 300));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var column = r.GetProperty("unitOps").EnumerateArray()
            .Single(u => u.GetProperty("type").GetString() == "distillationColumn");
        Assert.Equal(250, column.GetProperty("maxIterations").GetInt32());
    }
}
