// dwsim-runner Worker — GPL-3.0
// Spec 090 (tutorial 6, F27) — heat-exchanger parameter application against real DWSIM.
//
// TWO bugs, both of the same shape as the heater/cooler CalcMode bug these tests are modelled on,
// and both of them had been MEASURED BEFORE and written off as unfixable (spec 036, attempted and
// reverted; `KNOWN_GAPS` carried its residue):
//
//   1. `hotSideOutletTemperature` / `coldSideOutletTemperature` were silently ignored. The engine
//      constructs in `CalcBothTemp_UA`, where both outlet temperatures are OUTPUTS, so a document
//      asking for a specific outlet temperature came back converged, plausible and unaffected —
//      byte-identical to sending no parameters at all. 036 concluded the setpoint "never reaches
//      Calculate"; it does, as soon as `CalculationMode` names the right mode.
//
//   2. `overallUA` was neither a UA nor a power. DWSIM's `OverallCoefficient` is U in W/[m2.K] and
//      `Calculate` uses U × `Area`; the parameter is now `overallHeatTransferCoefficient` plus
//      `area`, under a `heatTransferCoefficient` unitType.
//
// The reference case is DWSIM tutorial 6 with counter-flow NTU theory as the independent check:
// hot water 1 kg/s at 400 K / 3 atm against cold water 2 kg/s at 300 K / 1 atm. With UA = 2500 W/K,
// C_min = 4250 W/K, Cr = 0.506, NTU = 0.588 → effectiveness 0.406 → Q = 172.4 kW, hot out 359.4 K,
// cold out 320.5 K. Every assertion below is against that, not against a captured output — a test
// that only remembers what the code did cannot tell you the code was wrong.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "HeatExchanger")]
public class HeatExchangerTests
{
    private static string Doc(string parameters) => $$"""
    {
      "schemaVersion": 1,
      "name": "heat exchanger integration",
      "compounds": ["Water"],
      "propertyPackage": "STEAM",
      "objects": [
        { "tag": "HOTIN", "kind": "materialStream",
          "spec": { "temperature": { "value": 400.0, "unit": "K" },
                    "pressure": { "value": 304000, "unit": "Pa" },
                    "massFlow": { "value": 1.0, "unit": "kg/s" },
                    "composition": { "basis": "mass", "fractions": { "Water": 1.0 } } } },
        { "tag": "COLDIN", "kind": "materialStream",
          "spec": { "temperature": { "value": 300.0, "unit": "K" },
                    "pressure": { "value": 101325, "unit": "Pa" },
                    "massFlow": { "value": 2.0, "unit": "kg/s" },
                    "composition": { "basis": "mass", "fractions": { "Water": 1.0 } } } },
        { "tag": "HX", "kind": "unitOp", "type": "heatExchanger", "parameters": {{{parameters}}} },
        { "tag": "HOTOUT", "kind": "materialStream" },
        { "tag": "COLDOUT", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "HOTIN", "to": "HX", "port": "Inlet 1" },
        { "from": "COLDIN", "to": "HX", "port": "Inlet 2" },
        { "from": "HX", "to": "HOTOUT", "port": "Outlet 1" },
        { "from": "HX", "to": "COLDOUT", "port": "Outlet 2" }
      ]
    }
    """;

    private static async Task<JsonElement> Solve(string parameters)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(Doc(parameters)));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(r.GetProperty("converged").GetBoolean(),
            "did not converge: " + r.GetProperty("warnings"));
        return r;
    }

    private static double TempC(JsonElement r, string name) =>
        r.GetProperty("streams").EnumerateArray()
         .First(s => s.GetProperty("name").GetString() == name)
         .GetProperty("temperatureC").GetDouble();

    private static double DutyKw(JsonElement r) =>
        r.GetProperty("unitOps").EnumerateArray()
         .First(u => u.GetProperty("name").GetString() == "HX")
         .GetProperty("dutyKw").GetDouble();

    /// The UA case, checked against counter-flow NTU theory rather than against itself.
    [SkippableFact]
    public async Task Coefficient_and_area_reproduce_counter_flow_NTU_theory()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve("""
            "overallHeatTransferCoefficient": { "value": 2500.0, "unit": "W/[m2.K]" },
            "area": { "value": 1.0, "unit": "m2" }
            """);

        Assert.InRange(DutyKw(r), 165.0, 180.0);          // theory 172.4 kW
        Assert.InRange(TempC(r, "HOTOUT"), 84.0, 88.0);   // theory 359.4 K = 86.25 C
        Assert.InRange(TempC(r, "COLDOUT"), 45.0, 49.0);  // theory 320.5 K = 47.35 C
    }

    /// UA = U x Area, so doubling the area moves the answer. This is the assertion that would have
    /// caught the old `overallUA` name: under that name a caller believes area is irrelevant.
    [SkippableFact]
    public async Task Area_participates_in_the_duty()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var single = DutyKw(await Solve("""
            "overallHeatTransferCoefficient": { "value": 2500.0, "unit": "W/[m2.K]" },
            "area": { "value": 1.0, "unit": "m2" }
            """));
        var doubled = DutyKw(await Solve("""
            "overallHeatTransferCoefficient": { "value": 2500.0, "unit": "W/[m2.K]" },
            "area": { "value": 2.0, "unit": "m2" }
            """));

        Assert.True(doubled > single + 50.0,
            $"doubling the area moved the duty by {doubled - single:F1} kW — Area is not reaching Calculate");
    }

    /// The hot-side setpoint is REACHED, not merely accepted. Pre-fix this returned 106.85 C.
    [SkippableFact]
    public async Task HotSideOutletTemperature_setpoint_is_reached()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve("""
            "hotSideOutletTemperature": { "value": 359.4, "unit": "K" }
            """);

        Assert.InRange(TempC(r, "HOTOUT"), 86.2, 86.3);   // 359.4 K = 86.25 C
    }

    /// The cold-side setpoint likewise — and this is the pair that proves the mode mapping is not
    /// backwards. `CalcTempColdOut` names the temperature the engine SOLVES FOR, so specifying the
    /// hot outlet selects it; one test alone cannot distinguish that from the opposite convention.
    [SkippableFact]
    public async Task ColdSideOutletTemperature_setpoint_is_reached()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve("""
            "coldSideOutletTemperature": { "value": 320.6, "unit": "K" }
            """);

        Assert.InRange(TempC(r, "COLDOUT"), 47.4, 47.5);  // 320.6 K = 47.45 C
    }

    /// A setpoint must MOVE the answer away from the unparameterised default, or "reached" is
    /// indistinguishable from "the default happened to land there". The default is 106.85 C hot out,
    /// which is why the assertion above picks a target far from it.
    [SkippableFact]
    public async Task An_unparameterised_exchanger_does_not_already_sit_at_the_setpoint()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve("");

        Assert.True(Math.Abs(TempC(r, "HOTOUT") - 86.25) > 5.0,
            $"the default hot outlet is {TempC(r, "HOTOUT"):F2} C, too close to the setpoint the " +
            "other tests assert — those tests can no longer tell a honoured setpoint from a coincidence");
    }

    /// Both setpoints at once specifies the duty twice, and is refused rather than resolved.
    /// DWSIM's `CalcBothTemp` is the trap this guards: it converges with duty 0 and both outlets
    /// sitting at their inlet temperatures, so "accepted" and "the exchanger is switched off" look
    /// identical to anything reading only `converged`.
    [SkippableFact]
    public async Task Both_outlet_temperature_setpoints_at_once_is_refused()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(Doc("""
                "hotSideOutletTemperature": { "value": 359.4, "unit": "K" },
                "coldSideOutletTemperature": { "value": 320.6, "unit": "K" }
                """)));

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("specifies the duty twice", body);
        // The refusal has to say what to do instead, or the caller's next move is another guess.
        Assert.Contains("overallHeatTransferCoefficient", body);
    }

    /// The honest unit is ACCEPTED and a wrong one is REFUSED. `W/K` used to be rejected as an
    /// unknown *power* unit while `W` was accepted and silently divided the value by 1000 — so the
    /// only reachable configuration was the one that said nothing about dimension.
    [SkippableFact]
    public async Task A_coefficient_unit_that_is_not_a_coefficient_is_refused()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(Doc("""
                "overallHeatTransferCoefficient": { "value": 2500.0, "unit": "W/K" }
                """)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_UNIT", body);
        // The refusal must name what IS accepted: a bare "unknown unit" sends the caller guessing,
        // and the whole reason this parameter went unused is that nobody could tell what it wanted.
        Assert.Contains("W/[m2.K]", body);
    }
}
