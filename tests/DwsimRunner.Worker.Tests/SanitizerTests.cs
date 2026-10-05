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

    // The same guard for property packages and graphic objects, which are now allowed by EXACT name
    // rather than by namespace prefix. The engine's built-in set is read from the real assemblies; if
    // a DWSIM upgrade adds or removes a class, these fail and the allow-lists are re-reviewed.
    [Fact]
    public void Every_engine_property_package_type_has_been_reviewed()
    {
        var engine = EngineTypes(typeof(DWSIM.Interfaces.IPropertyPackage), "DWSIM.Thermodynamics.dll");
        AssertReviewed(engine, Sanitizer.EnginePackageTypes, "property package");
    }

    [Fact]
    public void Every_engine_graphic_object_type_has_been_reviewed()
    {
        var engine = EngineTypes(typeof(DWSIM.Interfaces.IGraphicObject),
            "DWSIM.Drawing.SkiaSharp.dll", "DWSIM.DrawingTools.SkiaSharp.Extended.dll");
        AssertReviewed(engine, Sanitizer.EngineGraphicTypes, "graphic object");
        Assert.Subset(Sanitizer.EngineGraphicTypes, Sanitizer.AllowedEngineGraphicTypes);
    }

    // The names kept because they resolve to nothing in this engine must STILL resolve to nothing in
    // every assembly the loader looks them up in, or they are no longer inert and need a review.
    [Fact]
    public void Names_kept_as_unresolved_resolve_to_no_engine_type()
    {
        var lookIn = new[] { "DWSIM.FlowsheetBase.dll", "DWSIM.Thermodynamics.dll", "DWSIM.Drawing.SkiaSharp.dll",
                             "DWSIM.DrawingTools.SkiaSharp.Extended.dll" }
            .Select(f => System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(DwsimResolver.DwsimPath, f)))
            .ToList();
        var resolved = Sanitizer.UnresolvedPackageTypes.Concat(Sanitizer.UnresolvedGraphicTypes)
            .Where(n => lookIn.Any(a => a.GetType(n) is not null) || System.Type.GetType(n) is not null)
            .ToList();
        Assert.True(resolved.Count == 0, "now resolve to an engine type; review them: " + string.Join(",", resolved));
    }

    [Theory]
    [InlineData("DWSIM.Thermodynamics.PropertyPackages.Auxiliary.FlashAlgorithms.NestedLoops")]  // under the old prefix
    [InlineData("DWSIM.Thermodynamics.PropertyPackages.NotAPackage")]
    public void A_package_type_outside_the_exact_list_is_stripped(string type)
    {
        var xml = Doc($"<PropertyPackages><PropertyPackage><Tag>pp</Tag><Type>{type}</Type></PropertyPackage>"
                    + "<PropertyPackage><Tag>keep</Tag><Type>DWSIM.Thermodynamics.PropertyPackages.PengRobinsonPropertyPackage</Type></PropertyPackage></PropertyPackages>");

        var (clean, removed) = Sanitizer.Sanitize(xml);

        Assert.DoesNotContain(type, clean);
        Assert.Contains("PengRobinsonPropertyPackage", clean);
        Assert.Contains(removed, r => r.Kind == "unsupportedType" && r.Detail.Contains(type));
    }

    [Theory]
    [InlineData("DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ScriptGraphic")]   // an engine type, not allowed
    [InlineData("DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.NotAGraphic")]     // under the old prefix
    public void A_graphic_type_outside_the_exact_list_is_stripped(string type)
    {
        var xml = Doc($"<GraphicObjects><GraphicObject><Name>g</Name><Type>{type}</Type></GraphicObject></GraphicObjects>");
        var (clean, removed) = Sanitizer.Sanitize(xml);
        Assert.DoesNotContain(type, clean);
        Assert.Contains(removed, r => r.Kind == "unsupportedType" && r.Detail.Contains(type));
    }

    private static HashSet<string> EngineTypes(System.Type contract, params string[] assemblies) =>
        assemblies
            .Select(f => System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(DwsimResolver.DwsimPath, f)))
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null).Cast<System.Type>().ToArray(); }
            })
            .Where(t => t.IsClass && !t.IsAbstract && contract.IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .ToHashSet();

    private static void AssertReviewed(HashSet<string> engine, HashSet<string> reviewed, string what) =>
        Assert.True(engine.SetEquals(reviewed),
            $"the engine's {what} types changed; re-review the sanitiser allow-list. "
            + "new: " + string.Join(",", engine.Except(reviewed))
            + " gone: " + string.Join(",", reviewed.Except(engine)));
}
