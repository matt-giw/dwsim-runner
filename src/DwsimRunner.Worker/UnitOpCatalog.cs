// dwsim-runner Worker — GPL-3.0
// The headless-safe unit-operation allowlist (research.md R8): for each type,
// the stable wire name, the DWSIM ObjectType used with AddObject, the port map
// (name → connector direction/kind/index for ConnectFeed*/ConnectProduct*),
// and the settable parameters (friendly name → candidate .NET property names
// on the DWSIM class, tried in order via reflection).
//
// Deliberately excluded (v1): adjust, spec, energy recycle, and everything
// GUI-coupled or dynamics-only. GraphicObject connector indexes follow the
// DWSIM 9 classic layouts; the integration suite exercises the load-bearing
// ones (separator, heater, columns, reactors).

using DWSIM.Interfaces.Enums.GraphicObjects;

namespace DwsimRunner.Worker;

public sealed record PortDef(string Name, string Direction, string Accepts, bool Required, int Index);
public sealed record ParamDef(string Name, string UnitType, bool Required, string[] EngineProperties);
public sealed record UnitOpDef(string Type, string DisplayName, ObjectType ObjectType,
    PortDef[] Ports, ParamDef[] Parameters, bool RequiresReactionSet);

public static class UnitOpCatalog
{
    private static PortDef In(string name, int idx, bool required = true) => new(name, "in", "material", required, idx);
    private static PortDef Out(string name, int idx, bool required = true) => new(name, "out", "material", required, idx);
    private static PortDef EnergyIn(string name, int idx) => new(name, "in", "energy", false, idx);
    private static PortDef EnergyOut(string name, int idx) => new(name, "out", "energy", false, idx);
    private static ParamDef P(string name, string unitType, bool required, params string[] engineProps) =>
        new(name, unitType, required, engineProps);

    public static readonly Dictionary<string, UnitOpDef> Types = new[]
    {
        new UnitOpDef("mixer", "Stream Mixer", ObjectType.Mixer,
            [In("Inlet 1", 0), In("Inlet 2", 1, required: false), In("Inlet 3", 2, required: false),
             In("Inlet 4", 3, required: false), In("Inlet 5", 4, required: false), In("Inlet 6", 5, required: false),
             Out("Outlet", 0)],
            [], false),

        new UnitOpDef("splitter", "Stream Splitter", ObjectType.Splitter,
            [In("Inlet", 0), Out("Outlet 1", 0), Out("Outlet 2", 1, required: false), Out("Outlet 3", 2, required: false)],
            [P("splitRatio1", "dimensionless", false, "Ratios")], false),

        new UnitOpDef("separator", "Gas-Liquid Separator", ObjectType.Vessel,
            [In("Inlet", 0), Out("Vapor Outlet", 0), Out("Liquid Outlet", 1), EnergyIn("Energy Inlet", 6)],
            [], false),

        new UnitOpDef("tank", "Storage Tank", ObjectType.Tank,
            [In("Inlet", 0), Out("Outlet", 0)],
            [P("pressureDrop", "pressure", false, "DeltaP")], false),

        new UnitOpDef("heater", "Heater", ObjectType.Heater,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("heatDuty", "power", false, "DeltaQ"),
             P("pressureDrop", "pressure", false, "DeltaP"),
             P("efficiency", "dimensionless", false, "Eficiencia", "Efficiency")], false),

        new UnitOpDef("cooler", "Cooler", ObjectType.Cooler,
            [In("Inlet", 0), Out("Outlet", 0), EnergyOut("Energy Outlet", 1)],
            [P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("heatDuty", "power", false, "DeltaQ"),
             P("pressureDrop", "pressure", false, "DeltaP"),
             P("efficiency", "dimensionless", false, "Eficiencia", "Efficiency")], false),

        new UnitOpDef("heatExchanger", "Heat Exchanger", ObjectType.HeatExchanger,
            [In("Inlet 1", 0), In("Inlet 2", 1), Out("Outlet 1", 0), Out("Outlet 2", 1)],
            [P("coldSideOutletTemperature", "temperature", false, "ColdSideOutletTemperature"),
             P("hotSideOutletTemperature", "temperature", false, "HotSideOutletTemperature"),
             P("overallUA", "power", false, "OverallCoefficient")], false),

        new UnitOpDef("pump", "Pump", ObjectType.Pump,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("outletPressure", "pressure", false, "Pout", "POut"),
             P("pressureIncrease", "pressure", false, "DeltaP"),
             P("efficiency", "dimensionless", false, "Eficiencia", "Efficiency")], false),

        new UnitOpDef("compressor", "Compressor", ObjectType.Compressor,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("outletPressure", "pressure", false, "POut", "Pout"),
             P("pressureIncrease", "pressure", false, "DeltaP"),
             P("adiabaticEfficiency", "dimensionless", false, "AdiabaticEfficiency", "EficienciaAdiabatica")], false),

        new UnitOpDef("expander", "Expander (Turbine)", ObjectType.Expander,
            [In("Inlet", 0), Out("Outlet", 0), EnergyOut("Energy Outlet", 1)],
            [P("outletPressure", "pressure", false, "POut", "Pout"),
             P("pressureDecrease", "pressure", false, "DeltaP"),
             P("adiabaticEfficiency", "dimensionless", false, "AdiabaticEfficiency", "EficienciaAdiabatica")], false),

        new UnitOpDef("valve", "Valve", ObjectType.Valve,
            [In("Inlet", 0), Out("Outlet", 0)],
            [P("outletPressure", "pressure", false, "OutletPressure", "Pout", "POut"),
             P("pressureDrop", "pressure", false, "DeltaP")], false),

        new UnitOpDef("pipe", "Pipe Segment", ObjectType.Pipe,
            [In("Inlet", 0), Out("Outlet", 0)],
            [P("length", "length", false, "Length"),
             P("diameter", "length", false, "Diameter")], false),

        new UnitOpDef("orificePlate", "Orifice Plate", ObjectType.OrificePlate,
            [In("Inlet", 0), Out("Outlet", 0)],
            [P("orificeDiameter", "length", false, "OrificeDiameter")], false),

        new UnitOpDef("reactorConversion", "Conversion Reactor", ObjectType.RCT_Conversion,
            [In("Inlet", 0), Out("Vapor Outlet", 0), Out("Liquid Outlet", 1), EnergyIn("Energy Inlet", 1)],
            [P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("pressureDrop", "pressure", false, "DeltaP")], true),

        new UnitOpDef("reactorEquilibrium", "Equilibrium Reactor", ObjectType.RCT_Equilibrium,
            [In("Inlet", 0), Out("Vapor Outlet", 0), Out("Liquid Outlet", 1), EnergyIn("Energy Inlet", 1)],
            [P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("pressureDrop", "pressure", false, "DeltaP")], true),

        new UnitOpDef("reactorGibbs", "Gibbs Reactor", ObjectType.RCT_Gibbs,
            [In("Inlet", 0), Out("Vapor Outlet", 0), Out("Liquid Outlet", 1), EnergyIn("Energy Inlet", 1)],
            [P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("pressureDrop", "pressure", false, "DeltaP")], false),

        new UnitOpDef("reactorCSTR", "CSTR", ObjectType.RCT_CSTR,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("volume", "volume", false, "Volume"),
             P("outletTemperature", "temperature", false, "OutletTemperature")], true),

        new UnitOpDef("reactorPFR", "PFR", ObjectType.RCT_PFR,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("volume", "volume", false, "Volume"),
             P("length", "length", false, "Length")], true),

        new UnitOpDef("shortcutColumn", "Shortcut Column (FUG)", ObjectType.ShortcutColumn,
            [In("Feed", 0), Out("Distillate", 0), Out("Bottoms", 1),
             EnergyOut("Condenser Duty", 2), EnergyIn("Reboiler Duty", 1)],
            [P("refluxRatio", "dimensionless", true, "m_refluxratio", "RefluxRatio"),
             P("lightKey", "string", true, "m_lightkey", "LightKey"),
             P("heavyKey", "string", true, "m_heavykey", "HeavyKey"),
             P("lightKeyMolarFracInBottoms", "dimensionless", false, "m_lightkeymolarfrac", "LightKeyMolarFrac"),
             P("heavyKeyMolarFracInDistillate", "dimensionless", false, "m_heavykeymolarfrac", "HeavyKeyMolarFrac"),
             P("condenserPressure", "pressure", true, "m_condenserpressure", "CondenserPressure"),
             P("reboilerPressure", "pressure", true, "m_boilerpressure", "ReboilerPressure")], false),

        new UnitOpDef("distillationColumn", "Distillation Column (rigorous)", ObjectType.DistillationColumn,
            [In("Feed", 0), Out("Distillate", 0), Out("Bottoms", 1),
             EnergyOut("Condenser Duty", 0), EnergyIn("Reboiler Duty", 10)],
            [P("numberOfStages", "integer", true, "NumberOfStages"),
             P("feedStage", "integer", true),                          // applied by the builder, not reflection
             P("refluxRatio", "dimensionless", true),                  // condenser spec, applied by the builder
             P("condenserPressure", "pressure", true, "CondenserPressure"),
             P("reboilerPressure", "pressure", true, "ReboilerPressure"),
             P("distillateMolarFlow", "molarFlow", false)], false),    // condenser spec alternative

        new UnitOpDef("recycle", "Recycle", ObjectType.OT_Recycle,
            [In("Inlet", 0), Out("Outlet", 0)],
            [], false),
    }.ToDictionary(d => d.Type, d => d, StringComparer.Ordinal);

    /// <summary>Serializable catalog view (worker `catalog` mode payload).</summary>
    public static object ToPayload() => Types.Values
        .OrderBy(d => d.Type, StringComparer.Ordinal)
        .Select(d => new
        {
            type = d.Type,
            displayName = d.DisplayName,
            ports = d.Ports.Select(p => new { name = p.Name, direction = p.Direction, accepts = p.Accepts, required = p.Required }),
            parameters = d.Parameters.Select(p => new { name = p.Name, unitType = p.UnitType, required = p.Required }),
            requiresReactionSet = d.RequiresReactionSet,
        })
        .ToList();
}

/// <summary>Property-package id/description map: engine display names → stable
/// short ids crossing the wire. Unlisted packages fall back to id = name.</summary>
public static class PackageCatalog
{
    public static readonly (string Id, string Match, string Description)[] Known =
    [
        ("PR", "Peng-Robinson (PR)", "Cubic EOS; hydrocarbons and light gases"),
        ("PR78", "Peng-Robinson 1978", "Cubic EOS (1978 revision)"),
        ("SRK", "Soave-Redlich-Kwong", "Cubic EOS"),
        ("NRTL", "NRTL", "Activity model for polar/non-ideal mixtures"),
        ("UNIQUAC", "UNIQUAC", "Activity model"),
        ("UNIFAC", "UNIFAC", "Group-contribution activity model"),
        ("WILSON", "Wilson", "Activity model (fully miscible liquids)"),
        ("RAOULT", "Raoult", "Raoult's law (ideal solutions)"),
        ("STEAM", "Steam Tables", "IAPWS-IF97 steam tables (water/steam only)"),
        ("COOLPROP", "CoolProp", "CoolProp reference equations of state"),
        ("LKP", "Lee-Kesler-Pl", "Lee-Kesler-Plöcker corresponding states"),
        ("CS", "Chao-Seader", "Chao-Seader correlation"),
        ("GS", "Grayson-Streed", "Grayson-Streed correlation"),
        ("SOURWATER", "Sour Water", "Sour water systems"),
        ("SEAWATER", "Seawater", "Seawater model"),
        ("BLACKOIL", "Black Oil", "Black oil model (petroleum)"),
        ("IDEAL", "Ideal", "Ideal gas / ideal solution"),
    ];

    public static (string Id, string Description) Classify(string engineName)
    {
        foreach (var (id, match, description) in Known)
            if (engineName.Contains(match, StringComparison.OrdinalIgnoreCase))
                return (id, description);
        return (engineName, "");
    }

    /// <summary>Resolve a wire id (or full display name) back to the engine name.</summary>
    public static string? Resolve(string idOrName, IEnumerable<string> engineNames)
    {
        var list = engineNames.ToList();
        var exact = list.FirstOrDefault(n => string.Equals(n, idOrName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var known = Known.FirstOrDefault(k => string.Equals(k.Id, idOrName, StringComparison.OrdinalIgnoreCase));
        if (known.Match is not null)
            return list.FirstOrDefault(n => n.Contains(known.Match, StringComparison.OrdinalIgnoreCase));
        return null;
    }
}
