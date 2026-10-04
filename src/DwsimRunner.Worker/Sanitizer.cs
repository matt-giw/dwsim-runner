// dwsim-runner Worker — GPL-3.0
// iskra spec 285 (ISK-485), #31 review finding 1 — THE SECURITY BOUNDARY of the read path.
//
// DWSIM's loader is not a parser: loading a file runs code it names (a linked "Simulation Opened"
// script through IronPython; a type the file names, instantiated through Type.GetType + CreateInstance;
// a value deserialised into a type the file names; a nested flowsheet it loads from a path). So the
// bytes are rewritten HERE, before the loader sees them, to an ALLOW-LIST: only the sections the read
// needs, and only object/package/graphic types reviewed as safe to instantiate. Everything removed is
// reported so nothing is silently dropped. Re-review on every DWSIM version change (runner is 9.0.5).

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

    private const string AllowedPackagePrefix = "DWSIM.Thermodynamics.PropertyPackages.";

    private static readonly string[] AllowedGraphicPrefixes =
        { "DWSIM.Drawing.SkiaSharp.GraphicObjects.", "DWSIM.DrawingTools.GraphicObjects.", "DWSIM.GraphicObjects." };

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
        // 4) property packages: the DWSIM package namespace only.
        Prune(root.Element("PropertyPackages"), "PropertyPackage", t => Safe(t) && t.StartsWith(AllowedPackagePrefix, StringComparison.Ordinal), removed);
        // 5) graphic objects: the three reviewed drawing namespaces only.
        Prune(root.Element("GraphicObjects"), "GraphicObject", t => Safe(t) && AllowedGraphicPrefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal)), removed);

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
