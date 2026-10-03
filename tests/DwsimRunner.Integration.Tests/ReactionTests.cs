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

    // ── ISKS-176 round 2 — an unevaluable `lnKeq` is refused, not silently run as Keq = 1 ────────
    //
    // `2 H2 + O2 -> 2 H2O` at 250 °C, where Gibbs gives ln K ≈ +230 and the reaction runs to
    // completion. Measured before the guard: `equilibriumConstantSource` of 'TOTAL_GARBAGE', '' or
    // 'lnK' each CONVERGED at X(H2) = 34.2 %, duty = -1.1 kW, **warnings = []** — byte-identical to
    // a stated constant Keq of 1, because the engine evaluates an expression it cannot parse as
    // ln(Keq) = 0. Mass balance closed and the duty matched the (wrong) extent, so nothing in the
    // response said the stated chemistry had been discarded.
    //
    // O2-rich on purpose: at EXACTLY 2:1 the true equilibrium is complete conversion, a boundary the
    // engine's solve cannot reach ("Solution led to negative mole fractions"). A diverged leg would
    // not be able to tell a refusal from a failure, so the fixture sits where the correct answer
    // converges.
    private const string KeqDoc = """
    {
      "schemaVersion": 1,
      "name": "equilibrium Keq expression guard",
      "compounds": ["Hydrogen", "Oxygen", "Water"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 20, "unit": "C" },
                    "pressure": { "value": 1, "unit": "atm" },
                    "massFlow": { "value": 1, "unit": "kg/h" },
                    "composition": { "basis": "molar", "fractions": { "Hydrogen": 0.65, "Oxygen": 0.35 } } } },
        { "tag": "R-1", "kind": "unitOp", "type": "reactorEquilibrium",
          "parameters": { "outletTemperature": { "value": 250, "unit": "C" },
                          "pressureDrop": { "value": 0.1, "unit": "bar" } } },
        { "tag": "OUT_V", "kind": "materialStream" },
        { "tag": "OUT_L", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "R-1", "port": "Inlet" },
        { "from": "R-1", "to": "OUT_V", "port": "Vapor Outlet" },
        { "from": "R-1", "to": "OUT_L", "port": "Liquid Outlet" }
      ],
      "reactions": [
        { "tag": "RX-1", "type": "equilibrium", "basis": "molar", "phase": "Vapor",
          "stoichiometry": { "Water": 2, "Hydrogen": -2, "Oxygen": -1 },
          "baseCompound": "Water", "equilibriumConstantSource": "%%KEQ%%" }
      ],
      "reactionSets": [ { "tag": "RS-1", "reactions": ["RX-1"], "attachTo": ["R-1"] } ]
    }
    """;

    private static string KeqWith(string source) => KeqDoc.Replace("%%KEQ%%", source);

    [SkippableTheory]
    // The three measured silent-K=1 strings.
    [InlineData("TOTAL_GARBAGE")]
    [InlineData("")]
    [InlineData("lnK")]
    // `T` is the only variable and it is CASE-SENSITIVE: each of these was measured producing the
    // same 34.2 % K=1 result, so each has to refuse too.
    [InlineData("t")]
    [InlineData("P")]
    [InlineData("4577.8/Temperature")]
    public async Task An_unevaluable_Keq_expression_is_refused_rather_than_run_as_one(string source)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(KeqWith(source), timeoutSeconds: 180));

        // 422, not a converged 200 carrying a wrong extent.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var issue = r.GetProperty("issues").EnumerateArray()
            .Single(i => i.GetProperty("code").GetString() == "REACTION_KEQ_INVALID");
        Assert.Equal("RX-1", issue.GetProperty("tag").GetString());
        // The message says what to write instead. What it replaces is a plausible wrong number, so
        // the author has no other signal that the chemistry was discarded.
        Assert.Contains("Gibbs Energy", issue.GetProperty("message").GetString()!);
    }

    [SkippableTheory]
    // The other half, and the one that matters more: the guard must not refuse what the engine runs.
    // Every string here was measured solving to a conversion OTHER than the 34.2 % K=1 fingerprint.
    [InlineData("Gibbs Energy", 99.0)]           // the default; runs to completion
    [InlineData("4577.8/T - 4.33", 70.0)]        // a plain correlation in T
    [InlineData("-5.4+3465/(T*1.8)", 10.0)]      // DWSIM's OWN built-in form, verbatim
    [InlineData("T^2", 95.0)]                    // `^` is exponentiation
    [InlineData("exp(1)", 55.0)]                 // unqualified Math, which IS case-insensitive
    [InlineData("log(10)", 50.0)]
    [InlineData("sqrt(4)", 50.0)]
    [InlineData("0.46", 20.0)]                   // a bare number is a CONSTANT Keq, not an expression
    public async Task A_valid_Keq_still_solves(string source, double minConversionPct)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(KeqWith(source), timeoutSeconds: 180));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(r.GetProperty("converged").GetBoolean(),
            $"Keq '{source}' did not converge: {r.GetProperty("warnings")}");

        // Mass closes, and the reaction actually ran: a floor per expression, so a guard that
        // quietly turned every one of these into Keq = 1 would fail here rather than pass.
        var outMass = BuildSolveTests.MassFlow(r, "OUT_V") + BuildSolveTests.MassFlow(r, "OUT_L");
        Assert.InRange(outMass / BuildSolveTests.MassFlow(r, "FEED"), 0.99, 1.01);
        Assert.True(Conversion(r) >= minConversionPct,
            $"Keq '{source}': X(H2) was {Conversion(r):F1} %, expected at least {minConversionPct} %");
    }

    /// <summary>H2 conversion across the reactor, in percent, from the reported molar flows.</summary>
    private static double Conversion(JsonElement r)
    {
        static double Moles(JsonElement r, string tag, string compound)
        {
            var s = r.GetProperty("streams").EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == tag);
            if (!s.TryGetProperty("molarFlowKmolH", out var f) || f.ValueKind != JsonValueKind.Number) return 0;
            if (!s.TryGetProperty("compositionMol", out var c) || c.ValueKind != JsonValueKind.Object) return 0;
            return c.TryGetProperty(compound, out var x) ? f.GetDouble() * 1000 * x.GetDouble() : 0;
        }
        var inH2 = Moles(r, "FEED", "Hydrogen");
        var outH2 = Moles(r, "OUT_V", "Hydrogen") + Moles(r, "OUT_L", "Hydrogen");
        return inH2 > 0 ? 100 * (inH2 - outH2) / inH2 : 0;
    }
}
