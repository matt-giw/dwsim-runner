// dwsim-runner Worker — GPL-3.0
// Spec 099 US2 — the component separator, against real DWSIM.
//
// One engine type un-strands THREE equipment classes: ReverseOsmosisUnit, Adsorber and
// IonExchanger all draw on the canvas today and vanish from every solve, because a per-compound
// split is exactly what they are and no exposed type does one.
//
// The specification is a `Dictionary<string, ComponentSeparationSpec>` keyed by compound, which the
// generic name→property reflection every other type uses cannot express — hence a bespoke
// configurator, and hence the compound-name resolution these tests pin.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "ComponentSeparator")]
public class ComponentSeparatorTests
{
    /// Seawater: 1000 kg/h at 3.5 wt% salt — the reverse-osmosis case this type exists to serve.
    private static string Doc(string parameters, bool secondOutlet = true) => $$"""
    {
      "schemaVersion": 1,
      "name": "component separator integration",
      "compounds": ["Water", "Sodium chloride"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 298.15, "unit": "K" },
                    "pressure": { "value": 101325, "unit": "Pa" },
                    "massFlow": { "value": 1000, "unit": "kg/h" },
                    "composition": { "basis": "mass", "fractions": { "Water": 0.965, "Sodium chloride": 0.035 } } } },
        { "tag": "CS", "kind": "unitOp", "type": "componentSeparator", "parameters": {{{parameters}}} },
        { "tag": "OUT1", "kind": "materialStream" }
        {{(secondOutlet ? ", { \"tag\": \"OUT2\", \"kind\": \"materialStream\" }" : "")}}
      ],
      "connections": [
        { "from": "FEED", "to": "CS", "port": "Inlet" },
        { "from": "CS", "to": "OUT1", "port": "Outlet 1" }
        {{(secondOutlet ? ", { \"from\": \"CS\", \"to\": \"OUT2\", \"port\": \"Outlet 2\" }" : "")}}
      ]
    }
    """;

    /// 98.5 % of the water and 2 % of the salt report to Outlet 1 — an RO membrane.
    private const string RoSplit = """
        "specifiedStreamIndex": 0,
        "separationSpecs": {
          "Water": { "spec": "PercentInletMassFlow", "value": 98.5 },
          "Sodium chloride": { "spec": "PercentInletMassFlow", "value": 2.0 }
        }
        """;

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Post(string doc)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(doc, timeoutSeconds: 120));
        return (resp.StatusCode, JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private static JsonElement Stream(JsonElement r, string name) =>
        r.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("name").GetString() == name);

    private static double MassFrac(JsonElement s, string compound) =>
        s.TryGetProperty("compositionMass", out var c) && c.TryGetProperty(compound, out var v) ? v.GetDouble() : 0.0;

    [SkippableFact]
    public async Task A_stated_split_separates_as_specified()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var (status, r) = await Post(Doc(RoSplit));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(r.GetProperty("converged").GetBoolean(), "did not converge: " + r.GetProperty("warnings"));

        // Checked against the arithmetic, not against a captured output: 98.5 % of 965 kg water is
        // 950.5, plus 2 % of 35 kg salt is 0.7, so Outlet 1 carries 951.2 kg/h at 99.93 % water.
        var permeate = Stream(r, "OUT1");
        var reject = Stream(r, "OUT2");
        Assert.InRange(permeate.GetProperty("massFlowKgH").GetDouble(), 949.0, 953.0);
        Assert.InRange(MassFrac(permeate, "Water"), 0.995, 1.0);
        // And the remainder is genuinely concentrated — the whole point of the unit.
        Assert.InRange(reject.GetProperty("massFlowKgH").GetDouble(), 47.0, 51.0);
        Assert.True(MassFrac(reject, "Sodium Chloride") > 0.6,
            $"the reject is only {MassFrac(reject, "Sodium Chloride"):P1} salt — nothing was concentrated");
    }

    [SkippableFact]
    public async Task Mass_is_conserved_across_the_split()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var (_, r) = await Post(Doc(RoSplit));
        var total = Stream(r, "OUT1").GetProperty("massFlowKgH").GetDouble()
                  + Stream(r, "OUT2").GetProperty("massFlowKgH").GetDouble();
        Assert.InRange(total, 999.0, 1001.0);
    }

    /// A compound in the specification that the flowsheet does not have is REFUSED BY NAME.
    [SkippableFact]
    public async Task An_unknown_compound_is_refused_naming_it_and_the_real_ones()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var (status, r) = await Post(Doc("""
            "separationSpecs": { "Unobtainium": { "spec": "PercentInletMassFlow", "value": 50 } }
            """));

        Assert.NotEqual(HttpStatusCode.OK, status);
        var body = r.ToString();
        // A silently dropped separation spec is a CONVERGED FLOWSHEET WITH THE WRONG SPLIT, which is
        // the failure class this whole feature exists to close. So it must refuse, name the compound
        // it could not resolve, and list the ones it has.
        Assert.Contains("Unobtainium", body);
        Assert.Contains("Water", body);
        Assert.Contains("Sodium Chloride", body);
    }

    /// An unknown separation MODE is refused with the accepted list, not silently defaulted.
    [SkippableFact]
    public async Task An_unknown_separation_mode_names_the_accepted_ones()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var (status, r) = await Post(Doc("""
            "separationSpecs": { "Water": { "spec": "PercentOfSomething", "value": 50 } }
            """));

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Contains("PercentInletMassFlow", r.ToString());
    }

    /// BOTH outlets are required, because the engine dereferences the second unconditionally.
    [SkippableFact]
    public async Task A_separator_with_only_one_outlet_piped_is_refused_before_the_engine_throws()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // 099's contract made `Outlet 2` optional so a one-in-one-out ion exchanger would leave
        // nothing unpiped, calling a synthesized product stream "noise for the common case".
        // Measured: unpiped, the engine throws a NullReferenceException from inside `Calculate` —
        // the same unconditional dereference as the electrolyzer's power. So the port is required,
        // the mapper synthesizes the stream it already synthesizes for any unpiped required outlet,
        // and this asserts an engineer never reads that trace.
        var (status, r) = await Post(Doc("""
            "separationSpecs": { "Sodium chloride": { "spec": "PercentInletMassFlow", "value": 99.0 } }
            """, secondOutlet: false));

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.DoesNotContain("NullReferenceException", r.ToString());
        Assert.Contains("Outlet 2", r.ToString());
    }

    /// `SpecifiedStreamIndex` is a BYTE on the engine class.
    [SkippableFact]
    public async Task The_specified_stream_index_reaches_a_byte_typed_property()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // JSON gives Int32 and the reflection setter converted only to Int32, so this threw
        // "Object of type 'System.Int32' cannot be converted to type 'System.Byte'" — a reflection
        // message about a document the caller wrote. Byte/short/long/float are handled now.
        var (status, r) = await Post(Doc(RoSplit));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("cannot be converted to type", r.ToString());
    }
}
