// dwsim-runner Worker tests — GPL-3.0
// Spec 141 US1 (T012/T013) + US5 (T032): the binder is honest.
//
// Measured 2026-08-06 (141 research.md §7 + T008/T009a):
//  - `SetEngineProperty` reflected over PROPERTIES only, while DWSIM exposes several
//    settable members as public FIELDS (ShortcutColumn.m_refluxratio, m_lightkey, …) —
//    so the shortcut column's whole parameter set silently missed.
//  - The fallback `so.SetPropertyValue(name, value)` cannot be trusted to report failure:
//    ShortcutColumn's override returns TRUE unconditionally and writes nothing for a name
//    it does not recognise (decompiled 9.0.5.0; confirmed live — a bogus lightKey and a
//    correct one produced byte-identical build-solve envelopes).
//
// These tests drive the real DWSIM classes through the Worker's own binder, no flowsheet
// or Automation boot involved.

using System.Text.Json;
using DWSIM.UnitOperations.UnitOperations;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class BinderHonestyTests
{
    private static readonly UnitOpDef ShortcutDef = UnitOpCatalog.Types["shortcutColumn"];

    private static JsonElement Json(string s) => JsonSerializer.Deserialize<JsonElement>(s);

    private static ParamDef CatalogParam(string name) =>
        ShortcutDef.Parameters.First(p => p.Name == name);

    /// <summary>Runs the shared binder path and collects every issue it reports.</summary>
    private static List<BuildIssue> Apply(ShortcutColumn sc, ParamDef p, string rawJson)
    {
        var issues = new List<BuildIssue>();
        var unit = new FlowObject("SC-1", "unitOp", "shortcutColumn", null, null, null);
        FlowsheetBuilder.ApplyParameter(sc, unit, ShortcutDef, p, Json(rawJson), [],
            (code, tag, message, path) => issues.Add(new BuildIssue("error", code, tag, path, message)));
        return issues;
    }

    // T012 (FR-001): a parameter name resolving to no settable member is a typed refusal
    // naming unit + parameter — never a silent skip. On the pre-fix binder this test FAILS:
    // the fallback SetPropertyValue accepts the name, writes nothing, and reports nothing.
    [Fact]
    public void Unbindable_parameter_is_a_typed_refusal_naming_unit_and_parameter()
    {
        var sc = new ShortcutColumn();
        var ghost = new ParamDef("ghostParam", "dimensionless", false, ["NoSuchEngineMember"]);

        var issues = Apply(sc, ghost, "2.5");

        var issue = Assert.Single(issues);
        Assert.Equal("UNBINDABLE_PARAMETER", issue.Code);
        Assert.Equal("SC-1", issue.Tag);
        Assert.Contains("ghostParam", issue.Message);
    }

    // T013 (FR-002): the refusal is per-parameter and carries a path, the same envelope
    // shape MISSING_REQUIRED_PARAMETER issues ride (severity/code/tag/path/message —
    // serialized verbatim by the worker's BuildErrorDoc and the API's 422 body).
    [Fact]
    public void Refusal_is_per_parameter_with_path()
    {
        var sc = new ShortcutColumn();

        var issues =
            Apply(sc, new ParamDef("ghostA", "dimensionless", false, ["NopeA"]), "1")
            .Concat(Apply(sc, new ParamDef("ghostB", "dimensionless", false, ["NopeB"]), "2"))
            .ToList();

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal("UNBINDABLE_PARAMETER", i.Code));
        Assert.Equal("parameters.ghostA", issues[0].Path);
        Assert.Equal("parameters.ghostB", issues[1].Path);
    }

    // T014 (FR-003): binding reaches settable FIELDS as well as properties. The shortcut
    // column's five required parameters all live in public fields; before the fix every
    // one of them stayed at its constructor default (m_refluxratio 1.5, m_lightkey "").
    [Fact]
    public void Shortcut_column_parameters_reach_the_engine_fields()
    {
        var sc = new ShortcutColumn();

        Assert.Empty(Apply(sc, CatalogParam("refluxRatio"), """{"value":3.0,"unit":""}"""));
        Assert.Empty(Apply(sc, CatalogParam("lightKey"), "\"Benzene\""));
        Assert.Empty(Apply(sc, CatalogParam("heavyKey"), "\"Toluene\""));
        Assert.Empty(Apply(sc, CatalogParam("condenserPressure"), """{"value":1.2,"unit":"bar"}"""));

        Assert.Equal(3.0, sc.m_refluxratio, 10);
        Assert.Equal("Benzene", sc.m_lightkey);
        Assert.Equal("Toluene", sc.m_heavykey);
        Assert.Equal(120000.0, sc.m_condenserpressure, 3);   // 1.2 bar → Pa (SI, absolute)
    }

    // T015 (FR-001 acceptance 4): a parameter that binds successfully stays silent —
    // covered by the Assert.Empty calls above — and a bag-listed name still delegates
    // to the generic interface ("Stage Height" is in ShortcutColumn's property bag).
    [Fact]
    public void Property_bag_names_still_delegate_to_the_generic_interface()
    {
        var sc = new ShortcutColumn();
        var stageHeight = new ParamDef("Stage Height", "length", false, []);

        var issues = Apply(sc, stageHeight, "0.7");

        Assert.Empty(issues);
        Assert.Equal(0.7, sc.StageHeight, 10);
    }

    // T032 (FR-010): both column types' energy ports are declared required — the engine
    // refuses to validate without them (BaseClass.Validate, "Check the connections of the
    // object"), so a catalog saying `required: false` is a lie the document validator
    // then repeats. 045's cleanest surviving finding.
    [Theory]
    [InlineData("distillationColumn")]
    [InlineData("shortcutColumn")]
    public void Column_energy_ports_are_required(string type)
    {
        var energyPorts = UnitOpCatalog.Types[type].Ports.Where(p => p.Accepts == "energy").ToList();

        Assert.NotEmpty(energyPorts);
        Assert.All(energyPorts, p =>
            Assert.True(p.Required, $"{type} energy port '{p.Name}' must be required: true"));
    }
}
