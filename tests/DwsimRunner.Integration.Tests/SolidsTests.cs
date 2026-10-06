// iskra spec 281 (ISK-534) — Tier B: a defined solid is SOLID, or the request is refused.
//
// Every case here was measured with a probe on 2026-10-04 (DWSIM 9.0.5.0) before the code was
// written, and these are that table as assertions:
//
//   iron + water, 25 °C        Raoult + HandleSolids   S 0.495 (iron) + L 0.505 with 0.95 %  REFUSED by the guard
//                                                      iron "dissolved" in the water
//                              PR, any setting         iron liquid, or water "solid"        wrong → refused
//   Fe + H2O + Fe3O4 + H2,     Raoult + HandleSolids   V 0.6 + S 0.4 (Fe 0.75, Fe3O4 0.25)  right
//   700 °C
//   naphthalene + water, 25 °C default                 a 94 % naphthalene "liquid"          warned
//   naphthalene 5 % in toluene default                 one liquid — a true solution         silent
//
// The 25 °C row was first read as "right" and is not: the engine dissolves 0.95 % of the iron in
// the liquid water — the ideal solubility its solid-liquid model computes from the enthalpy of
// fusion. Iron is not 1 mol% soluble in water. The guard refuses it, so a defined solid is usable
// where no liquid phase coexists with it (iron and steam), and refused by name where one does.
//
// The thermochemical values below are handbook-order and illustrative. These tests measure where
// the engine PUTS a solid, not the chemistry.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Solids")]
public class SolidsTests
{
    private static object Q(double value, string unit) => new { value, unit };

    private static object Solid(string name, string formula, double mw, double tm, double hfus, double rho, double cp,
        double dHf, double dGf) => new
    {
        name, formula, kind = "solid",
        values = new Dictionary<string, object>
        {
            ["molarWeight"] = Q(mw, "g/mol"),
            ["meltingPoint"] = Q(tm, "K"),
            ["enthalpyOfFusion"] = Q(hfus, "kJ/mol"),
            ["solidDensity"] = Q(rho, "kg/m3"),
            ["solidHeatCapacity"] = Q(cp, "J/[mol.K]"),
            ["enthalpyOfFormation"] = Q(dHf, "kJ/mol"),
            ["gibbsEnergyOfFormation"] = Q(dGf, "kJ/mol"),
        },
    };

    internal static readonly object Iron = Solid("Iron", "Fe", 55.845, 1811, 13.81, 7874, 25.1, 0, 0);
    internal static readonly object Magnetite = Solid("Magnetite", "Fe3O4", 231.533, 1870, 138, 5170, 143.4, -1118.4, -1015.4);

    private static Task<HttpResponseMessage> Flash(string package, double tC, Dictionary<string, double> z, params object[] definitions) =>
        RunnerConnection.Client.PostAsJsonAsync("/flash", new Dictionary<string, object?>
        {
            ["compounds"] = z.Keys.ToArray(),
            ["composition"] = new { basis = "molar", fractions = z },
            ["propertyPackage"] = package,
            ["flashType"] = "TP",
            ["temperature"] = Q(tC, "C"),
            ["pressure"] = Q(1.0, "bar"),
            ["compoundDefinitions"] = definitions.Length > 0 ? definitions : null,
        });

    private static async Task<JsonElement> Body(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static Dictionary<string, (double Fraction, Dictionary<string, double> Composition)> Phases(JsonElement r) =>
        r.GetProperty("phases").EnumerateArray().ToDictionary(
            p => p.GetProperty("phase").GetString()!,
            p => (p.GetProperty("molarFraction").GetDouble(),
                  p.GetProperty("composition").EnumerateObject().ToDictionary(c => c.Name, c => c.Value.GetDouble())));

    [SkippableFact]
    public async Task A_defined_solid_with_steam_is_reported_solid_under_the_allowlisted_package()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await Flash("RAOULT", 700, new() { ["Iron"] = 0.5, ["Water"] = 0.5 }, Iron);
        var body = await Body(resp);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body.ToString());

        var phases = Phases(body);
        Assert.True(phases.ContainsKey("Solid"), body.ToString());
        Assert.InRange(phases["Solid"].Composition["Iron"], 0.999, 1.001);
        Assert.InRange(phases["Solid"].Fraction, 0.499, 0.501);
        Assert.InRange(phases["Vapor"].Composition["Water"], 0.999, 1.001);
        // The iron is nowhere else, to the guard's own tolerance — a 200 here means it found nothing to refuse.
        foreach (var (name, phase) in phases.Where(p => p.Key != "Solid"))
            Assert.True(phase.Composition.GetValueOrDefault("Iron") < 1e-6, $"iron in {name}: {body}");
    }

    [SkippableFact]
    public async Task Energy_results_are_withheld_and_a_PH_flash_is_refused_when_a_solid_is_defined()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // T009 measured the enthalpy moving 4x with the placeholders. The phases are reported; the energy is not.
        var body = await Body(await Flash("RAOULT", 700, new() { ["Iron"] = 0.5, ["Water"] = 0.5 }, Iron));
        // A null is dropped from the wire, so "withheld" reads as absent.
        Assert.False(body.TryGetProperty("enthalpyKJKg", out var h) && h.ValueKind != JsonValueKind.Null, body.ToString());
        Assert.False(body.TryGetProperty("entropyKJKgK", out var s) && s.ValueKind != JsonValueKind.Null, body.ToString());
        Assert.Contains(body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!),
            w => w.Contains("SOLID_ENTHALPY_UNMEASURED"));

        var ph = await RunnerConnection.Client.PostAsJsonAsync("/flash", new Dictionary<string, object?>
        {
            ["compounds"] = new[] { "Iron", "Water" },
            ["composition"] = new { basis = "molar", fractions = new Dictionary<string, double> { ["Iron"] = 0.5, ["Water"] = 0.5 } },
            ["propertyPackage"] = "RAOULT",
            ["flashType"] = "PH",
            ["pressure"] = Q(1.0, "bar"),
            ["enthalpy"] = Q(-10000, "kJ/kg"),
            ["compoundDefinitions"] = new[] { Iron },
        });
        var phBody = await Body(ph);
        Assert.Equal(HttpStatusCode.BadRequest, ph.StatusCode);
        Assert.Contains("SOLID_ENTHALPY_UNMEASURED", phBody.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task A_solid_the_engine_dissolves_in_liquid_water_is_refused_not_reported()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // Measured: S 0.495 (iron) + L 0.505 carrying 0.95 % iron. Nearly right is the dangerous kind:
        // it converges, and it says iron is soluble in water.
        var resp = await Flash("RAOULT", 25, new() { ["Iron"] = 0.5, ["Water"] = 0.5 }, Iron);
        var body = await Body(resp);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("SOLID_REPORTED_AS_FLUID", body.GetProperty("error").GetString());
        var message = body.GetProperty("message").GetString()!;
        Assert.Contains("Iron", message);
        Assert.Contains("Liquid", message);
    }

    [SkippableFact]
    public async Task The_iron_steam_mixture_at_700C_keeps_both_solids_solid_and_closes_its_balance()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var z = new Dictionary<string, double> { ["Iron"] = 0.3, ["Water"] = 0.4, ["Magnetite"] = 0.1, ["Hydrogen"] = 0.2 };
        var resp = await Flash("RAOULT", 700, z, Iron, Magnetite);
        var body = await Body(resp);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body.ToString());

        var phases = Phases(body);
        Assert.InRange(phases["Solid"].Fraction, 0.399, 0.401);
        Assert.InRange(phases["Solid"].Composition["Iron"], 0.749, 0.751);
        Assert.InRange(phases["Solid"].Composition["Magnetite"], 0.249, 0.251);
        Assert.InRange(phases["Vapor"].Fraction, 0.599, 0.601);

        // Every compound's moles are where the feed put them.
        foreach (var (compound, feed) in z)
        {
            var found = phases.Values.Sum(p => p.Fraction * p.Composition.GetValueOrDefault(compound));
            Assert.InRange(found, feed - 1e-4, feed + 1e-4);
        }
    }

    [SkippableTheory]
    [InlineData("PR")]
    [InlineData("NRTL")]
    public async Task A_package_not_measured_to_handle_solids_is_refused_before_the_flash(string package)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await Flash(package, 25, new() { ["Iron"] = 0.5, ["Water"] = 0.5 }, Iron);
        var body = await Body(resp);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("SOLIDS_UNSUPPORTED_PACKAGE", body.GetProperty("error").GetString());
        Assert.Contains("RAOULT", body.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task A_definition_leaves_nothing_behind_for_the_next_request()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var with = await Flash("RAOULT", 700, new() { ["Iron"] = 0.5, ["Water"] = 0.5 }, Iron);
        Assert.True(with.StatusCode == HttpStatusCode.OK, (await Body(with)).ToString());

        // The same compounds, a different temperature (so the result cache cannot answer), no definition.
        var without = await Flash("RAOULT", 701, new() { ["Iron"] = 0.5, ["Water"] = 0.5 });
        var body = await Body(without);
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal("FLASH_INVALID", body.GetProperty("error").GetString());
        Assert.Contains("'Iron' not found", body.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task A_definition_named_like_an_engine_compound_is_a_conflict()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var water = Solid("water", "H2O", 18.015, 273.15, 6.01, 917, 37.8, -285.8, -237.1);
        var resp = await Flash("RAOULT", 25, new() { ["water"] = 1.0 }, water);
        var body = await Body(resp);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("COMPOUND_DEFINITION_CONFLICT", body.GetProperty("error").GetString());
    }

    [SkippableFact]
    public async Task Naphthalene_in_water_below_its_melting_point_is_warned_and_in_toluene_is_not()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // No definitions anywhere in this test: the warning is for ordinary engine compounds.
        var inWater = await Body(await Flash("PR", 25, new() { ["Naphthalene"] = 0.5, ["Water"] = 0.5 }));
        // Contains, not Single: the API appends its own entry to `warnings` on an unsupported engine build.
        var warning = Assert.Single(inWater.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)
            .Where(w => w.Contains("BELOW_MELTING_POINT_AS_LIQUID")));
        Assert.Contains("Naphthalene", warning);
        // The numbers are untouched: the warning changes no result (SC-010).
        Assert.InRange(Phases(inWater)["Liquid"].Composition["Naphthalene"], 0.93, 0.95);

        var inToluene = await Body(await Flash("PR", 25, new() { ["Naphthalene"] = 0.05, ["Toluene"] = 0.95 }));
        Assert.True(!inToluene.TryGetProperty("warnings", out var none) || none.ValueKind == JsonValueKind.Null
                    || none.GetArrayLength() == 0, inToluene.ToString());

        // FR-003 — a pure solid below its melting point is still reported solid, as before.
        var pure = await Body(await Flash("PR", 25, new() { ["Naphthalene"] = 1.0 }));
        Assert.InRange(Phases(pure)["Solid"].Fraction, 0.999, 1.001);
    }

    private const string IronWaterHeaterDoc = """
    {
      "schemaVersion": 1,
      "name": "281 solids",
      "compounds": ["Iron", "Water"],
      "propertyPackage": "__PACKAGE__",
      "compoundDefinitions": [__DEFINITIONS__],
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": __TEMPERATURE__, "unit": "C" },
                    "pressure": { "value": 1, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar", "fractions": { "Iron": 0.5, "Water": 0.5 } } } }
      ],
      "connections": []
    }
    """;

    private static string HeaterDoc(string package, int temperatureC = 700) => IronWaterHeaterDoc
        .Replace("__PACKAGE__", package)
        .Replace("__TEMPERATURE__", temperatureC.ToString())
        .Replace("__DEFINITIONS__", JsonSerializer.Serialize(Iron));

    [SkippableFact]
    public async Task A_solved_document_reports_the_defined_solid_in_the_solid_phase()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(HeaterDoc("RAOULT")));
        var body = await Body(resp);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.True(body.GetProperty("converged").GetBoolean(), body.ToString());

        var feed = body.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "FEED");
        // Energy withheld, by name (T009).
        Assert.Contains(body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!),
            w => w.Contains("SOLID_ENTHALPY_UNMEASURED"));
        if (feed.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            Assert.DoesNotContain(props.EnumerateObject(), p => p.Name.Contains("enthalpy", StringComparison.OrdinalIgnoreCase));
        var phases = feed.GetProperty("phases").EnumerateArray().ToList();
        var solid = phases.Single(p => p.GetProperty("name").GetString() == "solid");
        Assert.InRange(solid.GetProperty("composition").GetProperty("Iron").GetDouble(), 0.999, 1.001);
        foreach (var p in phases.Where(p => p.GetProperty("name").GetString() != "solid"))
            Assert.True(!p.GetProperty("composition").TryGetProperty("Iron", out var fe) || fe.GetDouble() < 1e-6, body.ToString());
    }

    /// <summary>FEED (iron + steam) → heater → OUT, as a document object graph; the heater on an outlet
    /// temperature by default, or on a duty.</summary>
    private static Dictionary<string, object?> HeaterDocGraph(double feedC, bool onDuty = false) => new()
    {
        ["schemaVersion"] = 1,
        ["name"] = "281 solids heater",
        ["compounds"] = new[] { "Iron", "Water" },
        ["propertyPackage"] = "RAOULT",
        ["compoundDefinitions"] = new[] { Iron },
        ["objects"] = new object[]
        {
            new { tag = "FEED", kind = "materialStream", spec = new {
                temperature = Q(feedC, "C"), pressure = Q(1, "bar"), massFlow = Q(100, "kg/h"),
                composition = new { basis = "molar", fractions = new Dictionary<string, double> { ["Iron"] = 0.5, ["Water"] = 0.5 } } } },
            new { tag = "H-1", kind = "unitOp", type = "heater",
                  parameters = onDuty ? (object)new { heatDuty = Q(50, "kW") } : new { outletTemperature = Q(700, "C") } },
            new { tag = "OUT", kind = "materialStream" },
        },
        ["connections"] = new[] { new { from = "FEED", to = "H-1", port = "Inlet" }, new { from = "H-1", to = "OUT", port = "Outlet" } },
    };

    private static StringContent BuildSolveBody(Dictionary<string, object?> document, string? saveAsTemplateId = null) =>
        new(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["document"] = document,
            ["timeoutSeconds"] = 120,
            ["saveAsTemplate"] = saveAsTemplateId is null ? null : new { id = saveAsTemplateId },
        }), System.Text.Encoding.UTF8, "application/json");

    private static bool Absent(JsonElement el, string property) =>
        !el.TryGetProperty(property, out var v) || v.ValueKind == JsonValueKind.Null;

    [SkippableFact]
    public async Task A_heater_on_a_duty_over_a_solid_is_refused_because_its_outlet_flash_melts_the_iron()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // Measured 2026-10-05: with the outlet found by an enthalpy balance (a PH flash over the
        // placeholder enthalpy), the engine returns the iron as a LIQUID. The guard refuses it, naming
        // the stream — the right answer: an energy balance over a defined solid is not supported.
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", BuildSolveBody(HeaterDocGraph(600, onDuty: true)));
        var body = await Body(resp);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("SOLID_REPORTED_AS_FLUID", body.GetProperty("error").GetString());
        Assert.Contains(body.GetProperty("issues").EnumerateArray(), i => i.GetProperty("tag").GetString() == "OUT");
    }

    [SkippableFact]
    public async Task A_computed_temperature_is_withheld_with_a_solid_present_and_a_feeds_is_kept()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // Every stream that is not a feed had its temperature computed by the engine; with a solid
        // present that number rests on the placeholder enthalpy (T009), so it is withheld — even
        // here, where it equals the heater's own setpoint. The feed's was stated by the caller: kept.
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", BuildSolveBody(HeaterDocGraph(600)));
        var body = await Body(resp);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, body.ToString());
        var streams = body.GetProperty("streams").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!);
        Assert.InRange(streams["FEED"].GetProperty("temperatureC").GetDouble(), 599.9, 600.1);
        Assert.True(Absent(streams["OUT"], "temperatureC"), body.ToString());
        var heater = body.GetProperty("unitOps").EnumerateArray().Single(u => u.GetProperty("name").GetString() == "H-1");
        Assert.True(Absent(heater, "dutyKw") && Absent(heater, "outletTemperatureC"), body.ToString());
        Assert.Contains(body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!), w => w.Contains("SOLID_ENTHALPY_UNMEASURED"));
    }

    [SkippableFact]
    public async Task A_template_saved_with_a_defined_solid_obeys_the_same_rules_when_solved_again()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // Review of #34: the .dwxmz keeps the solid, and the template path used to run without the guard.
        var id = $"solids-{Guid.NewGuid():N}";
        var save = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", BuildSolveBody(HeaterDocGraph(600), id));
        var saved = await Body(save);
        Skip.If(save.StatusCode != HttpStatusCode.OK
                || !(saved.TryGetProperty("template", out var t) && t.TryGetProperty("saved", out var ok) && ok.GetBoolean()),
            "no writable user-template store on this runner: " + saved);
        try
        {
            // Re-solved from the template with the feed cooled to 25 C: 0.95 % of the iron would sit in
            // liquid water, which the document path refuses — so must this one.
            var resp = await RunnerConnection.Client.PostAsJsonAsync("/solve", new
            {
                templateId = id,
                overrides = new[] { new { @object = "FEED", property = "Temperature", value = 298.15, unit = "K" } },
            });
            var body = await Body(resp);
            Assert.True(resp.StatusCode == HttpStatusCode.UnprocessableEntity, body.ToString());
            Assert.Equal("SOLID_REPORTED_AS_FLUID", body.GetProperty("error").GetString());
        }
        finally
        {
            await RunnerConnection.Client.DeleteAsync($"/templates/{id}");
        }
    }

    [SkippableFact]
    public async Task A_solved_document_whose_solid_ends_up_in_a_liquid_is_refused_naming_the_stream()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(HeaterDoc("RAOULT", temperatureC: 25)));
        var body = await Body(resp);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("SOLID_REPORTED_AS_FLUID", body.GetProperty("error").GetString());
        var issue = Assert.Single(body.GetProperty("issues").EnumerateArray());
        Assert.Equal("FEED", issue.GetProperty("tag").GetString());
        Assert.Contains("Iron", issue.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task A_document_with_a_solid_under_an_unmeasured_package_is_refused_by_name()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            BuildSolveTests.BuildSolveBody(HeaterDoc("PR")));
        var body = await Body(resp);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("SOLIDS_UNSUPPORTED_PACKAGE", body.GetProperty("error").GetString());
        Assert.Contains(body.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("message").GetString()!.Contains("RAOULT"));
    }

    [SkippableFact]
    public async Task The_catalog_publishes_the_allowlist_it_enforces()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var solids = JsonSerializer.Deserialize<JsonElement>(await RunnerConnection.Client.GetStringAsync("/catalog/solids"))
            .GetProperty("solids");
        Assert.Contains("RAOULT", solids.GetProperty("packages").EnumerateArray().Select(p => p.GetString()));
    }
}
