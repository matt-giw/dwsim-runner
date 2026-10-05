// dwsim-runner Worker — GPL-3.0
// iskra spec 285 (ISK-485), #31 review finding 1 — THE SECURITY BOUNDARY of the read path.
//
// DWSIM's loader is not a parser: loading a file runs code it names (a linked "Simulation Opened"
// script through IronPython; a type the file names, instantiated through Type.GetType + CreateInstance;
// a value deserialised into a type the file names; a nested flowsheet it loads from a path). So the
// bytes are rewritten HERE, before the loader sees them, to an ALLOW-LIST: only the sections the read
// needs, and only object/package/graphic types reviewed as safe to instantiate, each by exact name.
// Everything removed is reported so nothing is silently dropped. Re-review on every DWSIM version
// change (runner is 9.0.5). The read worker ALSO runs inside ReadSandbox (least privilege); this file
// is the primary control, the sandbox the second.

using System.Xml;
using System.Xml.Linq;

namespace DwsimRunner.Worker;

internal static class Sanitizer
{
    internal sealed record RemovedItem(string Kind, string Detail);

    // The only top-level sections LoadFromXML reads that the read needs. Everything else — ScriptItems
    // (the measured RCE), DynamicsManager, Spreadsheet, SensitivityAnalysis, OptimizationCases,
    // StoredSolutions, Charts, the top-level DynamicProperties — is dropped.
    private static readonly HashSet<string> KeepSections = new(StringComparer.Ordinal)
        { "GeneralInfo", "Settings", "Compounds", "PropertyPackages", "SimulationObjects", "GraphicObjects", "Reactions", "ReactionSets" };

    // Removed wherever they appear: each drives a load-time Type.GetType + JsonConvert.DeserializeObject
    // into a type the file names. None is needed for the read, which uses the engine's own properties.
    private static readonly string[] DropAnywhere =
        { "DynamicProperties", "DynamicPropertiesDescriptions", "DynamicPropertiesUnitTypes", "AttachedUtilities" };

    // The CLR <Type> names the engine may instantiate from an untrusted file. The DWSIM 9.0.5 corpus
    // types, minus the four that execute or load more at deserialisation (CustomUO, ExcelUO, the
    // Flowsheet sub-flowsheet UO, and the Python controller) — those are stripped and reported.
    // CapeOpenUO is kept: on Linux its COM path is a no-op and it becomes a CAPE_OPEN placeholder.
    internal static readonly HashSet<string> AllowedSimObjectTypes = new(StringComparer.Ordinal)
    {
        "DWSIM.Thermodynamics.Streams.MaterialStream",
        "DWSIM.UnitOperations.Streams.EnergyStream",
        "DWSIM.UnitOperations.UnitOperations.Mixer",
        "DWSIM.UnitOperations.UnitOperations.Splitter",
        "DWSIM.UnitOperations.UnitOperations.Vessel",
        "DWSIM.UnitOperations.UnitOperations.Tank",
        "DWSIM.UnitOperations.UnitOperations.Heater",
        "DWSIM.UnitOperations.UnitOperations.Cooler",
        "DWSIM.UnitOperations.UnitOperations.HeatExchanger",
        "DWSIM.UnitOperations.UnitOperations.Pump",
        "DWSIM.UnitOperations.UnitOperations.Compressor",
        "DWSIM.UnitOperations.UnitOperations.Expander",
        "DWSIM.UnitOperations.UnitOperations.Valve",
        "DWSIM.UnitOperations.UnitOperations.Pipe",
        "DWSIM.UnitOperations.UnitOperations.ComponentSeparator",
        "DWSIM.UnitOperations.UnitOperations.SolidsSeparator",
        "DWSIM.UnitOperations.UnitOperations.Filter",
        "DWSIM.UnitOperations.UnitOperations.DistillationColumn",
        "DWSIM.UnitOperations.UnitOperations.AbsorptionColumn",
        "DWSIM.UnitOperations.UnitOperations.ShortcutColumn",
        "DWSIM.UnitOperations.UnitOperations.CapeOpenUO",
        "DWSIM.UnitOperations.Reactors.Reactor_Conversion",
        "DWSIM.UnitOperations.Reactors.Reactor_Equilibrium",
        "DWSIM.UnitOperations.Reactors.Reactor_Gibbs",
        "DWSIM.UnitOperations.Reactors.Reactor_CSTR",
        "DWSIM.UnitOperations.Reactors.Reactor_PFR",
        "DWSIM.UnitOperations.SpecialOps.Recycle",
        "DWSIM.UnitOperations.SpecialOps.EnergyRecycle",
        "DWSIM.UnitOperations.SpecialOps.Adjust",
        "DWSIM.UnitOperations.SpecialOps.Spec",
    };

    // Property packages, by EXACT type name (#31 re-review: the namespace prefix it replaced would have let
    // any future class under that namespace — a flash algorithm, an auxiliary — be instantiated unreviewed).
    // EnginePackageTypes is every concrete PropertyPackage class in DWSIM 9.0.5's DWSIM.Thermodynamics.dll,
    // frozen; SanitizerTests fails if the engine's set changes, which forces a review. Each is a
    // thermodynamic model the engine ships; CAPEOPENPropertyPackage is kept for the reason CapeOpenUO is
    // (no COM on Linux, so it cannot reach an external component and the file fails or reads as a
    // placeholder). Packages in other assemblies (ThermoC, AdvancedEOS, Reaktoro) are not listed: the loader
    // resolves an unqualified name in DWSIM.Thermodynamics only.
    internal static readonly HashSet<string> EnginePackageTypes = new(StringComparer.Ordinal)
    {
        "DWSIM.Thermodynamics.PropertyPackages.BlackOilPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.CAPEOPENPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.ChaoSeaderPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.CoolPropIncompressibleMixturePropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.CoolPropIncompressiblePurePropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.CoolPropPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.GraysonStreedPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.IdealElectrolytePropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.LIQUAC2PropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.LKPPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.MODFACPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.NISTMFACPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.NRTLPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.PRSV2PropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.PRSV2VLPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.PengRobinson1978PropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.PengRobinsonLKPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.PengRobinsonPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.RaoultPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.SRKPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.SeawaterPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.SourWaterPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.SteamTablesPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.UNIFACLLPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.UNIFACPropertyPackage",
        "DWSIM.Thermodynamics.PropertyPackages.UNIQUACPropertyPackage",
        "DWSIM.Thermodynamics.WilsonPropertyPackage",
    };

    // Package names files carry that resolve to NO type in this engine (a plugin library the image does not
    // ship). The loader refuses them by name ("…library was not found") before instantiating anything, so
    // they are kept to preserve that named refusal. SanitizerTests asserts each still resolves to nothing.
    internal static readonly HashSet<string> UnresolvedPackageTypes = new(StringComparer.Ordinal)
    {
        "DWSIM.Thermodynamics.PropertyPackages.ExUNIQUACPropertyPackage",
    };

    internal static readonly HashSet<string> AllowedPackageTypes =
        new(EnginePackageTypes.Concat(UnresolvedPackageTypes), StringComparer.Ordinal);

    // Graphic objects, by EXACT type name. EngineGraphicTypes is every concrete IGraphicObject class in
    // DWSIM 9.0.5's DWSIM.Drawing.SkiaSharp.dll and DWSIM.DrawingTools.SkiaSharp.Extended.dll, frozen and
    // guarded like the packages. AllowedGraphicTypes is the reviewed subset: the shape of each allowed
    // simulation object, plus text, rectangle, tables and chart (all reported as `ignored`). Left out: the
    // graphics of script, Python-controller, sub-flowsheet, external and dynamic-mode objects (buttons,
    // gauges, inputs, switches, PID), embedded images and HTML text — none is needed to read a process.
    internal static readonly HashSet<string> EngineGraphicTypes = new(StringComparer.Ordinal)
    {
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.AnalogGaugeGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Charts.OxyPlotGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.ConnectorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.DigitalGaugeGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.HTMLTextGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.InputGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.LevelGaugeGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.AbsorptionColumnGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.AdjustGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ButtonGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CAPEOPENGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CSTRGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ComponentSeparatorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CompressorExpanderGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CompressorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ConversionReactorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CoolerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.DummyGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.EmbeddedImageGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.EnergyRecycleGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.EnergyStreamGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.EquilibriumReactorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ExternalUnitOperationGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.FilterGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.FlowsheetGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.GibbsReactorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.HeatExchangerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.HeaterCoolerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.HeaterGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.MaterialStreamGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.MixerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.OrificePlateGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.PFRGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.PIDControllerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.PipeSegmentGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.PumpGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.PythonControllerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.RectangleGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.RecycleGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.RigorousColumnGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ScriptGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ShortcutColumnGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.SolidsSeparatorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.SpecGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.SplitterGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.SpreadsheetGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.TankGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.TurbineGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ValveGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.VesselGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.SwitchGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Tables.FloatingTableGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Tables.MasterTableGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Tables.SpreadsheetTableGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Tables.TableGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.TextGraphic",
    };

    internal static readonly HashSet<string> AllowedEngineGraphicTypes = new(StringComparer.Ordinal)
    {
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Charts.OxyPlotGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.AbsorptionColumnGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.AdjustGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CAPEOPENGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CSTRGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ComponentSeparatorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CompressorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ConversionReactorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.CoolerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.EnergyRecycleGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.EnergyStreamGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.EquilibriumReactorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.FilterGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.GibbsReactorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.HeatExchangerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.HeaterGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.MaterialStreamGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.MixerGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.PFRGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.PipeSegmentGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.PumpGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.RectangleGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.RecycleGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.RigorousColumnGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ShortcutColumnGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.SolidsSeparatorGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.SpecGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.SplitterGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.TankGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.TurbineGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.ValveGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes.VesselGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Tables.FloatingTableGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Tables.MasterTableGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Tables.SpreadsheetTableGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.Tables.TableGraphic",
        "DWSIM.Drawing.SkiaSharp.GraphicObjects.TextGraphic",
    };

    // Graphic names older files carry (DWSIM 5/6 namespaces) that resolve to NO type in this engine: the
    // loader gets null and skips the graphic, instantiating nothing. Kept so those files read as they did;
    // SanitizerTests asserts each still resolves to nothing, so an engine that brings one back is reviewed.
    internal static readonly HashSet<string> UnresolvedGraphicTypes = new(StringComparer.Ordinal)
    {
        "DWSIM.DrawingTools.GraphicObjects.AbsorptionColumnGraphic",
        "DWSIM.DrawingTools.GraphicObjects.AdjustGraphic",
        "DWSIM.DrawingTools.GraphicObjects.CapeOpenUOGraphic",
        "DWSIM.DrawingTools.GraphicObjects.ComponentSeparatorGraphic",
        "DWSIM.DrawingTools.GraphicObjects.CompressorGraphic",
        "DWSIM.DrawingTools.GraphicObjects.CoolerGraphic",
        "DWSIM.DrawingTools.GraphicObjects.DistillationColumnGraphic",
        "DWSIM.DrawingTools.GraphicObjects.EmbeddedImageGraphic",
        "DWSIM.DrawingTools.GraphicObjects.EnergyRecycleGraphic",
        "DWSIM.DrawingTools.GraphicObjects.EnergyStreamGraphic",
        "DWSIM.DrawingTools.GraphicObjects.HeatExchangerGraphic",
        "DWSIM.DrawingTools.GraphicObjects.HeaterGraphic",
        "DWSIM.DrawingTools.GraphicObjects.MaterialStreamGraphic",
        "DWSIM.DrawingTools.GraphicObjects.NodeInGraphic",
        "DWSIM.DrawingTools.GraphicObjects.NodeOutGraphic",
        "DWSIM.DrawingTools.GraphicObjects.PumpGraphic",
        "DWSIM.DrawingTools.GraphicObjects.ReactorCSTRGraphic",
        "DWSIM.DrawingTools.GraphicObjects.ReactorConversionGraphic",
        "DWSIM.DrawingTools.GraphicObjects.ReactorEquilibriumGraphic",
        "DWSIM.DrawingTools.GraphicObjects.ReactorGibbsGraphic",
        "DWSIM.DrawingTools.GraphicObjects.ReactorPFRGraphic",
        "DWSIM.DrawingTools.GraphicObjects.RectangleGraphic",
        "DWSIM.DrawingTools.GraphicObjects.RecycleGraphic",
        "DWSIM.DrawingTools.GraphicObjects.ShorcutColumnGraphic",
        "DWSIM.DrawingTools.GraphicObjects.TankGraphic",
        "DWSIM.DrawingTools.GraphicObjects.TextGraphic",
        "DWSIM.DrawingTools.GraphicObjects.TurbineGraphic",
        "DWSIM.DrawingTools.GraphicObjects.ValveGraphic",
        "DWSIM.DrawingTools.GraphicObjects.VesselGraphic",
        "DWSIM.GraphicObjects.MasterTableGraphic",
        "DWSIM.GraphicObjects.SpreadsheetTableGraphic",
        "DWSIM.GraphicObjects.TableGraphic",
    };

    internal static readonly HashSet<string> AllowedGraphicTypes =
        new(AllowedEngineGraphicTypes.Concat(UnresolvedGraphicTypes), StringComparer.Ordinal);

    // DWSIM 9.0.5's whole ObjectType vocabulary, frozen. SanitizerTests fails if the engine's set
    // changes, which forces a review of AllowedSimObjectTypes against the new member.
    internal static readonly HashSet<string> ReviewedObjectTypes = new(StringComparer.Ordinal)
    {
        "NodeIn", "NodeOut", "NodeEn", "Pump", "Tank", "Vessel", "MaterialStream", "EnergyStream", "Compressor",
        "Expander", "TPVessel", "Cooler", "Heater", "Pipe", "Valve", "Nenhum", "GO_Table", "GO_Text", "GO_Image",
        "GO_FloatingTable", "OT_Adjust", "OT_Spec", "OT_Recycle", "RCT_Conversion", "RCT_Equilibrium", "RCT_Gibbs",
        "RCT_CSTR", "RCT_PFR", "HeatExchanger", "ShortcutColumn", "DistillationColumn", "AbsorptionColumn",
        "RefluxedAbsorber", "ReboiledAbsorber", "OT_EnergyRecycle", "GO_Animation", "ComponentSeparator",
        "OrificePlate", "CustomUO", "ExcelUO", "CapeOpenUO", "FlowsheetUO", "GO_MasterTable", "SolidSeparator",
        "Filter", "GO_SpreadsheetTable", "GO_Rectangle", "CompressorExpander", "HeaterCooler", "GO_Chart",
        "GO_InputControl", "External", "AnalogGauge", "DigitalGauge", "LevelGauge", "Controller_PID", "Switch",
        "Input", "GO_HTMLText", "GO_Button", "AirCooler2", "WindTurbine", "HydroelectricTurbine", "SolarPanel",
        "PEMFuelCell", "WaterElectrolyzer", "RCT_GibbsReaktoro", "EnergyMixer", "Mixer", "Splitter",
        "Controller_Python", "Dummy", "SolidOps",
    };

    /// <summary>
    /// Rewrite the untrusted flowsheet XML to the allow-list. Returns the clean XML to hand the loader
    /// and the list of what was removed. Parses with DTDs prohibited and no resolver (no XXE, no
    /// billion-laughs, no external fetch). Throws XmlException on a malformed or DTD-bearing document.
    /// </summary>
    internal static (string Xml, List<RemovedItem> Removed) Sanitize(string xml)
    {
        var removed = new List<RemovedItem>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new XmlException("no root element");

        // 1) top-level sections: keep the eight, drop the rest.
        foreach (var section in root.Elements().ToList())
        {
            if (KeepSections.Contains(section.Name.LocalName)) continue;
            if (section.Name.LocalName == "ScriptItems")
                foreach (var item in section.Elements())
                    removed.Add(new("script",
                        $"{(string?)item.Element("Title") ?? item.Element("ID")?.Value ?? "script"} / {(string?)item.Element("LinkedEventType") ?? "?"}"));
            else
                removed.Add(new("section", section.Name.LocalName));
            section.Remove();
        }

        // 2) the deserialise-into-a-named-type children, wherever they sit.
        var dyn = 0;
        foreach (var name in DropAnywhere)
            foreach (var e in root.Descendants(name).ToList()) { e.Remove(); dyn++; }
        if (dyn > 0) removed.Add(new("dynamicProperties", $"{dyn} element(s)"));

        // 3) simulation objects: instantiate only reviewed types.
        Prune(root.Element("SimulationObjects"), "SimulationObject", t => AllowedSimObjectTypes.Contains(t), removed);
        // 4) property packages and 5) graphic objects: exact reviewed type names only.
        Prune(root.Element("PropertyPackages"), "PropertyPackage", t => AllowedPackageTypes.Contains(t), removed);
        Prune(root.Element("GraphicObjects"), "GraphicObject", t => AllowedGraphicTypes.Contains(t), removed);

        return (doc.ToString(SaveOptions.DisableFormatting), removed);
    }

    /// <summary>A bare, non-assembly-qualified type name (no comma, brackets or whitespace).</summary>
    private static bool Safe(string type) => type.Length > 0 && type.IndexOfAny([',', '[', ']', ' ']) < 0;

    private static void Prune(XElement? container, string child, Func<string, bool> allowed, List<RemovedItem> removed)
    {
        if (container is null) return;
        foreach (var e in container.Elements(child).ToList())
        {
            var type = (e.Element("Type")?.Value ?? "").Trim();
            if (Safe(type) && allowed(type)) continue;
            var name = e.Element("Name")?.Value ?? e.Element("Tag")?.Value ?? "?";
            removed.Add(new("unsupportedType", $"{name} ({(type.Length > 0 ? type : "no type")})"));
            e.Remove();
        }
    }
}
