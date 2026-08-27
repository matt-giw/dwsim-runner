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
    PortDef[] Ports, ParamDef[] Parameters, bool RequiresReactionSet, string? ExternalId = null,
    CalcModeDef? CalcMode = null);

/// <summary>
/// Spec 199 — the calculation mode as a declared INPUT.
///
/// Every DWSIM unit op with a calculation mode reads exactly the inputs that mode selects and
/// ignores the rest, with no error and no warning. Before 199 the mode was never an input: it was
/// INFERRED from whichever parameter happened to be set, through seven hand-written hatches. So a
/// mode was reachable only if it was the constructed default or a hatch flipped to it — 37 of 59
/// selections were unreachable, and five parameters that ARE declared were measured inert because
/// they select a mode nothing ever chose.
/// </summary>
/// <param name="ClrProperty">
/// The engine property to write. FOUR different names across the catalogue —
/// <c>CalcMode</c>, <c>CalculationMode</c>, <c>ReactorOperationMode</c>, <c>OperationMode</c> —
/// which is itself why this has to be declared per unit op rather than guessed.
/// </param>
/// <param name="EnumType">
/// The engine's own enum. The allowed values are REFLECTED off this and never listed here:
/// hand-copying them is prohibited by FR-001, and research R1 shows why it would be unsafe even if
/// it were allowed. Same member name, different ordinal per unit op (<c>OutletPressure</c> is 1 on
/// a Pump and 0 on a Compressor); same concept, different spelling (<c>Delta_P</c> vs <c>DeltaP</c>).
/// Any encoding shared across unit ops — integer OR string — is wrong.
/// </param>
/// <param name="Default">
/// The mode a document with NO explicit mode actually gets, as a wire name. **Measured, not derived
/// from the ordinal.** R1 originally claimed "ordinal 0 is the constructor default in each case" and
/// Vessel disproves it: its ordinal 0 is Adiabatic, but DWSIM stores ordinal 1 (Legacy) — the
/// default spec 166 measured and worked around by forcing Adiabatic at creation. This field is what
/// FR-004 promises back-compat for, so an assumed value here would be a wrong answer wearing a
/// contract.
/// </param>
/// <param name="Always">
/// Parameters EVERY mode reads. A mode selects which SPECIFICATION the engine solves for; it does
/// not switch off the ratings. A pump's <c>efficiency</c> is read whether it is sized by pressure
/// rise or by power, and filtering it out with the unselected setpoint would silently change the
/// answer of every flowsheet that sets one.
/// </param>
/// <param name="Consumes">
/// Wire mode name → the parameters ONLY that mode reads. An EMPTY array is meaningful and must not
/// be pruned: it is how <c>energyStream</c>, <c>curves</c>, <c>head</c> and <c>pressureRatio</c> say
/// "selectable, and reads none of your scalar parameters". Absence and emptiness are different —
/// absence is a missing declaration and fails the catalog test.
/// </param>
/// <param name="Infer">
/// Ordered fallback for FR-004, used ONLY when the document supplies no explicit mode: the first
/// rule whose parameter is present wins. These are the seven pre-199 hatches, moved out of
/// <c>FlowsheetBuilder</c> so that one file no longer holds both the rule and its exception.
/// </param>
public sealed record CalcModeDef(
    string ClrProperty,
    Type EnumType,
    string Default,
    string[] Always,
    Dictionary<string, string[]> Consumes,
    (string Param, string Mode)[] Infer)
{
    /// <summary>Every mode the engine declares, in ordinal order, with its wire name.</summary>
    public IEnumerable<(string Name, string EngineMember, int Ordinal)> Modes() =>
        Enum.GetValues(EnumType).Cast<object>()
            .Select(v => (UnitOpCatalog.NormalizeMode(v.ToString()!), v.ToString()!, (int)v))
            .OrderBy(t => t.Item3);

    /// <summary>Wire name → engine member, WITHIN this unit op. Never across.</summary>
    public bool TryResolve(string wireName, out string? engineMember)
    {
        engineMember = Enum.GetNames(EnumType)
            .FirstOrDefault(m => string.Equals(UnitOpCatalog.NormalizeMode(m), wireName, StringComparison.Ordinal));
        return engineMember is not null;
    }

    /// <summary>Does <paramref name="mode"/> read <paramref name="param"/>?</summary>
    public bool Reads(string mode, string param) =>
        Always.Contains(param, StringComparer.OrdinalIgnoreCase)
        || (Consumes.TryGetValue(mode, out var only) && only.Contains(param, StringComparer.OrdinalIgnoreCase));

    /// <summary>Which modes WOULD read it — the half of an error message that makes it an instruction.</summary>
    public string[] ModesReading(string param) =>
        Consumes.Where(kv => kv.Value.Contains(param, StringComparer.OrdinalIgnoreCase))
            .Select(kv => kv.Key).OrderBy(m => m, StringComparer.Ordinal).ToArray();
}

public static class UnitOpCatalog
{
    private static PortDef In(string name, int idx, bool required = true) => new(name, "in", "material", required, idx);
    private static PortDef Out(string name, int idx, bool required = true) => new(name, "out", "material", required, idx);
    private static PortDef EnergyIn(string name, int idx, bool required = false) => new(name, "in", "energy", required, idx);
    private static PortDef EnergyOut(string name, int idx, bool required = false) => new(name, "out", "energy", required, idx);
    private static ParamDef P(string name, string unitType, bool required, params string[] engineProps) =>
        new(name, unitType, required, engineProps);

    // ── 199: calculation modes ──────────────────────────────────────────────────────────────────
    // The VALUES are never written here — they are reflected off the engine enum (FR-001). What is
    // declared is which parameters each mode reads, which is a fact about the engine that no
    // reflection can recover, and the inference fallback that keeps pre-199 documents solving.
    //
    // `Default` is the mode a document with NO explicit mode gets. Measured, not derived from the
    // ordinal — see CalcModeDef's remarks and the Vessel case.

    private static readonly (string, string)[] NoInference = [];

    // Pressure changers. `efficiency` is Always: a mode picks the SPECIFICATION, not the ratings,
    // and filtering the efficiency out with the unselected setpoint would change every answer.
    private static readonly CalcModeDef PumpModes = new(
        "CalcMode", typeof(DWSIM.UnitOperations.UnitOperations.Pump.CalculationMode),
        Default: "deltaP", Always: ["efficiency"],
        Consumes: new() {
            ["deltaP"] = ["pressureIncrease"], ["outletPressure"] = ["outletPressure"],
            ["energyStream"] = [], ["curves"] = [], ["power"] = [],
        },
        // Pre-199 a pump had NO hatch at all, which is why `outletPressure` measured inert: the
        // catalog declared it, the engine constructed in Delta_P, and the value was accepted and
        // ignored. Inferring it is new behaviour and it is the FIX, not a regression — a document
        // that sets only `outletPressure` currently gets a silently wrong answer.
        Infer: [("pressureIncrease", "deltaP"), ("outletPressure", "outletPressure")]);

    private static readonly CalcModeDef CompressorModes = new(
        "CalcMode", typeof(DWSIM.UnitOperations.UnitOperations.Compressor.CalculationMode),
        Default: "outletPressure", Always: ["adiabaticEfficiency"],
        Consumes: new() {
            ["outletPressure"] = ["outletPressure"], ["deltaP"] = ["pressureIncrease"],
            ["energyStream"] = [], ["powerRequired"] = [], ["head"] = [], ["curves"] = [],
            ["pressureRatio"] = [],
        },
        Infer: [("pressureIncrease", "deltaP"), ("outletPressure", "outletPressure")]);

    private static readonly CalcModeDef ExpanderModes = new(
        "CalcMode", typeof(DWSIM.UnitOperations.UnitOperations.Expander.CalculationMode),
        Default: "outletPressure", Always: ["adiabaticEfficiency"],
        Consumes: new() {
            ["outletPressure"] = ["outletPressure"], ["deltaP"] = ["pressureDecrease"],
            ["powerGenerated"] = [], ["head"] = [], ["curves"] = [], ["pressureRatio"] = [],
        },
        Infer: [("pressureDecrease", "deltaP"), ("outletPressure", "outletPressure")]);

    private static readonly CalcModeDef ValveModes = new(
        "CalcMode", typeof(DWSIM.UnitOperations.UnitOperations.Valve.CalculationMode),
        Default: "deltaP", Always: [],
        Consumes: new() {
            ["deltaP"] = ["pressureDrop"], ["outletPressure"] = ["outletPressure"],
            // One `Kv` and four correlations to apply it with — the phase is the engine's answer,
            // not the engineer's input, which is why the pre-199 hatch always chose Kv_General.
            // Exposing the mode is what makes the other three reachable at all.
            //
            // `openingPct` is declared on the Kv modes because that is where D3 says it belongs, and
            // D3 is NOT yet settled: it measured `inert` with `echoes: false`, consistent with
            // needing `DefinedOpeningKvRelationShipType` as a SECOND property. If setting the mode
            // alone does not move it, that is D3 answered and its own spec's problem.
            ["kvLiquid"] = ["kv", "openingPct"], ["kvGas"] = ["kv", "openingPct"],
            ["kvSteam"] = ["kv", "openingPct"], ["kvGeneral"] = ["kv", "openingPct"],
        },
        Infer: [("kv", "kvGeneral"), ("pressureDrop", "deltaP"), ("outletPressure", "outletPressure")]);

    // Thermal. Note the ordinals differ between the two (research R1): EnergyStream is 2 on Heater
    // and 4 on Cooler; OutletVaporFraction is 3 vs 2. Two declarations, never one shared table.
    private static readonly CalcModeDef HeaterModes = new(
        "CalcMode", typeof(DWSIM.UnitOperations.UnitOperations.Heater.CalculationMode),
        Default: "heatAdded", Always: ["pressureDrop", "efficiency"],
        Consumes: new() {
            ["heatAdded"] = ["heatDuty"], ["outletTemperature"] = ["outletTemperature"],
            ["energyStream"] = [], ["outletVaporFraction"] = [], ["temperatureChange"] = [],
            ["heatAddedRemoved"] = ["heatDuty"],
        },
        Infer: [("outletTemperature", "outletTemperature"), ("heatDuty", "heatAdded")]);

    private static readonly CalcModeDef CoolerModes = new(
        "CalcMode", typeof(DWSIM.UnitOperations.UnitOperations.Cooler.CalculationMode),
        Default: "heatRemoved", Always: ["pressureDrop", "efficiency"],
        Consumes: new() {
            ["heatRemoved"] = ["heatDuty"], ["outletTemperature"] = ["outletTemperature"],
            // The COOLER declares `outletVaporFraction` and the heater does not — the catalog
            // already reflects that asymmetry (FlowsheetBuilder's own comment says so), and the
            // map must not invent a parameter to make the two look alike.
            ["outletVaporFraction"] = ["outletVaporFraction"],
            ["temperatureChange"] = [], ["energyStream"] = [],
        },
        // `outletVaporFraction` was the sixth hatch and only the COOLER declares the parameter,
        // so only the cooler carries the rule.
        Infer: [("outletTemperature", "outletTemperature"), ("outletVaporFraction", "outletVaporFraction"),
                ("heatDuty", "heatRemoved")]);

    // 166 — the runner forces Adiabatic at creation because DWSIM constructs in Legacy, whose
    // mixed-feed (T,P) flash is ill-posed for a pure compound on the saturation line. So iskra's
    // effective default is neither the enum's ordinal 0 as a matter of numbering nor DWSIM's own,
    // and `Default` states the one that is true HERE.
    private static readonly CalcModeDef SeparatorModes = new(
        "CalculationMode", typeof(DWSIM.UnitOperations.UnitOperations.Vessel.CalculationModes),
        // The separator declares NO catalog parameters, so every mode consumes nothing. The mode
        // is still worth exposing: it changes which FLASH the vessel runs, which is the whole of
        // spec 166, and none of that is expressible as a parameter.
        Default: "adiabatic", Always: [],
        Consumes: new() {
            ["adiabatic"] = [], ["legacy"] = [],
            ["heatingCoolingIsothermic"] = [], ["heatingCoolingIsobaric"] = [],
        },
        Infer: NoInference);

    /// <summary>
    /// The five reactors share ONE engine enum (<c>Reactors.OperationMode</c>), so their MODE lists
    /// are identical by construction. They do NOT share a parameter set, and assuming they did was
    /// wrong on first write: `reactorConversion`/`Equilibrium`/`Gibbs` declare
    /// {outletTemperature, pressureDrop}, `reactorCSTR` declares {volume, headspace,
    /// outletTemperature} with NO pressureDrop, and `reactorPFR` declares {volume, length} — no
    /// outletTemperature at all.
    ///
    /// So `reactorPFR` can SELECT the `outletTemperature` mode and has no way to state the
    /// temperature it should hold. That is a real gap, and it is a CATALOG-PARAMETER gap rather
    /// than the mode gap this spec closes — recorded rather than papered over, because a map that
    /// named a parameter the type does not declare would read as "this mode consumes nothing" at
    /// the filter and hide it.
    /// </summary>
    private static CalcModeDef ReactorModes(string[] always, bool hasOutletTemperature) => new(
        "ReactorOperationMode", typeof(DWSIM.UnitOperations.Reactors.OperationMode),
        Default: "isothermic", Always: always,
        Consumes: new() {
            ["isothermic"] = [], ["adiabatic"] = [],
            ["outletTemperature"] = hasOutletTemperature ? ["outletTemperature"] : [],
            ["nonIsothermalNonAdiabatic"] = [],
        },
        Infer: hasOutletTemperature ? [("outletTemperature", "outletTemperature")] : []);

    private static readonly CalcModeDef SplitterModes = new(
        "OperationMode", typeof(DWSIM.UnitOperations.UnitOperations.Splitter.OpMode),
        Default: "splitRatios", Always: [],
        Consumes: new() {
            ["splitRatios"] = ["splitRatio1"], ["streamMassFlowSpec"] = ["outletMassFlow"],
            // Reachable for the first time: the catalog has one `outletMassFlow` setpoint and the
            // engine reads it as MASS or MOLE depending on the mode. Pre-199 only the mass reading
            // was selectable, so a mole-flow split could not be expressed at all.
            ["streamMoleFlowSpec"] = ["outletMassFlow"],
        },
        Infer: [("outletMassFlow", "streamMassFlowSpec")]);

    private static readonly CalcModeDef OrificePlateModes = new(
        "CalculationMethod", typeof(DWSIM.UnitOperations.UnitOperations.OrificePlate.CalcMethod),
        Default: "homogeneous", Always: ["orificeDiameter"],
        Consumes: new() { ["homogeneous"] = [], ["slip"] = [] },
        // No hatch ever existed for this one, which is exactly why nothing noticed it had a mode:
        // R3's inventory finds mode-bearing types by looking for hatches.
        Infer: NoInference);

    // Eleven modes — more than any other unit op, and never counted by coverage-gap.md's table.
    // The pre-199 hatch picked between the two outlet-temperature modes and refused both; the other
    // nine were unreachable.
    private static readonly CalcModeDef HeatExchangerModes = new(
        "CalculationMode", typeof(DWSIM.UnitOperations.UnitOperations.HeatExchangerCalcMode),
        Default: "calcBothTempUA", Always: [],
        Consumes: new() {
            ["calcTempHotOut"] = ["coldSideOutletTemperature", "overallHeatTransferCoefficient", "area"],
            ["calcTempColdOut"] = ["hotSideOutletTemperature", "overallHeatTransferCoefficient", "area"],
            ["calcBothTemp"] = ["overallHeatTransferCoefficient", "area"],
            ["calcBothTempUA"] = ["overallHeatTransferCoefficient", "area"],
            ["calcArea"] = ["coldSideOutletTemperature", "hotSideOutletTemperature"],
            ["shellandTubeRating"] = ["overallHeatTransferCoefficient", "area"],
            ["shellandTubeCalcFoulingFactor"] = ["overallHeatTransferCoefficient", "area"],
            ["pinchPoint"] = [], ["thermalEfficiency"] = [],
            ["outletVaporFraction1"] = [], ["outletVaporFraction2"] = [],
        },
        Infer: [("coldSideOutletTemperature", "calcTempHotOut"), ("hotSideOutletTemperature", "calcTempColdOut")]);

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
            [P("calcMode", "string", false, "__calcMode"),
             P("splitRatio1", "dimensionless", false, "Ratios"),
             P("outletMassFlow", "massFlow", false, "StreamFlowSpec")], false, CalcMode: SplitterModes),

        new UnitOpDef("separator", "Gas-Liquid Separator", ObjectType.Vessel,
            [In("Inlet", 0), Out("Vapor Outlet", 0), Out("Liquid Outlet", 1), EnergyIn("Energy Inlet", 6)],
            [P("calcMode", "string", false, "__calcMode")], false, CalcMode: SeparatorModes),

        new UnitOpDef("tank", "Storage Tank", ObjectType.Tank,
            [In("Inlet", 0), Out("Outlet", 0)],
            [P("pressureDrop", "pressure", false, "DeltaP")], false),

        new UnitOpDef("heater", "Heater", ObjectType.Heater,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("heatDuty", "power", false, "DeltaQ"),
             P("pressureDrop", "pressure", false, "DeltaP"),
             P("efficiency", "dimensionless", false, "Eficiencia", "Efficiency")], false, CalcMode: HeaterModes),

        new UnitOpDef("cooler", "Cooler", ObjectType.Cooler,
            [In("Inlet", 0), Out("Outlet", 0), EnergyOut("Energy Outlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("heatDuty", "power", false, "DeltaQ"),
             P("pressureDrop", "pressure", false, "DeltaP"),
             P("efficiency", "dimensionless", false, "Eficiencia", "Efficiency"),
             // 099 US5 — COOLER ONLY. `OutletVaporFraction` is declared on `Cooler` and NOT on
             // `Heater`, measured: setting it on a heater moved nothing, because the escape hatch
             // selected the mode and the reflection then had no property to write. A parameter on
             // the wrong type is the silent-setpoint bug wearing the right name.
             P("outletVaporFraction", "dimensionless", false, "OutletVaporFraction")], false, CalcMode: CoolerModes),

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
            [P("calcMode", "string", false, "__calcMode"),
             P("coldSideOutletTemperature", "temperature", false, "ColdSideOutletTemperature"),
             P("hotSideOutletTemperature", "temperature", false, "HotSideOutletTemperature"),
             P("overallHeatTransferCoefficient", "heatTransferCoefficient", false, "OverallCoefficient"),
             P("area", "area", false, "Area")], false, CalcMode: HeatExchangerModes),

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
            [P("calcMode", "string", false, "__calcMode"),
             P("outletPressure", "pressure", false, "Pout", "POut"),
             P("pressureIncrease", "pressure", false, "DeltaP"),
             P("efficiency", "dimensionless", false, "Eficiencia", "Efficiency")], false, CalcMode: PumpModes),

        new UnitOpDef("compressor", "Compressor", ObjectType.Compressor,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("outletPressure", "pressure", false, "POut", "Pout"),
             P("pressureIncrease", "pressure", false, "DeltaP"),
             P("adiabaticEfficiency", "dimensionless", false, "AdiabaticEfficiency", "EficienciaAdiabatica")], false, CalcMode: CompressorModes),

        new UnitOpDef("expander", "Expander (Turbine)", ObjectType.Expander,
            [In("Inlet", 0), Out("Outlet", 0), EnergyOut("Energy Outlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("outletPressure", "pressure", false, "POut", "Pout"),
             P("pressureDecrease", "pressure", false, "DeltaP"),
             P("adiabaticEfficiency", "dimensionless", false, "AdiabaticEfficiency", "EficienciaAdiabatica")], false, CalcMode: ExpanderModes),

        new UnitOpDef("valve", "Valve", ObjectType.Valve,
            [In("Inlet", 0), Out("Outlet", 0)],
            [P("calcMode", "string", false, "__calcMode"),
             P("outletPressure", "pressure", false, "OutletPressure", "Pout", "POut"),
             P("pressureDrop", "pressure", false, "DeltaP"),
             // 099 US5. ONE `Kv`, not the `Kv_Liquid`/`Kv_Gas` pair the plan named — the engine has
             // a single flow coefficient and reports an `ActualKv` back. Dimensionless: a Kv is a
             // number read off a valve datasheet.
             P("kv", "dimensionless", false, "Kv"),
             P("openingPct", "dimensionless", false, "OpeningPct")], false, CalcMode: ValveModes),

        new UnitOpDef("pipe", "Pipe Segment", ObjectType.Pipe,
            [In("Inlet", 0), Out("Outlet", 0)],
            [P("length", "length", false, "Length"),
             P("diameter", "length", false, "Diameter")], false),

        new UnitOpDef("orificePlate", "Orifice Plate", ObjectType.OrificePlate,
            [In("Inlet", 0), Out("Outlet", 0)],
            [P("calcMode", "string", false, "__calcMode"),
             P("orificeDiameter", "length", false, "OrificeDiameter")], false, CalcMode: OrificePlateModes),

        new UnitOpDef("reactorConversion", "Conversion Reactor", ObjectType.RCT_Conversion,
            [In("Inlet", 0), Out("Vapor Outlet", 0), Out("Liquid Outlet", 1), EnergyIn("Energy Inlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("pressureDrop", "pressure", false, "DeltaP")], true, CalcMode: ReactorModes(["pressureDrop"], hasOutletTemperature: true)),

        new UnitOpDef("reactorEquilibrium", "Equilibrium Reactor", ObjectType.RCT_Equilibrium,
            [In("Inlet", 0), Out("Vapor Outlet", 0), Out("Liquid Outlet", 1), EnergyIn("Energy Inlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("pressureDrop", "pressure", false, "DeltaP")], true, CalcMode: ReactorModes(["pressureDrop"], hasOutletTemperature: true)),

        new UnitOpDef("reactorGibbs", "Gibbs Reactor", ObjectType.RCT_Gibbs,
            [In("Inlet", 0), Out("Vapor Outlet", 0), Out("Liquid Outlet", 1), EnergyIn("Energy Inlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("outletTemperature", "temperature", false, "OutletTemperature"),
             P("pressureDrop", "pressure", false, "DeltaP")], false, CalcMode: ReactorModes(["pressureDrop"], hasOutletTemperature: true)),

        new UnitOpDef("reactorCSTR", "CSTR", ObjectType.RCT_CSTR,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("volume", "volume", false, "Volume"),
             P("headspace", "volume", false, "Headspace"),   // vapor volume; required by the engine for gas-phase feeds
             P("outletTemperature", "temperature", false, "OutletTemperature")], true, CalcMode: ReactorModes(["volume", "headspace"], hasOutletTemperature: true)),

        new UnitOpDef("reactorPFR", "PFR", ObjectType.RCT_PFR,
            [In("Inlet", 0), Out("Outlet", 0), EnergyIn("Energy Inlet", 1)],
            [P("calcMode", "string", false, "__calcMode"),
             P("volume", "volume", false, "Volume"),
             P("length", "length", false, "Length")], true, CalcMode: ReactorModes(["volume", "length"], hasOutletTemperature: false)),

        // 141 US5 (FR-010): both columns' energy ports are `required: true` because that is what
        // the ENGINE enforces — without them BaseClass.Validate refuses with the opaque "Check
        // the connections of the object" (045 row E, re-confirmed 2026-08-06). The catalog said
        // `required: false` while every converging probe piped both; document validation now
        // refuses with the port NAMED, before the engine is reached.
        new UnitOpDef("shortcutColumn", "Shortcut Column (FUG)", ObjectType.ShortcutColumn,
            [In("Feed", 0), Out("Distillate", 0), Out("Bottoms", 1),
             EnergyOut("Condenser Duty", 2, required: true), EnergyIn("Reboiler Duty", 1, required: true)],
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
             EnergyOut("Condenser Duty", 0, required: true), EnergyIn("Reboiler Duty", 10, required: true)],
            [P("numberOfStages", "integer", true),
             P("feedStage", "integer", true),
             P("refluxRatio", "dimensionless", true),                  // condenser spec ("Reflux Ratio")
             P("condenserPressure", "pressure", true),
             P("reboilerPressure", "pressure", true),
             P("distillateMolarFlow", "molarFlow", false),             // condenser spec alternative
             P("bottomsMolarFlow", "molarFlow", false),                // reboiler spec ("Product Molar Flow Rate")
             // 143 — engine configuration, not engineering data. Both are deliberately
             // unbound by any app-side datasheet attribute (143 FR-008): a solving method is a
             // property of the simulator, not a fact about a column. See ColumnConfigurator.
             P("solvingMethod", "string", false),
             P("maxIterations", "integer", false)], false),

        new UnitOpDef("recycle", "Recycle", ObjectType.OT_Recycle,
            [In("Inlet", 0), Out("Outlet", 0)],
            [], false),
    }.ToDictionary(d => d.Type, d => d, StringComparer.Ordinal);

    /// <summary>Serializable catalog view (worker `catalog` mode payload).</summary>
    /// <summary>
    /// Engine enum member → wire name. `Delta_P` and `DeltaP` both become `deltaP`; `Kv_Liquid`
    /// becomes `kvLiquid`.
    ///
    /// ONE function, and it is the only place a mode name is transformed. Normalized because the
    /// engine's spelling divergence is DWSIM's inconsistency and not the engineer's — `Valve` spells
    /// it `DeltaP` while `Pump`/`Compressor`/`Expander` spell it `Delta_P`, for one concept.
    ///
    /// Uniqueness is claimed WITHIN a unit op and nowhere else. `outletPressure` is ordinal 1 on a
    /// Pump and 0 on a Compressor (research R1 hazard 1), so this is only ever applied through a
    /// `CalcModeDef`, which is per unit op by construction.
    ///
    /// Kept byte-identical with `dwsim-runner/scripts/modes.py:normalize`, which generates
    /// `modes.json` — the denominator FR-006 is measured against. Two implementations of one rule is
    /// a second home for a fact; they are together here because one side must run without .NET and
    /// the other without Python, and `CalcModeTests` checks this side against the engine directly.
    /// </summary>
    public static string NormalizeMode(string engineMember)
    {
        var parts = engineMember.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return engineMember;
        var head = char.ToLowerInvariant(parts[0][0]) + parts[0][1..];
        return head + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    public static object ToPayload() => Types.Values
        .OrderBy(d => d.Type, StringComparer.Ordinal)
        .Select(d =>
        {
            // A Dictionary rather than an anonymous type, for ONE reason: a unit op with no
            // calculation mode must OMIT the key, not carry `"calcMode": null`. Absent means "this
            // type has no mode" — the fail-closed reading spec 055 gives an unset gate — and eight
            // null entries in a committed fixture are noise that a reader has to learn to ignore.
            var row = new Dictionary<string, object?>
            {
                ["type"] = d.Type,
                ["displayName"] = d.DisplayName,
                ["ports"] = d.Ports.Select(p => new { name = p.Name, direction = p.Direction, accepts = p.Accepts, required = p.Required }),
                ["parameters"] = d.Parameters.Select(p => new { name = p.Name, unitType = p.UnitType, required = p.Required }),
                ["requiresReactionSet"] = d.RequiresReactionSet,
            };
            if (d.CalcMode is { } cm)
                row["calcMode"] = new
                {
                    @default = cm.Default,
                    modes = cm.Modes().Select(m => new
                    {
                        name = m.Name,
                        engineMember = m.EngineMember,
                        ordinal = m.Ordinal,
                        // Everything the mode reads: its own setpoints PLUS the ratings every mode
                        // reads. The app greys what is not in here, so omitting `Always` would grey
                        // a pump's efficiency in every mode.
                        consumes = cm.Always
                            .Concat(cm.Consumes.TryGetValue(m.Name, out var only) ? only : [])
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    }),
                };
            return row;
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
