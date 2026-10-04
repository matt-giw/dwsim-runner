// dwsim-runner Worker tests — GPL-3.0
// iskra spec 285 (ISK-485), #31 review finding 1 — the sanitiser is the security boundary of the
// read path. It rewrites the untrusted flowsheet XML BEFORE the engine's loader sees it, keeping
// only the sections the read needs and only the object types we have reviewed as safe to
// instantiate. These tests use harmless placeholder values only; none is a payload.

using System.Linq;
using DWSIM.Interfaces.Enums.GraphicObjects;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class SanitizerTests
{
    private static string Doc(string body) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DWSIM_Simulation_Data>{body}</DWSIM_Simulation_Data>";

    private static string So(string type, string name = "obj-1") =>
        $"<SimulationObject><Name>{name}</Name><Type>{type}</Type></SimulationObject>";

    [Fact]
    public void Keeps_the_eight_sections_and_drops_every_other()
    {
        var xml = Doc("<GeneralInfo/><Settings/><Compounds/><PropertyPackages/><SimulationObjects/>"
                    + "<GraphicObjects/><Reactions/><ReactionSets/>"
                    + "<ScriptItems><ScriptItem><Title>t</Title><LinkedEventType>SimulationOpened</LinkedEventType></ScriptItem></ScriptItems>"
                    + "<DynamicsManager/><Spreadsheet/><SensitivityAnalysis/><OptimizationCases/>"
                    + "<StoredSolutions/><ChartItems/><PetroleumAssays/><WatchItems/><PanelLayout/><FlowsheetView/><DynamicProperties/>");

        var (clean, removed) = Sanitizer.Sanitize(xml);

        foreach (var keep in new[] { "GeneralInfo", "Settings", "Compounds", "PropertyPackages", "SimulationObjects", "GraphicObjects", "Reactions", "ReactionSets" })
            Assert.Contains($"<{keep}", clean);
        foreach (var gone in new[] { "ScriptItems", "DynamicsManager", "Spreadsheet", "SensitivityAnalysis", "StoredSolutions", "<DynamicProperties" })
            Assert.DoesNotContain(gone, clean);
        Assert.Contains(removed, r => r.Kind == "script" && r.Detail.Contains("SimulationOpened"));
        Assert.Contains(removed, r => r.Kind == "section" && r.Detail == "DynamicsManager");
    }

    [Fact]
    public void Strips_dynamic_property_and_attached_utility_children_anywhere()
    {
        var xml = Doc("<SimulationObjects>"
                    + "<SimulationObject><Name>s</Name><Type>DWSIM.Thermodynamics.Streams.MaterialStream</Type>"
                    + "<DynamicProperties><x/></DynamicProperties><AttachedUtilities><u/></AttachedUtilities></SimulationObject>"
                    + "</SimulationObjects>");

        var (clean, removed) = Sanitizer.Sanitize(xml);

        Assert.Contains("MaterialStream", clean);      // the object survives
        Assert.DoesNotContain("DynamicProperties", clean);
        Assert.DoesNotContain("AttachedUtilities", clean);
        Assert.Contains(removed, r => r.Kind == "dynamicProperties");
    }

    [Theory]
    [InlineData("DWSIM.UnitOperations.UnitOperations.CustomUO")]
    [InlineData("DWSIM.UnitOperations.UnitOperations.Flowsheet")]
    [InlineData("DWSIM.UnitOperations.SpecialOps.PythonController")]
    [InlineData("System.Diagnostics.Process")]                 // a type outside the list; harmless string here
    [InlineData("DWSIM.UnitOperations.UnitOperations.Heater, Evil")]  // assembly-qualified
    public void Strips_a_simulation_object_whose_type_is_not_allow_listed(string type)
    {
        var xml = Doc($"<SimulationObjects>{So(type, "danger")}{So("DWSIM.UnitOperations.UnitOperations.Heater", "keep")}</SimulationObjects>");

        var (clean, removed) = Sanitizer.Sanitize(xml);

        Assert.DoesNotContain(type, clean);
        Assert.Contains("<Name>keep</Name>", clean);
        Assert.Contains(removed, r => r.Kind == "unsupportedType" && r.Detail.Contains("danger"));
    }

    [Theory]
    [InlineData("DWSIM.Thermodynamics.Streams.MaterialStream")]
    [InlineData("DWSIM.UnitOperations.UnitOperations.Heater")]
    [InlineData("DWSIM.UnitOperations.UnitOperations.CapeOpenUO")]   // safe to instantiate on Linux (no COM); stays a placeholder
    public void Keeps_an_allow_listed_type(string type)
    {
        var (clean, removed) = Sanitizer.Sanitize(Doc($"<SimulationObjects>{So(type)}</SimulationObjects>"));
        Assert.Contains(type, clean);
        Assert.DoesNotContain(removed, r => r.Kind == "unsupportedType");
    }

    [Fact]
    public void Drops_an_assembly_qualified_graphic_type()
    {
        var xml = Doc("<GraphicObjects>"
                    + "<GraphicObject><Name>g1</Name><Type>DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.HeaterGraphic</Type></GraphicObject>"
                    + "<GraphicObject><Name>g2</Name><Type>Evil.Graphic, Evil</Type></GraphicObject>"
                    + "</GraphicObjects>");

        var (clean, removed) = Sanitizer.Sanitize(xml);

        Assert.Contains("HeaterGraphic", clean);
        Assert.DoesNotContain("Evil.Graphic", clean);
        Assert.Contains(removed, r => r.Kind == "unsupportedType" && r.Detail.Contains("g2"));
    }

    [Fact]
    public void A_doctype_is_rejected_not_expanded()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"y\">]><DWSIM_Simulation_Data><Settings/></DWSIM_Simulation_Data>";
        Assert.Throws<System.Xml.XmlException>(() => Sanitizer.Sanitize(xml));
    }

    // The guard: the allow-list was reviewed against the engine's whole ObjectType vocabulary. If a
    // DWSIM upgrade adds a member, this fails so someone decides whether the new type is safe to
    // instantiate from an untrusted file. The runner is DWSIM 9.0.5; re-review on every version bump.
    [Fact]
    public void Every_engine_object_type_has_been_reviewed()
    {
        var engine = System.Enum.GetNames<ObjectType>().ToHashSet();
        Assert.True(engine.SetEquals(Sanitizer.ReviewedObjectTypes),
            "ObjectType changed; re-review the sanitiser allow-list. "
            + "new: " + string.Join(",", engine.Except(Sanitizer.ReviewedObjectTypes))
            + " gone: " + string.Join(",", Sanitizer.ReviewedObjectTypes.Except(engine)));
    }
}
