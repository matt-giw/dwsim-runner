// dwsim-runner integration tests — GPL-3.0
// Spec 199 — the calculation mode as an input, proved against a real engine.
//
// These are the assertions the offline tier CANNOT make. Whether a parameter reaches the engine is
// not a property of the catalog; it is a property of the solve, and this spec exists because five
// parameters were accepted, echoed, converged and ignored while every offline test stayed green.
//
// Every case here fails against the pre-199 runner for a stated reason.

using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "CalcMode")]
public class CalcModeTests
{
    /// <summary>A one-pump flowsheet whose only variable is the pump's parameter block.</summary>
    private static string PumpDoc(string parameters) => $$"""
    {
      "schemaVersion": 1, "name": "199 calc mode", "compounds": ["Water"], "propertyPackage": "PR",
      "objects": [
        { "tag": "IN1", "kind": "materialStream",
          "spec": { "temperature": { "value": 25, "unit": "C" }, "pressure": { "value": 1, "unit": "bar" },
                    "massFlow": { "value": 1000, "unit": "kg/h" },
                    "composition": { "basis": "molar", "fractions": { "Water": 1 } } } },
        { "tag": "P-101", "kind": "unitOp", "type": "pump", "parameters": {{parameters}} },
        { "tag": "OUT", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "IN1", "to": "P-101", "port": "Inlet" },
        { "from": "P-101", "to": "OUT", "port": "Outlet" }
      ]
    }
    """;

    private static StringContent Body(string doc) =>
        new($"{{\"document\":{doc}}}", Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Solve(string parameters)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", Body(PumpDoc(parameters)));
        return (resp.StatusCode, JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private static double OutletPressure(JsonElement r) =>
        r.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "OUT")
            .GetProperty("pressureBar").GetDouble();

    [SkippableFact]
    public async Task An_explicit_mode_makes_a_previously_inert_parameter_move_the_answer()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // THE headline case. `pump.outletPressure` measured `inert` on two capability captures 19
        // days and 17 runner commits apart — "both probe values produced a byte-identical answer".
        // A pump constructs in Delta_P and reads the increase, never the target, so the setpoint was
        // accepted, converged, and ignored. Naming the mode is what makes it an input.
        var (s10, r10) = await Solve("""{"calcMode":"outletPressure","outletPressure":{"value":10,"unit":"bar"},"efficiency":0.75}""");
        var (s20, r20) = await Solve("""{"calcMode":"outletPressure","outletPressure":{"value":20,"unit":"bar"},"efficiency":0.75}""");

        Assert.Equal(HttpStatusCode.OK, s10);
        Assert.Equal(HttpStatusCode.OK, s20);
        Assert.Equal(10.0, OutletPressure(r10), 1);
        Assert.Equal(20.0, OutletPressure(r20), 1);
        Assert.NotEqual(OutletPressure(r10), OutletPressure(r20));
    }

    [SkippableFact]
    public async Task A_document_with_no_mode_solves_exactly_as_it_did_before()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // FR-004. Every saved document is this shape — no `calcMode` — so inference must reproduce
        // the pre-199 answer exactly: 1 bar in, 5 bar increase, 6 bar out.
        var (status, r) = await Solve("""{"pressureIncrease":{"value":5,"unit":"bar"},"efficiency":0.75}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(r.GetProperty("converged").GetBoolean());
        Assert.Equal(6.0, OutletPressure(r), 1);
        Assert.Empty(r.GetProperty("warnings").EnumerateArray());
    }

    [SkippableFact]
    public async Task An_inferred_mode_drops_what_it_cannot_read_and_says_so()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // FR-003a's other half, and the one that protects D2. Both pressure forms on one pump
        // converges TODAY — silently, with `outletPressure` ignored. Refusing it would break saved
        // documents, so the answer is unchanged and the silence is what ends.
        var (status, r) = await Solve(
            """{"outletPressure":{"value":20,"unit":"bar"},"pressureIncrease":{"value":5,"unit":"bar"},"efficiency":0.75}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(r.GetProperty("converged").GetBoolean());
        Assert.Equal(6.0, OutletPressure(r), 1);   // the deltaP answer, exactly as before

        var warning = Assert.Single(r.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!).Where(w => w.Contains("PARAMETER_NOT_READ_BY_MODE")));
        Assert.Contains("no explicit calculation mode", warning);
        Assert.Contains("'deltaP' was inferred", warning);
        Assert.Contains("'outletPressure' is not read", warning);
    }

    [SkippableFact]
    public async Task An_explicit_mode_refuses_a_parameter_it_cannot_read_and_names_both()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var (status, r) = await Solve(
            """{"calcMode":"outletPressure","outletPressure":{"value":20,"unit":"bar"},"pressureIncrease":{"value":5,"unit":"bar"}}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);   // the runner's shape for a well-formed document it refuses
        var issue = r.GetProperty("issues").EnumerateArray()
            .Single(i => i.GetProperty("code").GetString() == "PARAMETER_NOT_READ_BY_MODE");
        var message = issue.GetProperty("message").GetString()!;

        // Both halves, and the alternatives. An error naming only one half sends the reader to the
        // wrong place (spec 173's scorer reported a missing VALUE where the cause was an unreachable
        // TOOL); naming the modes that WOULD read it is what makes it an instruction rather than a
        // dead end (spec 138's measured grounding fix).
        Assert.Contains("calculation mode 'outletPressure'", message);
        Assert.Contains("'pressureIncrease'", message);
        Assert.Contains("Modes that read it: deltaP", message);
    }

    [SkippableFact]
    public async Task An_unknown_mode_is_refused_with_the_known_ones_listed()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var (status, r) = await Solve("""{"calcMode":"polytropic","outletPressure":{"value":20,"unit":"bar"}}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);   // the runner's shape for a well-formed document it refuses
        var message = r.GetProperty("issues").EnumerateArray()
            .Single(i => i.GetProperty("code").GetString() == "UNKNOWN_CALC_MODE")
            .GetProperty("message").GetString()!;
        Assert.Contains("deltaP, outletPressure, energyStream, curves, power", message);
    }

    [SkippableFact]
    public async Task The_catalog_advertises_the_mode_map()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.GetAsync("/catalog/unit-op-types");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var types = body.GetProperty("unitOpTypes").EnumerateArray().ToList();

        // 15 mode-bearing unit ops, from modes.json. coverage-gap.md's table said 12 and missed
        // heatExchanger (11 modes), splitter and orificePlate.
        Assert.Equal(15, types.Count(t => t.TryGetProperty("calcMode", out _)));

        var pump = types.Single(t => t.GetProperty("type").GetString() == "pump").GetProperty("calcMode");
        Assert.Equal("deltaP", pump.GetProperty("default").GetString());
        Assert.Equal(5, pump.GetProperty("modes").GetArrayLength());

        // `consumes` carries the ratings every mode reads, not just the mode's own setpoint —
        // otherwise the app would grey a pump's efficiency in every mode.
        var deltaP = pump.GetProperty("modes").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "deltaP");
        var consumes = deltaP.GetProperty("consumes").EnumerateArray().Select(c => c.GetString()).ToArray();
        Assert.Contains("pressureIncrease", consumes);
        Assert.Contains("efficiency", consumes);
        Assert.DoesNotContain("outletPressure", consumes);
    }
}

/// <summary>
/// Spec 200 US2 — a value the engine reads only when a second property admits it.
///
/// 199 closed its D3 by measuring that `valve.openingPct` stays inert even with `kvGeneral` AND
/// `kvGas` explicitly selected: 20% and 80% open both give 7.987199 bar. Selecting the mode is
/// necessary and not sufficient — the valve also needs `DefinedOpeningKvRelationShipType`, which
/// says HOW an opening maps to a Kv. `ParamDef` could not express that, so the parameter was
/// declared, accepted, converged and ignored.
///
/// Phase 0 found the same shape three more times (`Reactor_PFR`, `HeatExchanger`, `Cooler`), which
/// is why this is a mechanism rather than a special case.
/// </summary>
[Trait("Category", "CalcMode")]
public class GatedValueTests
{
    private static string ValveDoc(string parameters) => $$"""
    {
      "schemaVersion": 1, "name": "200 gated value", "compounds": ["Nitrogen"], "propertyPackage": "PR",
      "objects": [
        { "tag": "IN1", "kind": "materialStream",
          "spec": { "temperature": { "value": 80, "unit": "C" }, "pressure": { "value": 8, "unit": "bar" },
                    "massFlow": { "value": 1000, "unit": "kg/h" },
                    "composition": { "basis": "molar", "fractions": { "Nitrogen": 1 } } } },
        { "tag": "V-1", "kind": "unitOp", "type": "valve", "parameters": {{parameters}} },
        { "tag": "OUT", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "IN1", "to": "V-1", "port": "Inlet" },
        { "from": "V-1", "to": "OUT", "port": "Outlet" }
      ]
    }
    """;

    private static async Task<double> OutletPressure(string parameters)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            new StringContent($"{{\"document\":{ValveDoc(parameters)}}}", Encoding.UTF8, "application/json"));
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return r.GetProperty("streams").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "OUT").GetProperty("pressureBar").GetDouble();
    }

    [SkippableFact]
    public async Task A_valve_opening_moves_the_answer_once_its_gate_is_set()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // 199 measured these identical at 7.987199 bar under every Kv mode. The gate is the
        // difference, and this is the assertion that was impossible to make before it existed.
        var open20 = await OutletPressure("""{"calcMode":"kvGeneral","kv":100,"openingPct":20}""");
        var open80 = await OutletPressure("""{"calcMode":"kvGeneral","kv":100,"openingPct":80}""");

        Assert.NotEqual(open20, open80);
        // A more open valve restricts less, so it drops less pressure. Direction is physics, and
        // asserting it stops a gate that "works" by scrambling the answer from passing.
        Assert.True(open80 > open20,
            $"an 80% open valve must drop less than a 20% one — got {open80} bar vs {open20} bar");
    }
}
