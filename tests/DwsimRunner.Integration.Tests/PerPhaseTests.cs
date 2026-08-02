// 120-runner-dwsim-parity US1 (T005) — Tier B: per-phase stream results against real DWSIM.
//
// The bug these tests were written against: HarvestStream reported
//   Phase: ms.Phases[0].Properties.molarfraction == 1 ? "vapor" : null
// where Phases[0] is DWSIM's MIXTURE phase, whose molar fraction is 1.0 by definition —
// so the label was noise in both directions (liquid water at 25 C came back "vapor" under
// STEAM, and absent under PR). Written first and observed failing (Constitution IX).
//
// Assertions are driven by PHYSICS (liquid water at 25 C is liquid), never by a phase
// index — trusting an index is the original bug (specs/036-runner-fidelity/research.md:390).
// On failure, the full stream row is serialized into the assertion message so a red run
// doubles as the Phases-dictionary diagnostic dump research R1 calls for.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "PerPhase")]
public class PerPhaseTests
{
    private static string Doc(string unitParams) => $$"""
    {
      "schemaVersion": 1,
      "name": "per-phase integration",
      "compounds": ["Water"],
      "propertyPackage": "STEAM",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 298.15, "unit": "K" },
                    "pressure": { "value": 101325, "unit": "Pa" },
                    "massFlow": { "value": 1.0, "unit": "kg/s" },
                    "composition": { "basis": "mass", "fractions": { "Water": 1.0 } } } },
        { "tag": "U-1", "kind": "unitOp", "type": "heater",
          "parameters": { {{unitParams}} } },
        { "tag": "PROD", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "U-1", "port": "Inlet" },
        { "from": "U-1", "to": "PROD", "port": "Outlet" }
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

    private static JsonElement Stream(JsonElement r, string name) =>
        r.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("name").GetString() == name);

    /// The diagnostic dump: any failed expectation prints the whole row, so a red run
    /// shows exactly what the engine's Phases dictionary yielded.
    private static string Dump(JsonElement row) => JsonSerializer.Serialize(row);

    private static double VaporFraction(JsonElement row)
    {
        Assert.True(row.TryGetProperty("vaporFraction", out var vf),
            "no vaporFraction on row: " + Dump(row));
        return vf.GetDouble();
    }

    private static List<JsonElement> Phases(JsonElement row)
    {
        Assert.True(row.TryGetProperty("phases", out var ph),
            "no phases[] on row: " + Dump(row));
        return ph.EnumerateArray().ToList();
    }

    // (a) Liquid water at 25 C / 1 atm is LIQUID. Under STEAM the tautology said "vapor".
    [SkippableFact]
    public async Task Liquid_water_at_25C_reports_liquid_with_zero_vapor_fraction()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // no-op heater: outlet = inlet state, but the stream is downstream-computed
        var r = await Solve(Doc("""
            "outletTemperature": { "value": 298.15, "unit": "K" }
            """));
        var prod = Stream(r, "PROD");

        Assert.True(prod.GetProperty("phase").GetString() == "liquid",
            "phase should be liquid: " + Dump(prod));
        Assert.InRange(VaporFraction(prod), 0.0, 0.0001);

        var phases = Phases(prod);
        var block = Assert.Single(phases);                       // exactly one phase present
        Assert.Equal("liquid", block.GetProperty("name").GetString());
        Assert.InRange(block.GetProperty("moleFraction").GetDouble(), 0.9999, 1.0001);
        // per-phase property: liquid water density ~997 kg/m3
        Assert.True(block.TryGetProperty("densityKgM3", out var rho),
            "liquid block carries no densityKgM3: " + Dump(prod));
        Assert.InRange(rho.GetDouble(), 950, 1050);
    }

    // (b) Steam at 150 C / 1 atm is VAPOR.
    [SkippableFact]
    public async Task Steam_at_150C_reports_vapor_with_unit_vapor_fraction()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc("""
            "outletTemperature": { "value": 423.15, "unit": "K" }
            """));
        var prod = Stream(r, "PROD");

        Assert.True(prod.GetProperty("phase").GetString() == "vapor",
            "phase should be vapor: " + Dump(prod));
        Assert.InRange(VaporFraction(prod), 0.9999, 1.0001);

        var phases = Phases(prod);
        var block = Assert.Single(phases);
        Assert.Equal("vapor", block.GetProperty("name").GetString());
        Assert.InRange(block.GetProperty("moleFraction").GetDouble(), 0.9999, 1.0001);
    }

    // (c) Partial vaporization: 1 kg/s water from 25 C at 1 atm given 1000 kW.
    // Sensible to 100 C ~ 314 kW, full latent ~ 2256 kW => vf ~ (1000-314)/2256 ~ 0.30.
    // Wide tolerance: the point is TWO phases, fractions summing to ~1, vf strictly inside (0,1).
    [SkippableFact]
    public async Task Partially_vaporized_water_reports_two_phase_with_both_blocks()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc("""
            "heatDuty": { "value": 1000, "unit": "kW" }
            """));
        var prod = Stream(r, "PROD");

        Assert.True(prod.GetProperty("phase").GetString() == "two-phase",
            "phase should be two-phase: " + Dump(prod));
        Assert.InRange(VaporFraction(prod), 0.1, 0.6);

        var phases = Phases(prod);
        Assert.Equal(2, phases.Count);
        var names = phases.Select(p => p.GetProperty("name").GetString()).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "liquid", "vapor" }, names);
        var sum = phases.Sum(p => p.GetProperty("moleFraction").GetDouble());
        Assert.InRange(sum, 0.99, 1.01);
        // vaporFraction must agree with the vapor block's mole fraction
        var vaporBlock = phases.First(p => p.GetProperty("name").GetString() == "vapor");
        Assert.InRange(Math.Abs(vaporBlock.GetProperty("moleFraction").GetDouble() - VaporFraction(prod)),
            0, 0.01);
    }
}
