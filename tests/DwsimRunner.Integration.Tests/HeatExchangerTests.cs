// 036-runner-fidelity FR-014..FR-017 — the heat exchanger's setpoints are OUTPUTS,
// and writing to them does nothing.
//
// This is spec 005's bug verbatim, one unit-op over, in the same method of the same
// file, unfixed for a year. FlowsheetBuilder sets CalcMode for `heater` and `cooler`
// (005's fix) and NOTHING for `heatExchanger`.
//
// Proven by IL disassembly of DWSIM.UnitOperations.dll: the HeatExchanger constructor
// does `ldc.i4.3; stfld ...HeatExchanger::CalcMode` — 3 is CalcBothTemp_UA, a mode in
// which BOTH outlet temperatures are computed FROM the overall coefficient. So the
// setpoints are not "ignored"; in the mode the engine ships in, they are RESULTS.
//
// THE TRAP, and it is why this file exists before the fix: the property is
// `CalculationMode`, NOT `CalcMode`. The heater and cooler use `CalcMode`, and the
// existing code reaches for the mode with GetProperty("CalcMode") and NULL-CHECKS it.
// A copy-paste of the 005 fix onto this type compiles, converges, warns about nothing,
// and does nothing. The fix for a silently-ignored setpoint would itself be a silently-
// ignored setpoint.
//
// Written first and observed failing (Constitution IX).

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "HeatExchanger")]
public class HeatExchangerTests
{
    // Cold side: 1000 kg/h water at 25 °C. Hot side: 2000 kg/h water at 150 °C.
    // The engine's DEFAULT answer for this rig is a cold outlet of 89.908 °C — every
    // assertion below is chosen so that the default cannot satisfy it.
    private static string Doc(string parameters) => $$"""
    {
      "schemaVersion": 1,
      "name": "heat exchanger integration",
      "compounds": ["Water"],
      "propertyPackage": "STEAM",
      "objects": [
        { "tag": "COLD_IN", "kind": "materialStream",
          "spec": { "temperature": { "value": 25, "unit": "C" },
                    "pressure": { "value": 5, "unit": "bar" },
                    "massFlow": { "value": 1000, "unit": "kg/h" },
                    "composition": { "basis": "mass", "fractions": { "Water": 1.0 } } } },
        { "tag": "HOT_IN", "kind": "materialStream",
          "spec": { "temperature": { "value": 150, "unit": "C" },
                    "pressure": { "value": 5, "unit": "bar" },
                    "massFlow": { "value": 2000, "unit": "kg/h" },
                    "composition": { "basis": "mass", "fractions": { "Water": 1.0 } } } },
        { "tag": "U-1", "kind": "unitOp", "type": "heatExchanger",
          "parameters": { {{parameters}} } },
        { "tag": "COLD_OUT", "kind": "materialStream" },
        { "tag": "HOT_OUT", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "COLD_IN", "to": "U-1", "port": "Inlet 1" },
        { "from": "HOT_IN",  "to": "U-1", "port": "Inlet 2" },
        { "from": "U-1", "to": "COLD_OUT", "port": "Outlet 1" },
        { "from": "U-1", "to": "HOT_OUT",  "port": "Outlet 2" }
      ]
    }
    """;

    private static async Task<JsonElement> Solve(string doc)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(doc));
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

    // FR-014: a cold-side setpoint is HONOURED. Two different values, deliberately —
    // the engine's default answer is 89.908 °C, so a single case near it could pass
    // against a broken build by coincidence. A test that can pass by coincidence is
    // exactly what this spec exists to delete.
    [SkippableTheory]
    [InlineData(60.0)]
    [InlineData(90.0)]
    public async Task ColdSideOutletTemperature_is_honoured(double setpoint)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc($$"""
            "coldSideOutletTemperature": { "value": {{setpoint}}, "unit": "C" }
            """));

        // Pre-fix: returns 89.908 for BOTH, byte-identical to sending nothing.
        Assert.InRange(TempC(r, "COLD_OUT"), setpoint - 0.5, setpoint + 0.5);
    }

    // FR-014: the hot side likewise.
    [SkippableTheory]
    [InlineData(60.0)]
    [InlineData(100.0)]
    public async Task HotSideOutletTemperature_is_honoured(double setpoint)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc($$"""
            "hotSideOutletTemperature": { "value": {{setpoint}}, "unit": "C" }
            """));

        Assert.InRange(TempC(r, "HOT_OUT"), setpoint - 0.5, setpoint + 0.5);
    }

    // FR-014: overallUA still works — it is the one knob that worked all along, and the
    // fix must not break it. This is the regression canary.
    [SkippableFact]
    public async Task OverallUA_still_moves_the_answer()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // NOTE the empty unit. The catalog declares overallUA's unitType as `power`,
        // which it is NOT — UA is W/K. So a correct unit string like "W/[K.m2]" is
        // REJECTED as not-a-power, and the only thing that gets through is no unit at
        // all. That is the lie FR-016 fixes at the source (UnitOpCatalog.cs).
        var low  = await Solve(Doc("""  "overallUA": { "value": 1000, "unit": "" } """));
        var high = await Solve(Doc("""  "overallUA": { "value": 50000, "unit": "" } """));

        // More UA ⇒ more heat transferred ⇒ a hotter cold outlet.
        Assert.True(TempC(high, "COLD_OUT") > TempC(low, "COLD_OUT"),
            $"UA did not move the answer: {TempC(low, "COLD_OUT")} vs {TempC(high, "COLD_OUT")}");
    }

    // FR-015: an OVER-SPECIFIED exchanger is refused, never resolved by silent
    // precedence. The engine can honour a temperature setpoint OR a UA, not both, and
    // picking one for the caller is the confident-wrong-answer this spec exists to kill.
    [SkippableFact]
    public async Task Overspecified_exchanger_is_refused()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(Doc("""
                "coldSideOutletTemperature": { "value": 60, "unit": "C" },
                "overallUA": { "value": 5000, "unit": "" }
                """)));

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_PARAMETER_VALUE", body);
    }
}
