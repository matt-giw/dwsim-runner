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
/// <param name="ExternalId">
/// Set ONLY for an EXTERNAL unit operation, and it is the engine's registry key ("Water
/// Electrolyzer"), not the wire type.
///
/// DWSIM builds a unit op two ways and the difference is not cosmetic. The ordinary path is
/// `AddObject(ObjectType, x, y, tag)`. An external op needs `AddObject(ObjectType.External, x, y,
/// ID, tag)` — the five-argument overload — because the id is what selects it from the plugin
/// registry. Measured: `AddObject(ObjectType.WaterElectrolyzer, ...)` RETURNS AN OBJECT, so it looks
/// like it worked, and that object has ZERO graphic connectors, so the first attempt to connect its
/// water inlet fails with "Index was out of range". Constructed-but-unconnectable is the shape to
/// watch for, and it is why the engine inventory reports this type instantiable.
/// </param>
public sealed record UnitOpDef(string Type, string DisplayName, ObjectType ObjectType,
    PortDef[] Ports, ParamDef[] Parameters, bool RequiresReactionSet, string? ExternalId = null);

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
            // 099 US5 — `StreamFlowSpec`, not `StreamMassFlowSpec`: the latter is the OpMode ENUM
            // MEMBER, and the plan named the mode where the property goes. The escape hatch selects
            // the mode; this is the setpoint it reads.
            [P("splitRatio1", "dimensionless", false, "Ratios"),
             P("outletMassFlow", "massFlow", false, "StreamFlowSpec")], false),

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
             P("efficiency", "dimensionless", false, "Eficiencia", "Efficiency"),
             // 099 US5 — COOLER ONLY. `OutletVaporFraction` is declared on `Cooler` and NOT on
             // `Heater`, measured: setting it on a heater moved nothing, because the escape hatch
             // selected the mode and the reflection then had no property to write. A parameter on
             // the wrong type is the silent-setpoint bug wearing the right name.
             P("outletVaporFraction", "dimensionless", false, "OutletVaporFraction")], false),

        new UnitOpDef("heatExchanger", "Heat Exchanger", ObjectType.HeatExchanger,
            [In("Inlet 1", 0), In("Inlet 2", 1), Out("Outlet 1", 0), Out("Outlet 2", 1)],
            // `overallUA` was NEITHER a UA NOR a power, and it is now two parameters that are what
            // the engine actually has. Measured, not read: DWSIM's own `SetPropertyValue` converts
            // `OverallCoefficient` through `IUnitsOfMeasure.heat_transf_coeff` — W/[m2.K], an area-
            // specific coefficient — and the `CalcBothTemp_UA` branch of `Calculate` reads BOTH
            // `Area` and `OverallCoefficient` and uses their product. There is no UA property.
            //
            // The old declaration was wrong twice over, and the two errors hid each other:
            //   - `overallUA: 2500` bare gave the exactly-right duty (172.1 kW against 172.4 kW of
            //     counter-flow NTU theory) — but only because `Area` defaults to 1.0 m2, so U and UA
            //     are numerically equal in the default case and nowhere else.
            //   - `overallUA: {2500, "W"}` gave 0.2 kW: the "power" unitType converted W→kW, so
            //     U arrived as 2.5. A 1000x error, converged, no warning.
            //   - `overallUA: {2500, "W/K"}` — the honest unit — was REFUSED as an unknown power
            //     unit. The parameter was only reachable by declining to say what you meant.
            [P("coldSideOutletTemperature", "temperature", false, "ColdSideOutletTemperature"),
             P("hotSideOutletTemperature", "temperature", false, "HotSideOutletTemperature"),
             P("overallHeatTransferCoefficient", "heatTransferCoefficient", false, "OverallCoefficient"),
             P("area", "area", false, "Area")], false),

        // Spec 099 US1 — the P0 entry. `ElectrolyzerStack` carried "DWSIM has no electrolyzer unit
        // op" for a year; the engine has shipped this since 9.0, in the DLL already vendored here.
        //
        // NO ENERGY PORT, deliberately. The engine takes its power on input connector 1 and leaves
        // the graphic energy connector inactive, but the document's standing invariant is that an
        // energy port is a PARAMETER, never a nozzle (spec 024 FR-009). So `powerInput` is a
        // parameter with no engine property, and `ElectrolyzerConfigurator` synthesizes the energy
        // stream the engine demands. It is `required: true` because absence is not a degraded solve
        // — it is a null dereference inside `Calculate`, and the runner must refuse first.
        //
        // `voltage` is the STACK TOTAL, not the per-cell voltage. `CellVoltage` is settable on the
        // engine class and is REPORTED — every property on this type has a setter, so a setter proves
        // nothing about what the engine reads. Binding a datasheet's 1.9 V here would send 1.9 where
        // ~988 is expected, converge, and return a current 520x too large. The app derives the total.
        //
        // `efficiency` binds `InputEfficiency`, NOT `Efficiency`: the latter is what the engine
        // reports back. Same trap as the voltage pair, one property apart.
        new UnitOpDef("waterElectrolyzer", "Water Electrolyzer", ObjectType.WaterElectrolyzer,
            [In("Water Inlet", 0), Out("Hydrogen-Rich Outlet", 0), Out("Oxygen-Rich Outlet", 1)],
            [P("powerInput", "power", true),
             P("voltage", "voltage", false, "Voltage"),
             P("cellCount", "integer", false, "NumberOfCells"),
             P("efficiency", "dimensionless", false, "InputEfficiency")], false,
            ExternalId: "Water Electrolyzer"),

        // Spec 099 US2 — one engine type un-strands THREE equipment classes: ReverseOsmosisUnit,
        // Adsorber and IonExchanger all draw today and vanish from every solve, because a
        // per-compound split is exactly what they are and no exposed type does one.
        //
        // OUTLET NAMES ARE POSITIONAL ON PURPOSE. Not vapor/liquid, not permeate/reject: the
        // mapper's NOZZLE-side hints already match `permeate` as a vapour hint and `reject` as a
        // liquid one, so a vapor/liquid-shaped CATALOG name would make the name-matching assignment
        // compete with the positional plan, and which won would depend on regex details rather than
        // on the engineer's drawing. `Outlet 2` is optional so an IonExchanger — one in, one out —
        // leaves nothing unpiped.
        //
        // `separationSpecs` is a per-compound DICTIONARY, which name→property reflection cannot
        // express, so it has a bespoke configurator like the column and the electrolyzer.
        //
        // BOTH OUTLETS ARE REQUIRED, and 099's contract said otherwise. It made `Outlet 2` optional
        // so an IonExchanger — one in, one out — would leave nothing unpiped, judging a synthesized
        // product stream to be "noise for the common case". Measured: with the second outlet
        // unpiped the engine throws a NullReferenceException from inside `Calculate` — it
        // dereferences that outlet unconditionally, exactly as the electrolyzer does its power.
        // So the mapper synthesizes the stream, which it already does for any unpiped required
        // outlet and reports as INFORMATIONAL. The "noise" is a named, harmless drop; the
        // alternative was a .NET stack trace.
        new UnitOpDef("componentSeparator", "Component Separator", ObjectType.ComponentSeparator,
            [In("Inlet", 0), Out("Outlet 1", 0), Out("Outlet 2", 1)],
            [P("specifiedStreamIndex", "integer", false, "SpecifiedStreamIndex"),
             P("separationSpecs", "string", false)], false),

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
             P("pressureDrop", "pressure", false, "DeltaP"),
             // 099 US5. ONE `Kv`, not the `Kv_Liquid`/`Kv_Gas` pair the plan named — the engine has
             // a single flow coefficient and reports an `ActualKv` back. Dimensionless: a Kv is a
             // number read off a valve datasheet.
             P("kv", "dimensionless", false, "Kv"),
             P("openingPct", "dimensionless", false, "OpeningPct")], false),

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
             P("headspace", "volume", false, "Headspace"),   // vapor volume; required by the engine for gas-phase feeds
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

        // All distillationColumn ports and parameters are bound by
        // ColumnConfigurator (dedicated engine methods) — indexes unused.
        new UnitOpDef("distillationColumn", "Distillation Column (rigorous)", ObjectType.DistillationColumn,
            [In("Feed", 0), Out("Distillate", 0), Out("Bottoms", 1),
             EnergyOut("Condenser Duty", 0), EnergyIn("Reboiler Duty", 10)],
            [P("numberOfStages", "integer", true),
             P("feedStage", "integer", true),
             P("refluxRatio", "dimensionless", true),                  // condenser spec ("Reflux Ratio")
             P("condenserPressure", "pressure", true),
             P("reboilerPressure", "pressure", true),
             P("distillateMolarFlow", "molarFlow", false),             // condenser spec alternative
             P("bottomsMolarFlow", "molarFlow", false)], false),       // reboiler spec ("Product Molar Flow Rate")

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
        var list = engineNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var exact = list.FirstOrDefault(n => string.Equals(n, idOrName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var known = Known.FirstOrDefault(k => string.Equals(k.Id, idOrName, StringComparison.OrdinalIgnoreCase));
        if (known.Match is not null)
            return list.FirstOrDefault(n => n.Contains(known.Match, StringComparison.OrdinalIgnoreCase));
        return null;
    }
}
