// 120-runner-dwsim-parity US4 (T019) — Tier B: kinetic and heterogeneous-catalytic reactions
// on the measured carrier, reactorPFR (reactorCSTR is measured non-converging).
//
// Context: FlowsheetBuilder has constructed kinetic reactions since 099 — the PFR capability
// probe runs a kinetic WGS and measured solvable:"yes" — but no test PINNED convergence or
// mass balance, and the app schema still refuses `kinetic`. Het-cat construction exists too
// but hands the engine EMPTY rate expressions ("",""), the exact converges-but-moves-nothing
// risk this stack calls the confident-wrong-answer shape. These tests measure both; a failure
// here is a verdict for the fixture, not necessarily a bug to fix.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "KineticReactions")]
public class KineticReactionTests
{
    // Water-gas shift: CO + H2O -> CO2 + H2 at 350 C / 10 atm on a PFR (5 m3, 3 m).
    // Mirrors the 099 capability probe recipe (probes.ts KINETIC_WGS + reactorPFR).
    private static string Doc(string reactionJson) => $$"""
    {
      "schemaVersion": 1,
      "name": "kinetic reaction integration",
      "compounds": ["Carbon monoxide", "Water", "Carbon dioxide", "Hydrogen"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 350, "unit": "C" },
                    "pressure": { "value": 10, "unit": "atm" },
                    "molarFlow": { "value": 100, "unit": "mol/s" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Carbon monoxide": 0.4, "Water": 0.4,
                                                    "Carbon dioxide": 0.1, "Hydrogen": 0.1 } } } },
        { "tag": "R-1", "kind": "unitOp", "type": "reactorPFR",
          "parameters": { "volume": { "value": 5, "unit": "m3" }, "length": { "value": 3, "unit": "m" } } },
        { "tag": "OUT", "kind": "materialStream" },
        { "tag": "Q-RX", "kind": "energyStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "R-1", "port": "Inlet" },
        { "from": "R-1", "to": "OUT", "port": "Outlet" },
        { "from": "Q-RX", "to": "R-1", "port": "Energy Inlet" }
      ],
      "reactions": [ {{reactionJson}} ],
      "reactionSets": [ { "tag": "RS-1", "reactions": ["RX-1"], "attachTo": ["R-1"] } ]
    }
    """;

    private const string KineticWgs = """
        { "tag": "RX-1", "type": "kinetic", "basis": "molarConcentration", "phase": "Vapor",
          "stoichiometry": { "Carbon monoxide": -1, "Water": -1, "Carbon dioxide": 1, "Hydrogen": 1 },
          "baseCompound": "Carbon monoxide",
          "A": 1e6, "E": 50000,
          "directOrders": { "Carbon monoxide": 1, "Water": 1 },
          "reverseOrders": {} }
        """;

    private const string HetCatWgs = """
        { "tag": "RX-1", "type": "heterogeneousCatalytic", "basis": "molarConcentration", "phase": "Vapor",
          "stoichiometry": { "Carbon monoxide": -1, "Water": -1, "Carbon dioxide": 1, "Hydrogen": 1 },
          "baseCompound": "Carbon monoxide" }
        """;

    private static async Task<JsonElement> Solve(string doc)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(doc));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static JsonElement Stream(JsonElement r, string name) =>
        r.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("name").GetString() == name);

    private static double MolFrac(JsonElement stream, string compound) =>
        stream.GetProperty("compositionMol").TryGetProperty(compound, out var v) ? v.GetDouble() : 0.0;

    [SkippableFact]
    public async Task Kinetic_WGS_on_a_PFR_converges_consumes_CO_and_conserves_mass()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc(KineticWgs));
        Assert.True(r.GetProperty("converged").GetBoolean(),
            "kinetic WGS did not converge: " + r.GetProperty("warnings"));

        var feed = Stream(r, "FEED");
        var outp = Stream(r, "OUT");

        // The reaction RAN: CO consumed, H2 produced.
        Assert.True(MolFrac(outp, "Carbon monoxide") < MolFrac(feed, "Carbon monoxide") - 0.01,
            $"CO did not drop: feed={MolFrac(feed, "Carbon monoxide")} out={MolFrac(outp, "Carbon monoxide")} — " +
            "a converged solve that moves nothing is the confident-wrong-answer shape");
        Assert.True(MolFrac(outp, "Hydrogen") > MolFrac(feed, "Hydrogen") + 0.01, "H2 did not rise");

        // Mass conserved (equimolar reaction: mass must balance to <0.5%).
        var mFeed = feed.GetProperty("massFlowKgH").GetDouble();
        var mOut = outp.GetProperty("massFlowKgH").GetDouble();
        Assert.InRange(Math.Abs(mOut - mFeed) / mFeed, 0, 0.005);
    }

    [SkippableFact]
    public async Task HetCat_WGS_on_a_PFR_is_measured_not_assumed()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Solve(Doc(HetCatWgs));

        // The builder constructs het-cat with EMPTY rate expressions. Two honest outcomes:
        // it fails (verdict "no"), or it converges AND moves the composition (verdict "yes").
        // A converge-and-move-nothing result must FAIL this test — that outcome is the
        // silently-inert defect class, and recording it as "yes" would be the lie.
        if (!r.GetProperty("converged").GetBoolean())
        {
            // Measured refusal — the fixture records verdict "no" with the engine's words.
            Assert.True(r.GetProperty("warnings").EnumerateArray().Any(),
                "non-convergence with no diagnostic is a worker bug, not a verdict");
            return;
        }
        var feed = Stream(r, "FEED");
        var outp = Stream(r, "OUT");
        Assert.True(MolFrac(outp, "Carbon monoxide") < MolFrac(feed, "Carbon monoxide") - 0.01,
            "het-cat converged but moved nothing (empty rate expressions) — record verdict 'no', do not expose");
    }
}
