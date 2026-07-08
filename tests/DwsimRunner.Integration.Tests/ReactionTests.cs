// T029 — US1 Tier B: reactor documents with reaction sets build and solve,
// parameterized over the three primary reaction types (conversion,
// equilibrium, kinetic). Heterogeneous-catalytic is deliberately out (no
// template fits; optional per tasks.md). Assertions stay thermodynamic-light:
// convergence + mass-balance closure + product formation where deterministic.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Reaction")]
public class ReactionTests
{
    // CO + 2 H2 → CH3OH over a conversion reactor (80 % of CO).
    private const string ConversionDoc = """
    {
      "schemaVersion": 1,
      "name": "conversion reactor integration",
      "compounds": ["Carbon monoxide", "Hydrogen", "Methanol"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 200, "unit": "C" },
                    "pressure": { "value": 50, "unit": "bar" },
                    "molarFlow": { "value": 100, "unit": "kmol/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Carbon monoxide": 0.3333, "Hydrogen": 0.6667 } } } },
        { "tag": "R-1", "kind": "unitOp", "type": "reactorConversion" },
        { "tag": "OUT_V", "kind": "materialStream" },
        { "tag": "OUT_L", "kind": "materialStream" },
        { "tag": "Q-RX", "kind": "energyStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "R-1", "port": "Inlet" },
        { "from": "R-1", "to": "OUT_V", "port": "Vapor Outlet" },
        { "from": "R-1", "to": "OUT_L", "port": "Liquid Outlet" },
        { "from": "Q-RX", "to": "R-1", "port": "Energy Inlet" }
      ],
      "reactions": [
        { "tag": "RX-1", "type": "conversion", "basis": "molar",
          "stoichiometry": { "Methanol": 1, "Carbon monoxide": -1, "Hydrogen": -2 },
          "baseCompound": "Carbon monoxide", "conversionExpression": "80" }
      ],
      "reactionSets": [
        { "tag": "RS-1", "reactions": ["RX-1"], "attachTo": ["R-1"] }
      ]
    }
    """;

    // Water-gas shift equilibrium: CO + H2O ⇌ CO2 + H2 (K from Gibbs energy).
    private const string EquilibriumDoc = """
    {
      "schemaVersion": 1,
      "name": "equilibrium reactor integration",
      "compounds": ["Carbon monoxide", "Water", "Carbon dioxide", "Hydrogen"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 350, "unit": "C" },
                    "pressure": { "value": 10, "unit": "bar" },
                    "molarFlow": { "value": 100, "unit": "kmol/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Carbon monoxide": 0.5, "Water": 0.5 } } } },
        { "tag": "R-1", "kind": "unitOp", "type": "reactorEquilibrium",
          "parameters": { "outletTemperature": { "value": 350, "unit": "C" } } },
        { "tag": "OUT_V", "kind": "materialStream" },
        { "tag": "OUT_L", "kind": "materialStream" },
        { "tag": "Q-RX", "kind": "energyStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "R-1", "port": "Inlet" },
        { "from": "R-1", "to": "OUT_V", "port": "Vapor Outlet" },
        { "from": "R-1", "to": "OUT_L", "port": "Liquid Outlet" },
        { "from": "Q-RX", "to": "R-1", "port": "Energy Inlet" }
      ],
      "reactions": [
        { "tag": "RX-1", "type": "equilibrium", "basis": "molar", "phase": "Vapor",
          "stoichiometry": { "Carbon dioxide": 1, "Hydrogen": 1, "Carbon monoxide": -1, "Water": -1 },
          "baseCompound": "Carbon monoxide", "equilibriumConstantSource": "Gibbs Energy",
          "temperature": 623.15 }
      ],
      "reactionSets": [
        { "tag": "RS-1", "reactions": ["RX-1"], "attachTo": ["R-1"] }
      ]
    }
    """;

    // Kinetic CO + 2 H2 → CH3OH in a CSTR with an Arrhenius rate.
    private const string KineticDoc = """
    {
      "schemaVersion": 1,
      "name": "kinetic reactor integration",
      "compounds": ["Carbon monoxide", "Hydrogen", "Methanol"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 250, "unit": "C" },
                    "pressure": { "value": 50, "unit": "bar" },
                    "molarFlow": { "value": 100, "unit": "kmol/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Carbon monoxide": 0.3333, "Hydrogen": 0.6667 } } } },
        { "tag": "R-1", "kind": "unitOp", "type": "reactorCSTR",
          "parameters": { "volume": { "value": 5, "unit": "m3" },
                          "headspace": { "value": 5, "unit": "m3" },
                          "outletTemperature": { "value": 250, "unit": "C" } } },
        { "tag": "OUT", "kind": "materialStream" },
        { "tag": "Q-RX", "kind": "energyStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "R-1", "port": "Inlet" },
        { "from": "R-1", "to": "OUT", "port": "Outlet" },
        { "from": "Q-RX", "to": "R-1", "port": "Energy Inlet" }
      ],
      "reactions": [
        { "tag": "RX-1", "type": "kinetic", "basis": "molarConcentration", "phase": "Vapor",
          "stoichiometry": { "Methanol": 1, "Carbon monoxide": -1, "Hydrogen": -2 },
          "baseCompound": "Carbon monoxide", "a": 0.5, "e": 0 }
      ],
      "reactionSets": [
        { "tag": "RS-1", "reactions": ["RX-1"], "attachTo": ["R-1"] }
      ]
    }
    """;

    public static TheoryData<string, string, string[]> Reactors => new()
    {
        { "conversion", ConversionDoc, new[] { "OUT_V", "OUT_L" } },
        { "equilibrium", EquilibriumDoc, new[] { "OUT_V", "OUT_L" } },
        { "kinetic", KineticDoc, new[] { "OUT" } },
    };

    [SkippableTheory]
    [MemberData(nameof(Reactors))]
    public async Task Reactor_document_with_reaction_set_builds_and_solves(
        string reactionType, string doc, string[] outletTags)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(doc, timeoutSeconds: 180));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(r.GetProperty("converged").GetBoolean(),
            $"{reactionType} reactor did not converge: {r.GetProperty("warnings")}");

        var feed = BuildSolveTests.MassFlow(r, "FEED");
        var outMass = outletTags.Sum(tag => BuildSolveTests.MassFlow(r, tag));
        Assert.InRange(outMass / feed, 0.99, 1.01);   // reaction conserves mass

        // Conversion (80 % of CO) and isothermal WGS equilibrium (K ≈ 20 at
        // 623 K) both must form product; kinetic extent depends on residence
        // time, so only mass balance is pinned there.
        var expectedProduct = reactionType switch
        {
            "conversion" => "Methanol",
            "equilibrium" => "Carbon dioxide",
            _ => null,
        };
        if (expectedProduct is not null)
        {
            var product = r.GetProperty("streams").EnumerateArray()
                .Where(s => outletTags.Contains(s.GetProperty("name").GetString()))
                .SelectMany(s => s.TryGetProperty("compositionMol", out var c) && c.ValueKind == JsonValueKind.Object
                    ? c.EnumerateObject().ToArray() : [])
                .Where(p => p.Name == expectedProduct)
                .Sum(p => p.Value.GetDouble());
            Assert.True(product > 0.05, $"expected {expectedProduct} in reactor outlet, composition sum was {product}");
        }
    }
}
