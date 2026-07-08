// 005-unitop-parameter-application — Tier B: heater/cooler parameter
// application against real DWSIM. The CalcMode bug: setting outletTemperature
// only set the engine's m_Tout; CalcMode stayed at its heat-duty default, so
// the setpoint was silently ignored and the stream passed through unchanged.
// These tests were written first and observed failing (spec 005, Constitution IX).

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "HeaterCooler")]
public class HeaterCoolerTests
{
    // Water at 1 atm heated/cooled between 25 °C and 80 °C: ~230 kW per kg/s.
    private static string Doc(string feedSpec, string unitOp) => $$"""
    {
      "schemaVersion": 1,
      "name": "heater cooler integration",
      "compounds": ["Water"],
      "propertyPackage": "STEAM",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { {{feedSpec}},
                    "massFlow": { "value": 1.0, "unit": "kg/s" },
                    "composition": { "basis": "mass", "fractions": { "Water": 1.0 } } } },
        {{unitOp}},
        { "tag": "PROD", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "U-1", "port": "Inlet" },
        { "from": "U-1", "to": "PROD", "port": "Outlet" }
      ]
    }
    """;

    private const string ColdFeed = """
        "temperature": { "value": 298.15, "unit": "K" },
                    "pressure": { "value": 101325, "unit": "Pa" }
        """;
    private const string HotFeed = """
        "temperature": { "value": 353.15, "unit": "K" },
                    "pressure": { "value": 101325, "unit": "Pa" }
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

    private static JsonElement Stream(JsonElement r, string name) =>
        r.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("name").GetString() == name);

    private static double DutyKw(JsonElement r, string name)
    {
        var uo = r.GetProperty("unitOps").EnumerateArray()
                  .First(u => u.GetProperty("name").GetString() == name);
        Assert.True(uo.TryGetProperty("dutyKw", out var d), $"unit op '{name}' reports no dutyKw");
        return d.GetDouble();
    }

    // T001 / US1-AS1 / SC-001: heater reaches the outlet temperature setpoint.
    [SkippableFact]
    public async Task Heater_reaches_outletTemperature_setpoint_and_reports_duty()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc(ColdFeed, """
            { "tag": "U-1", "kind": "unitOp", "type": "heater",
              "parameters": { "outletTemperature": { "value": 353.15, "unit": "K" } } }
            """));

        Assert.InRange(Stream(r, "PROD").GetProperty("temperatureC").GetDouble(), 79.95, 80.05);
        Assert.InRange(DutyKw(r, "U-1"), 200, 260);   // ~230 kW for 1 kg/s water, 25→80 °C
    }

    // T002 / US1-AS2 / SC-002: cooler reaches the outlet temperature setpoint.
    [SkippableFact]
    public async Task Cooler_reaches_outletTemperature_setpoint_and_reports_duty()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc(HotFeed, """
            { "tag": "U-1", "kind": "unitOp", "type": "cooler",
              "parameters": { "outletTemperature": { "value": 298.15, "unit": "K" } } }
            """));

        Assert.InRange(Stream(r, "PROD").GetProperty("temperatureC").GetDouble(), 24.95, 25.05);
        Assert.InRange(DutyKw(r, "U-1"), 200, 260);   // positive = heat removed
    }

    // T003 / US1-AS3 / SC-003: the heatDuty path must not regress. Pre-fix
    // behavior (observed 2026-07-08): 230.1 kW heats 1 kg/s water 25 → 80.009 °C.
    [SkippableFact]
    public async Task Heater_heatDuty_path_is_unchanged()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc(ColdFeed, """
            { "tag": "U-1", "kind": "unitOp", "type": "heater",
              "parameters": { "heatDuty": { "value": 230.1, "unit": "kW" } } }
            """));

        Assert.InRange(Stream(r, "PROD").GetProperty("temperatureC").GetDouble(), 79.9, 80.1);
        Assert.InRange(DutyKw(r, "U-1"), 230.0, 230.2);
    }

    // T004 / US2-AS1 / FR-FIX-006: pressureDrop is applied in OutletTemperature
    // mode. 101325 − 5000 = 96325 Pa = 0.96325 bar (streams report bar, 3 dp).
    [SkippableFact]
    public async Task Heater_applies_pressureDrop()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc(ColdFeed, """
            { "tag": "U-1", "kind": "unitOp", "type": "heater",
              "parameters": { "outletTemperature": { "value": 353.15, "unit": "K" },
                              "pressureDrop": { "value": 5000, "unit": "Pa" } } }
            """));

        Assert.InRange(Stream(r, "PROD").GetProperty("temperatureC").GetDouble(), 79.95, 80.05);
        Assert.InRange(Stream(r, "PROD").GetProperty("pressureBar").GetDouble(), 0.962, 0.964);
    }

    // T005 / US2-AS2 / FR-FIX-007: efficiency scales the reported input duty.
    // Engine semantic (confirmed against 9.0.5.0): m_eta is a percent (default
    // 100); the fluid receives eta/100 of the input duty, so in
    // OutletTemperature mode dutyKw = duty_fluid / (eta/100). The document
    // convention is a 0–1 fraction, normalized ×100 by the runner. Here:
    // duty_fluid ≈ 230 kW, so reported duty ≈ 230 / 0.8 ≈ 288 kW.
    [SkippableFact]
    public async Task Heater_efficiency_scales_reported_duty()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc(ColdFeed, """
            { "tag": "U-1", "kind": "unitOp", "type": "heater",
              "parameters": { "outletTemperature": { "value": 353.15, "unit": "K" },
                              "efficiency": 0.8 } }
            """));

        Assert.InRange(Stream(r, "PROD").GetProperty("temperatureC").GetDouble(), 79.95, 80.05);
        Assert.InRange(DutyKw(r, "U-1"), 260, 320);   // ~230 / 0.8 ≈ 288 kW
    }
}
