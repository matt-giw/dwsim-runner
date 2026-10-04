// iskra spec 281 (ISK-534) — a compound the engine does not ship travels WITH the request.
//
// Measured before it was written (2026-10-04, DWSIM 9.0.5.0): Automation3.AvailableCompounds is a
// plain dictionary, a ConstantProperties built in memory is usable, and a second Automation3 does
// not see it. So there is no UserDB, no file, and nothing for this runner to remember.
//
// What these tests pin is the part that converges when it is wrong:
//   - the engine stores formation properties PER KILOGRAM; the wire carries kJ/mol;
//   - a unit this runner does not know is refused, never converted;
//   - a solid in a fluid phase is a refusal, not a result.
//
// TIER NOTE: not built in CI (Worker needs DWSIM at compile time). See WatchdogTests.

using System.Text.Json;
using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class CompoundDefinitionsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string IronValues = """
        "molarWeight":{"value":55.845,"unit":"g/mol"},
        "meltingPoint":{"value":1811,"unit":"K"},
        "enthalpyOfFusion":{"value":13.81,"unit":"kJ/mol"},
        "solidDensity":{"value":7874,"unit":"kg/m3"},
        "solidHeatCapacity":{"value":25.1,"unit":"J/[mol.K]"},
        "enthalpyOfFormation":{"value":0,"unit":"kJ/mol"},
        "gibbsEnergyOfFormation":{"value":0,"unit":"kJ/mol"}
        """;

    private static string Def(string name = "Iron", string kind = "solid", string values = IronValues) =>
        "{\"name\":\"" + name + "\",\"formula\":\"Fe\",\"casNumber\":\"7439-89-6\",\"kind\":\"" + kind + "\",\"values\":{" + values + "}}";

    private static (List<CompoundDefinition> Defs, List<BuildIssue> Issues) Parse(string arrayJson, params string[] compounds)
    {
        var issues = new List<BuildIssue>();
        var defs = CompoundDefinitions.Parse(J(arrayJson), compounds.Length > 0 ? compounds : ["Iron", "Water"], issues);
        return (defs, issues);
    }

    [Fact]
    public void A_complete_solid_definition_parses_with_no_issues()
    {
        var (defs, issues) = Parse($"[{Def()}]");
        Assert.Empty(issues);
        var iron = Assert.Single(defs);
        Assert.Equal("Iron", iron.Name);
        Assert.Equal("solid", iron.Kind);
        Assert.Equal(55.845, iron.Values["molarWeight"]);
        Assert.Equal(1811, iron.Values["meltingPoint"]);
    }

    [Fact]
    public void A_request_with_no_definitions_is_the_request_it_always_was()
    {
        var issues = new List<BuildIssue>();
        Assert.Empty(CompoundDefinitions.Parse(null, ["Water"], issues));
        Assert.Empty(CompoundDefinitions.Parse(J("null"), ["Water"], issues));
        Assert.Empty(issues);
    }

    [Theory]
    // a required value is missing — named
    [InlineData("meltingPoint", "COMPOUND_DEFINITION_INVALID", "meltingPoint")]
    [InlineData("gibbsEnergyOfFormation", "COMPOUND_DEFINITION_INVALID", "gibbsEnergyOfFormation")]
    public void A_missing_required_value_is_refused_by_name(string dropKey, string code, string named)
    {
        var values = string.Join(",", IronValues.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimEnd(','))
            .Where(l => !l.StartsWith($"\"{dropKey}\"")));
        var (_, issues) = Parse($"[{Def(values: values)}]");
        var issue = Assert.Single(issues);
        Assert.Equal(code, issue.Code);
        Assert.Contains("Iron", issue.Message);
        Assert.Contains(named, issue.Message);
    }

    [Theory]
    [InlineData("""{"value":"heavy","unit":"g/mol"}""")]   // not a number
    [InlineData("""{"value":0,"unit":"g/mol"}""")]         // a molar mass of zero divides the formation properties
    [InlineData("""{"value":-1,"unit":"g/mol"}""")]
    public void A_molar_weight_that_is_not_a_positive_number_is_refused(string quantity)
    {
        var values = IronValues.Replace("""{"value":55.845,"unit":"g/mol"}""", quantity);
        var (_, issues) = Parse($"[{Def(values: values)}]");
        Assert.Contains(issues, i => i.Code == "COMPOUND_DEFINITION_INVALID" && i.Message.Contains("molarWeight"));
    }

    [Fact]
    public void An_unknown_value_key_is_refused_rather_than_ignored()
    {
        // A key this runner does not bind would be a knob wired to nothing.
        var (_, issues) = Parse($"[{Def(values: IronValues + ""","criticalTemperature":{"value":9340,"unit":"K"}""")}]");
        var issue = Assert.Single(issues);
        Assert.Equal("COMPOUND_DEFINITION_INVALID", issue.Code);
        Assert.Contains("criticalTemperature", issue.Message);
    }

    [Fact]
    public void A_unit_this_runner_does_not_know_is_refused_never_converted()
    {
        // 1538 °C is iron's melting point. Read as kelvin it is 1538 K — 273 K low, and it converges.
        var values = IronValues.Replace("""{"value":1811,"unit":"K"}""", """{"value":1538,"unit":"C"}""");
        var (_, issues) = Parse($"[{Def(values: values)}]");
        var issue = Assert.Single(issues);
        Assert.Equal("INVALID_UNIT", issue.Code);
        Assert.Contains("meltingPoint", issue.Message);
        Assert.Contains("K", issue.Message);
    }

    [Fact]
    public void An_unknown_kind_is_refused_and_a_fluid_is_refused_as_unmeasured()
    {
        var (_, gas) = Parse($"[{Def(kind: "plasma")}]");
        Assert.Equal("COMPOUND_DEFINITION_INVALID", Assert.Single(gas).Code);

        var (_, fluid) = Parse($"[{Def(kind: "fluid")}]");
        var issue = Assert.Single(fluid);
        Assert.Equal("COMPOUND_KIND_UNSUPPORTED", issue.Code);
        Assert.Contains("Iron", issue.Message);
    }

    [Fact]
    public void A_definition_the_document_does_not_use_is_refused()
    {
        var (_, issues) = Parse($"[{Def()}]", "Water");
        var issue = Assert.Single(issues);
        Assert.Equal("COMPOUND_DEFINITION_INVALID", issue.Code);
        Assert.Contains("compounds", issue.Message);
    }

    [Fact]
    public void Two_definitions_with_one_name_conflict_whatever_their_case()
    {
        var (_, issues) = Parse($"[{Def()},{Def(name: "IRON")}]");
        Assert.Contains(issues, i => i.Code == "COMPOUND_DEFINITION_CONFLICT");
    }

    [Fact]
    public void More_than_fifty_definitions_is_too_large()
    {
        var many = string.Join(",", Enumerable.Range(0, 51).Select(i => Def(name: $"C{i}")));
        var (_, issues) = Parse($"[{many}]", Enumerable.Range(0, 51).Select(i => $"C{i}").ToArray());
        Assert.Contains(issues, i => i.Code == "DOCUMENT_TOO_LARGE");
    }

    [Fact]
    public void Formation_properties_cross_into_the_engine_per_kilogram()
    {
        // The engine's own naphthalene: 1174.84 kJ/kg. The handbook's: 150.6 kJ/mol at 128.171 g/mol.
        // Same number, two units — and passing kJ/mol straight through converges to a wrong duty.
        var naphthalene = IronValues
            .Replace("""{"value":55.845,"unit":"g/mol"}""", """{"value":128.171,"unit":"g/mol"}""")
            .Replace("""
                "enthalpyOfFormation":{"value":0,"unit":"kJ/mol"}
                """.Trim(), """
                "enthalpyOfFormation":{"value":150.6,"unit":"kJ/mol"}
                """.Trim());
        var (defs, issues) = Parse($"[{Def(values: naphthalene)}]");
        Assert.Empty(issues);

        var built = CompoundDefinitions.Build(defs[0], 0);
        Assert.InRange(built.IG_Enthalpy_of_Formation_25C, 1174.84 - 0.5, 1174.84 + 0.5);
        Assert.Equal(128.171, built.Molar_Weight);
    }

    [Fact]
    public void A_solid_is_built_as_a_solid_with_its_physical_values_where_the_engine_reads_them()
    {
        var (defs, _) = Parse($"[{Def()}]");
        var iron = CompoundDefinitions.Build(defs[0], 0);
        Assert.True(iron.IsSolid);
        Assert.Equal("Iron", iron.Name);
        Assert.Equal("Fe", iron.Formula);
        Assert.Equal("7439-89-6", iron.CAS_Number);
        Assert.Equal(1811, iron.TemperatureOfFusion);
        Assert.Equal(13.81, iron.EnthalpyOfFusionAtTf);
        Assert.Equal(7874, iron.SolidDensityAtTs);
        // J/(mol.K) on the wire; the engine's heat-capacity polynomials are per kmol.
        Assert.Equal(25100, iron.Solid_Heat_Capacity_Const_A, 6);
        // The placeholders an equation of state needs of every compound. Not physics; never on the wire.
        Assert.True(iron.Critical_Temperature > iron.TemperatureOfFusion);
        Assert.True(iron.Critical_Pressure > 0);
    }

    [Fact]
    public void A_definition_named_like_an_engine_compound_conflicts_and_a_new_one_is_registered()
    {
        var auto = new DWSIM.Automation.Automation3();

        var issues = new List<BuildIssue>();
        var (water, _) = Parse($"[{Def(name: "water")}]", "water");
        CompoundDefinitions.Register(auto, water, issues);
        var conflict = Assert.Single(issues);
        Assert.Equal("COMPOUND_DEFINITION_CONFLICT", conflict.Code);
        Assert.Contains("Water", conflict.Message);

        issues.Clear();
        var (iron, _) = Parse($"[{Def()}]");
        CompoundDefinitions.Register(auto, iron, issues);
        Assert.Empty(issues);
        Assert.True(auto.AvailableCompounds.ContainsKey("Iron"));

        // ADR 0015 invariant 2: nothing outlives the engine instance it was given to.
        Assert.False(new DWSIM.Automation.Automation3().AvailableCompounds.ContainsKey("Iron"));
    }
}

public class SolidsCheckTests
{
    private static readonly HashSet<string> Solids = new(StringComparer.OrdinalIgnoreCase) { "Iron" };

    private static (string Phase, IReadOnlyDictionary<string, double>? Composition) Phase(string name, params (string, double)[] comp) =>
        (name, comp.ToDictionary(c => c.Item1, c => c.Item2));

    [Fact]
    public void A_solid_in_a_liquid_phase_is_a_refusal_naming_stream_compound_and_phase()
    {
        var issues = SolidsCheck.SolidInFluid("S-2", [Phase("liquid", ("Iron", 1e-3), ("Water", 0.999))], Solids);
        var issue = Assert.Single(issues);
        Assert.Equal("error", issue.Severity);
        Assert.Equal("SOLID_REPORTED_AS_FLUID", issue.Code);
        Assert.Equal("S-2", issue.Tag);
        Assert.Contains("Iron", issue.Message);
        Assert.Contains("liquid", issue.Message);
    }

    [Theory]
    [InlineData("Vapor")]
    [InlineData("Liquid2")]
    public void Every_fluid_phase_counts_whatever_its_spelling(string phase) =>
        Assert.Single(SolidsCheck.SolidInFluid(null, [Phase(phase, ("iron", 0.5))], Solids));

    [Fact]
    public void A_solid_in_the_solid_phase_or_a_numerical_trace_of_it_elsewhere_is_fine()
    {
        Assert.Empty(SolidsCheck.SolidInFluid("S-1", [Phase("solid", ("Iron", 1.0))], Solids));
        Assert.Empty(SolidsCheck.SolidInFluid("S-1", [Phase("liquid", ("Iron", 1e-7), ("Water", 1.0))], Solids));
        Assert.Empty(SolidsCheck.SolidInFluid("S-1", [Phase("liquid", ("Water", 1.0))], Solids));
    }

    private static readonly Dictionary<string, double> MeltingPointK = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Naphthalene"] = 353.434, ["Water"] = 273.15, ["Toluene"] = 178.18, ["Iron"] = 1811,
    };

    [Fact]
    public void A_liquid_phase_that_is_mostly_a_compound_below_its_melting_point_is_warned_by_name()
    {
        // Measured 2026-10-04: naphthalene 0.5 + water 0.5 at 25 C comes back as a 94 % naphthalene liquid.
        var warnings = SolidsCheck.BelowMeltingPoint("S-1", 25.0,
            [Phase("liquid", ("Naphthalene", 0.94128), ("Water", 0.058721)), Phase("liquid2", ("Water", 1.0))],
            MeltingPointK, Solids);
        var w = Assert.Single(warnings);
        Assert.Contains("BELOW_MELTING_POINT_AS_LIQUID", w);
        Assert.Contains("S-1", w);
        Assert.Contains("Naphthalene", w);
        Assert.Contains("80.28", w);   // its melting point, in the unit the stream's temperature is reported in
    }

    [Fact]
    public void A_dissolved_minor_component_is_not_warned()
    {
        // Measured the same day: naphthalene at 5 mol% in toluene at 25 C is a true solution.
        Assert.Empty(SolidsCheck.BelowMeltingPoint("S-1", 25.0,
            [Phase("liquid", ("Naphthalene", 0.05), ("Toluene", 0.95))], MeltingPointK, Solids));
    }

    [Fact]
    public void Above_the_melting_point_in_a_vapour_or_with_no_temperature_there_is_nothing_to_say()
    {
        Assert.Empty(SolidsCheck.BelowMeltingPoint("S-1", 100.0,
            [Phase("liquid", ("Naphthalene", 0.94), ("Water", 0.06))], MeltingPointK, Solids));
        Assert.Empty(SolidsCheck.BelowMeltingPoint("S-1", 25.0,
            [Phase("vapor", ("Naphthalene", 0.94), ("Water", 0.06))], MeltingPointK, Solids));
        Assert.Empty(SolidsCheck.BelowMeltingPoint("S-1", null,
            [Phase("liquid", ("Naphthalene", 0.94), ("Water", 0.06))], MeltingPointK, Solids));
    }

    [Fact]
    public void Energy_keys_leave_the_bag_and_everything_else_stays()
    {
        // Measured 2026-10-05: a defined solid's enthalpy follows the placeholders, not its physics.
        var bag = new Dictionary<string, double>
        {
            ["enthalpy"] = -13464.1, ["entropy"] = -4.2, ["molar_enthalpy"] = -1, ["internal_energy"] = -2,
            ["gibbs_free_energy"] = -3, ["density"] = 7874, ["molecularWeight"] = 55.8, ["heatCapacityCp"] = 0.45,
        };
        var kept = SolidsCheck.WithoutEnergy(bag)!;
        Assert.Equal(new[] { "density", "heatCapacityCp", "molecularWeight" }, kept.Keys.Order());
        Assert.Null(SolidsCheck.WithoutEnergy(null));
        Assert.Null(SolidsCheck.WithoutEnergy(new() { ["enthalpy"] = 1 }));
    }

    [Fact]
    public void A_defined_solid_is_the_guards_business_not_the_warnings()
    {
        // One finding per defect: the refusal already names it.
        Assert.Empty(SolidsCheck.BelowMeltingPoint("S-1", 25.0,
            [Phase("liquid", ("Iron", 0.9), ("Water", 0.1))], MeltingPointK, Solids));
    }
}
