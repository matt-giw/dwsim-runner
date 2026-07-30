// dwsim-runner Worker — GPL-3.0
// Spec 099 US1 — the water electrolyzer, against real DWSIM.
//
// `ElectrolyzerStack` carried the comment "DWSIM has no electrolyzer unit op" for a year. The engine
// has shipped `WaterElectrolyzer` since 9.0, in the DLL this repository already vendors.
//
// TWO THINGS THAT LOOK LIKE OUR BUG AND ARE NOT, both measured here so they cannot recur silently:
//
//   1. An EXTERNAL unit operation builds its own ports, via `IExternalUnitOperation.CreateConnectors()`,
//      and nothing on the headless path calls it. `AddObject` returns a perfectly good object whose
//      graphic has ZERO connectors, so the first connect fails with "Index was out of range" — which
//      reads as a wrong port index in our catalog rather than as a missing engine initialisation.
//      Constructed-but-unconnectable.
//   2. The engine takes its power on input connector 1 as an ENERGY STREAM and dereferences it
//      unconditionally, while the document's invariant is that an energy port is a parameter. The
//      runner synthesizes the stream; the document never contains one.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Electrolyzer")]
public class ElectrolyzerTests
{
    /// 400 kg/h of water at 60 C, 1 atm. 1 MW across 520 cells at 988 V stack total.
    private static string Doc(string parameters, double waterKgH = 400.0) => $$"""
    {
      "schemaVersion": 1,
      "name": "electrolyzer integration",
      "compounds": ["Water", "Hydrogen", "Oxygen"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 333.15, "unit": "K" },
                    "pressure": { "value": 101325, "unit": "Pa" },
                    "massFlow": { "value": {{waterKgH}}, "unit": "kg/h" },
                    "composition": { "basis": "molar", "fractions": { "Water": 1.0, "Hydrogen": 0.0, "Oxygen": 0.0 } } } },
        { "tag": "EL-1", "kind": "unitOp", "type": "waterElectrolyzer", "parameters": {{{parameters}}} },
        { "tag": "H2OUT", "kind": "materialStream" },
        { "tag": "O2OUT", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "EL-1", "port": "Water Inlet" },
        { "from": "EL-1", "to": "H2OUT", "port": "Hydrogen-Rich Outlet" },
        { "from": "EL-1", "to": "O2OUT", "port": "Oxygen-Rich Outlet" }
      ]
    }
    """;

    private const string Faraday = """
        "powerInput": { "value": 1000, "unit": "kW" },
        "voltage": { "value": 988, "unit": "V" },
        "cellCount": 520
        """;

    private static async Task<JsonElement> Post(string doc)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(doc, timeoutSeconds: 180));
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static JsonElement Stream(JsonElement r, string name) =>
        r.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("name").GetString() == name);

    private static double MoleFraction(JsonElement s, string compound) =>
        s.TryGetProperty("compositionMol", out var c) && c.TryGetProperty(compound, out var v)
            ? v.GetDouble() : 0.0;

    [SkippableFact]
    public async Task Electrolyzer_builds_connects_and_solves()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Post(Doc(Faraday));
        Assert.True(r.GetProperty("converged").GetBoolean(),
            "electrolyzer did not converge: " + r.GetProperty("warnings"));
    }

    /// SC-003 — the products are not transposed. Hydrogen evolves at the CATHODE.
    [SkippableFact]
    public async Task Hydrogen_leaves_the_hydrogen_port_and_oxygen_the_oxygen_port()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Post(Doc(Faraday));
        Assert.True(r.GetProperty("converged").GetBoolean());

        // The only guard against a converged, plausible, TRANSPOSED answer. Both products are gases
        // and both are mostly the right thing, so nothing else in a solve result distinguishes them.
        Assert.True(MoleFraction(Stream(r, "H2OUT"), "Hydrogen") > 0.5,
            "the hydrogen-rich outlet is not hydrogen-rich — the products are swapped");
        Assert.True(MoleFraction(Stream(r, "O2OUT"), "Oxygen") > 0.2,
            "the oxygen-rich outlet carries no oxygen — the products are swapped");
        Assert.Equal(0.0, MoleFraction(Stream(r, "O2OUT"), "Hydrogen"));
    }

    /// SC-002 — hydrogen matches Faraday's law, which also settles what `voltage` MEANS.
    [SkippableFact]
    public async Task Hydrogen_production_matches_Faradays_law_with_voltage_as_the_stack_total()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Post(Doc(Faraday));
        Assert.True(r.GetProperty("converged").GetBoolean());

        var h2 = Stream(r, "H2OUT");
        // molarFlow is not on the wire for this stream, so derive it from mass and composition —
        // which is also a stronger check, since it uses two independent reported quantities.
        var xH2 = MoleFraction(h2, "Hydrogen");
        var xH2O = MoleFraction(h2, "Water");
        var meanMW = xH2 * 2.016 + xH2O * 18.0153;                       // g/mol
        var totalKmolH = h2.GetProperty("massFlowKgH").GetDouble() * 1000.0 / meanMW / 1000.0;
        var h2KmolH = xH2 * totalKmolH;

        // I = P / V_stack; a series stack passes the same current through every cell, so
        // H2 = cells x I / 2F. THE VOLTAGE IS THE STACK TOTAL — measured ratio 0.99997.
        const double F = 96485.0;
        var current = 1000.0 * 1000.0 / 988.0;
        var expected = 520.0 * current / (2.0 * F) * 3.6;                 // kmol/h

        Assert.InRange(h2KmolH, expected * 0.98, expected * 1.02);
        // The failure this pins: reading `voltage` as PER-CELL puts the answer out by exactly the
        // cell count. A 520x error that converges is the reason FR-003 forbids settling this from
        // source, and the reason the app derives the total instead of binding CellVoltage.
        Assert.True(h2KmolH < expected * 10, "hydrogen is orders out — `voltage` is not the stack total");
    }

    /// The synthesized power stream is the runner's business, not the caller's.
    [SkippableFact]
    public async Task The_power_stream_is_invisible_in_the_result()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r = await Post(Doc(Faraday));
        var energyTags = r.GetProperty("energy").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();

        // The document contains no energy stream, so a result carrying one would have the app fold
        // back an object that does not exist on its side.
        Assert.DoesNotContain(energyTags, t => t is not null && t.Contains("POWER"));
    }

    /// Missing power is refused by name, never as a null reference from inside the engine.
    [SkippableFact]
    public async Task A_missing_powerInput_is_refused_with_a_sentence()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(Doc("""
                "voltage": { "value": 988, "unit": "V" },
                "cellCount": 520
                """)));

        var body = await resp.Content.ReadAsStringAsync();
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        // The engine's own failure here is an NRE inside Calculate. An engineer must never read it.
        Assert.DoesNotContain("Object reference not set", body);
        Assert.Contains("powerInput", body);
    }

    /// Over-powering is the engine's own message, surfaced rather than re-derived.
    [SkippableFact]
    public async Task Over_powering_the_stack_says_what_to_change()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // 1 MW against 100 kg/h consumes ~177 kg/h — more water than is fed.
        var r = await Post(Doc(Faraday, waterKgH: 100.0));

        Assert.False(r.GetProperty("converged").GetBoolean());
        var warnings = r.GetProperty("warnings").ToString();
        Assert.Contains("water", warnings, StringComparison.OrdinalIgnoreCase);
        // It names the remedy — raise the water rate or drop the power — which is worth keeping
        // verbatim rather than replacing with a message of our own.
        Assert.Contains("power", warnings, StringComparison.OrdinalIgnoreCase);
    }

    /// Solving twice gives the same answer — the reported-value-as-input trap (FR-009, SC-004).
    [SkippableFact]
    public async Task Solving_the_same_flowsheet_twice_gives_the_same_answer()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var first = await Post(Doc(Faraday));
        var second = await Post(Doc(Faraday));

        Assert.True(first.GetProperty("converged").GetBoolean());
        Assert.True(second.GetProperty("converged").GetBoolean());
        Assert.Equal(Stream(first, "H2OUT").GetProperty("massFlowKgH").GetDouble(),
                     Stream(second, "H2OUT").GetProperty("massFlowKgH").GetDouble(), 6);
    }
}
