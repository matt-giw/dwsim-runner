// dwsim-runner Worker — GPL-3.0
// Declarative flowsheet document → live DWSIM flowsheet (FR-BUILD-001..003,
// FR-VAL-002). Collects ALL issues it can before aborting; a build with any
// error-severity issue throws BuildAbortException carrying them. The same
// path serves `validate` (build, no solve), `build-solve`, and `pfd`.

using System.Text.Json;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;

namespace DwsimRunner.Worker;

public sealed record BuildIssue(string Severity, string Code, string? Tag, string? Path, string Message);

public sealed class BuildAbortException(string code, string message, List<BuildIssue> issues) : Exception(message)
{
    public string Code { get; } = code;
    public List<BuildIssue> Issues { get; } = issues;
}

// ── document DTOs (schemaVersion 1) ─────────────────────────────────────────

public sealed record FlowDoc(int SchemaVersion, string? Name, List<string> Compounds, string PropertyPackage,
    List<FlowObject> Objects, List<FlowConnection>? Connections,
    List<FlowReaction>? Reactions, List<FlowReactionSet>? ReactionSets);
public sealed record FlowObject(string Tag, string Kind, string? Type,
    FlowStreamSpec? Spec, Dictionary<string, JsonElement>? Parameters, FlowPosition? Position);
public sealed record FlowPosition(int X, int Y);
public sealed record FlowQuantity(double Value, string? Unit);
public sealed record FlowComposition(string? Basis, Dictionary<string, double> Fractions);
public sealed record FlowStreamSpec(FlowQuantity? Temperature, FlowQuantity? Pressure, FlowQuantity? Enthalpy,
    FlowQuantity? MassFlow, FlowQuantity? MolarFlow, FlowQuantity? VolumetricFlow, FlowComposition? Composition);
public sealed record FlowConnection(string From, string To, string Port);
public sealed record FlowReaction(string Tag, string Type, string? Basis, Dictionary<string, double> Stoichiometry,
    string BaseCompound, string? Phase, string? ConversionExpression,
    double? A, double? E, Dictionary<string, double>? DirectOrders, Dictionary<string, double>? ReverseOrders,
    string? EquilibriumConstantSource, double? Temperature);
public sealed record FlowReactionSet(string Tag, List<string> Reactions, List<string> AttachTo);

public sealed record BuildInfo(int ObjectsCreated, int ConnectionsMade, long ElapsedMs);

public static class FlowsheetBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static FlowDoc ParseDocument(JsonElement el)
    {
        try
        {
            var doc = el.Deserialize<FlowDoc>(JsonOpts);
            if (doc is null || doc.Objects is null)
                throw new WorkerInputException("INVALID_REQUEST", "document is empty or has no objects");
            return doc;
        }
        catch (JsonException ex)
        {
            throw new WorkerInputException("INVALID_REQUEST", $"document does not match schema: {ex.Message}");
        }
    }

    /// <summary>Build the flowsheet. Throws BuildAbortException on any
    /// error-severity issue (all issues attached).</summary>
    public static (IFlowsheet Fs, BuildInfo Info, List<BuildIssue> Warnings) Build(
        DWSIM.Automation.Automation3 auto, FlowDoc doc)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var issues = new List<BuildIssue>();
        void Error(string code, string? tag, string message, string? path = null) =>
            issues.Add(new BuildIssue("error", code, tag, path, message));
        void Warn(string code, string? tag, string message) =>
            issues.Add(new BuildIssue("warning", code, tag, null, message));

        var fs = auto.CreateFlowsheet()
                 ?? throw new WorkerInputException("WORKER_CRASH", "engine failed to create a flowsheet");

        // ── compounds ──────────────────────────────────────────────────────
        var available = fs.AvailableCompounds;   // name → constant properties
        var resolvedCompounds = new List<string>();
        foreach (var requested in doc.Compounds ?? [])
        {
            var match = available.Keys.FirstOrDefault(k => string.Equals(k, requested, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var suggestions = available.Keys
                    .Where(k => k.Contains(requested, StringComparison.OrdinalIgnoreCase)
                             || requested.Length >= 4 && k.StartsWith(requested[..4], StringComparison.OrdinalIgnoreCase))
                    .Take(5).ToList();
                Error("UNKNOWN_COMPOUND", null,
                    $"compound '{requested}' not found" +
                    (suggestions.Count > 0 ? $"; did you mean: {string.Join(", ", suggestions)}?" : ""),
                    "compounds");
                continue;
            }
            fs.AddCompound(match);
            resolvedCompounds.Add(match);
        }

        // ── property package ───────────────────────────────────────────────
        var engineNames = fs.GetAvailablePropertyPackages().Cast<string>().ToList();
        var packageName = PackageCatalog.Resolve(doc.PropertyPackage ?? "", engineNames);
        if (packageName is null)
            Error("UNKNOWN_PROPERTY_PACKAGE", null,
                $"property package '{doc.PropertyPackage}' not found; available ids: " +
                string.Join(", ", engineNames.Select(n => PackageCatalog.Classify(n).Id).Distinct().Order()),
                "propertyPackage");

        if (issues.Any(i => i.Severity == "error"))
            throw new BuildAbortException(
                issues.Any(i => i.Code == "UNKNOWN_COMPOUND") ? "UNKNOWN_COMPOUND" : "BUILD_FAILED",
                "document references unknown engine entities", issues);

        fs.CreateAndAddPropertyPackage(packageName!);

        // ── objects ────────────────────────────────────────────────────────
        var byTag = new Dictionary<string, ISimulationObject>(StringComparer.Ordinal);
        var anyPositionMissing = false;
        var i = 0;
        foreach (var o in doc.Objects)
        {
            var x = o.Position?.X ?? 120 + 180 * (i % 6);
            var y = o.Position?.Y ?? 120 + 160 * (i / 6);
            if (o.Position is null) anyPositionMissing = true;
            i++;

            try
            {
                ISimulationObject? created = o.Kind switch
                {
                    "materialStream" => fs.AddObject(ObjectType.MaterialStream, x, y, o.Tag),
                    "energyStream" => fs.AddObject(ObjectType.EnergyStream, x, y, o.Tag),
                    "unitOp" when o.Type is not null && UnitOpCatalog.Types.TryGetValue(o.Type, out var def)
                        => fs.AddObject(def.ObjectType, x, y, o.Tag),
                    _ => null,
                };
                if (created is null)
                {
                    Error("BUILD_FAILED", o.Tag, $"unknown object kind/type '{o.Kind}/{o.Type}'");
                    continue;
                }
                // AN EXTERNAL UNIT OPERATION BUILDS ITS OWN PORTS, AND NOTHING CALLS IT.
                //
                // `IExternalUnitOperation.CreateConnectors()` is the engine's own hook, and for a
                // plugin-supplied op the flowsheet does not invoke it on this headless path. So
                // `AddObject` returns a perfectly good object whose graphic has ZERO connectors, and
                // the failure surfaces one step later as "Index was out of range" on the first
                // connect — which reads as a wrong port index in OUR catalog rather than as a
                // missing initialisation in the engine's.
                //
                // Constructed-but-unconnectable is the shape to remember: the engine inventory
                // reports this type `instantiable` because construction genuinely succeeds.
                if (created is DWSIM.Interfaces.IExternalUnitOperation ext)
                    try { ext.CreateConnectors(); }
                    catch (Exception ex) { Error("BUILD_FAILED", o.Tag, $"'{o.Tag}' could not create its ports: {ex.Message}"); }
                byTag[o.Tag] = created;
            }
            catch (Exception ex)
            {
                Error("BUILD_FAILED", o.Tag, $"engine failed to add '{o.Tag}': {ex.Message}");
            }
        }

        // ── connections ────────────────────────────────────────────────────
        var connectionsMade = 0;
        foreach (var c in doc.Connections ?? [])
        {
            if (!byTag.TryGetValue(c.From, out var fromObj)) { Error("BUILD_FAILED", c.From, $"no object tagged '{c.From}'"); continue; }
            if (!byTag.TryGetValue(c.To, out var toObj)) { Error("BUILD_FAILED", c.To, $"no object tagged '{c.To}'"); continue; }

            var fromDoc = doc.Objects.First(o => o.Tag == c.From);
            var toDoc = doc.Objects.First(o => o.Tag == c.To);
            var unitDoc = fromDoc.Kind == "unitOp" ? fromDoc : toDoc;
            var unitObj = fromDoc.Kind == "unitOp" ? fromObj : toObj;
            var streamObj = fromDoc.Kind == "unitOp" ? toObj : fromObj;
            var streamDoc = fromDoc.Kind == "unitOp" ? toDoc : fromDoc;
            var isFeed = toDoc.Kind == "unitOp";   // stream → unit

            if (unitDoc.Kind != "unitOp" || streamDoc.Kind == "unitOp")
            {
                Error("BUILD_FAILED", c.From, $"connection {c.From}→{c.To} must join a stream and a unit operation");
                continue;
            }
            var def = UnitOpCatalog.Types[unitDoc.Type!];
            var port = def.Ports.FirstOrDefault(p => string.Equals(p.Name, c.Port, StringComparison.OrdinalIgnoreCase));
            if (port is null)
            {
                Error("BUILD_FAILED", unitDoc.Tag,
                    $"'{unitDoc.Type}' has no port '{c.Port}'; valid: {string.Join(", ", def.Ports.Select(p => p.Name))}");
                continue;
            }

            try
            {
                if (ColumnConfigurator.TryConnect(unitObj, port.Name, streamObj, unitDoc))
                {
                    connectionsMade++;
                    continue;
                }
                var isEnergy = streamDoc.Kind == "energyStream";
                if (isFeed && !isEnergy) unitObj.ConnectFeedMaterialStream(streamObj, port.Index);
                else if (isFeed) unitObj.ConnectFeedEnergyStream(streamObj, port.Index);
                else if (!isEnergy) unitObj.ConnectProductMaterialStream(streamObj, port.Index);
                else unitObj.ConnectProductEnergyStream(streamObj, port.Index);
                connectionsMade++;
            }
            catch (Exception ex)
            {
                Error("BUILD_FAILED", unitDoc.Tag,
                    $"cannot connect '{streamDoc.Tag}' to '{unitDoc.Tag}' port '{port.Name}': {ex.Message}");
            }
        }

        // ── stream specifications ──────────────────────────────────────────
        foreach (var o in doc.Objects.Where(o => o.Kind == "materialStream" && o.Spec is not null))
        {
            if (!byTag.TryGetValue(o.Tag, out var so) || so is not IMaterialStream ms) continue;
            var spec = o.Spec!;
            try
            {
                if (spec.Temperature is { } tq) ms.SetTemperature(ToSi(tq, "K"));
                if (spec.Pressure is { } pq) ms.SetPressure(ToSi(pq, "Pa"));
                if (spec.MassFlow is { } mf) ms.SetMassFlow(ToSi(mf, "kg/s"));
                if (spec.MolarFlow is { } nf) ms.SetMolarFlow(ToSi(nf, "mol/s"));
                if (spec.Composition is { } comp)
                {
                    var order = ((dynamic)ms).Phases[0].Compounds.Values;
                    var vector = new List<double>();
                    foreach (var compound in order)
                    {
                        string name = compound.Name;
                        var frac = comp.Fractions.FirstOrDefault(f =>
                            string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
                        vector.Add(frac);
                    }
                    if (string.Equals(comp.Basis, "mass", StringComparison.OrdinalIgnoreCase))
                        ms.SetOverallMassComposition([.. vector]);
                    else
                        ms.SetOverallMolarComposition([.. vector]);
                }
            }
            catch (Exception ex)
            {
                Error("INVALID_PARAMETER_VALUE", o.Tag, $"cannot apply stream spec on '{o.Tag}': {ex.Message}");
            }
        }

        // ── synthesized power streams (099 US1) ────────────────────────────
        // AFTER connections, BEFORE parameters: the electrolyzer must already be connected to its
        // water feed, and its `voltage`/`cellCount` must not be applied to an object that is about
        // to be refused for having no power. See ElectrolyzerConfigurator for why the document
        // carries power as a parameter and the engine gets a stream.
        ElectrolyzerConfigurator.Apply(fs, doc, byTag, Error);

        // ── unit-op parameters ─────────────────────────────────────────────
        foreach (var o in doc.Objects.Where(o => o.Kind == "unitOp" && o.Parameters is { Count: > 0 }))
        {
            if (!byTag.TryGetValue(o.Tag, out var so)) continue;
            var def = UnitOpCatalog.Types[o.Type!];
            foreach (var (name, raw) in o.Parameters!)
            {
                var paramDef = def.Parameters.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (paramDef is null)
                {
                    Warn("INVALID_PARAMETER_VALUE", o.Tag,
                        $"'{o.Type}' has no parameter '{name}' — ignored (known: {string.Join(", ", def.Parameters.Select(p => p.Name))})");
                    continue;
                }
                try
                {
                    ApplyParameter(so, o, def, paramDef, raw, resolvedCompounds, Error);
                }
                catch (Exception ex)
                {
                    Error("INVALID_PARAMETER_VALUE", o.Tag, $"cannot set '{name}' on '{o.Tag}': {ex.Message}");
                }
            }
            if (o.Type == "distillationColumn")
                try { ColumnConfigurator.Finish(so, o); }
                catch (Exception ex) { Error("INVALID_PARAMETER_VALUE", o.Tag, $"column pressure profile on '{o.Tag}': {ex.Message}"); }
        }

        // ── reactions ──────────────────────────────────────────────────────
        BuildReactions(fs, doc, byTag, Error);

        if (anyPositionMissing)
            try { fs.AutoLayout(); } catch { /* cosmetic only */ }

        if (issues.Any(x => x.Severity == "error"))
            throw new BuildAbortException(
                issues.Any(x => x.Code == "UNKNOWN_COMPOUND") ? "UNKNOWN_COMPOUND" : "BUILD_FAILED",
                "engine rejected the document", issues);

        return (fs, new BuildInfo(byTag.Count, connectionsMade, sw.ElapsedMilliseconds),
                issues.Where(x => x.Severity == "warning").ToList());
    }

    // Parameter application: type-specific handlers first, then reflection over
    // the candidate .NET property names, then the DWSIM generic property bag.
    /// <param name="compounds">The flowsheet's RESOLVED compound names — what the engine keys a
    /// per-compound specification by. Threaded rather than re-derived so a separator's spec cannot
    /// be validated against a different list than the one the engine holds.</param>
    internal static void ApplyParameter(ISimulationObject so, FlowObject o, UnitOpDef def, ParamDef p,
        JsonElement raw, IReadOnlyCollection<string> compounds,
        Action<string, string?, string, string?> error)   // (code, tag, message, path)
    {
        if (def.Type is "distillationColumn" && ColumnConfigurator.Handles(p.Name))
        {
            ColumnConfigurator.Apply(so, p.Name, raw);
            return;
        }
        // 099 US1 — `powerInput` was CONSUMED before this loop: ElectrolyzerConfigurator.Apply
        // synthesizes the energy stream from it (the engine takes power as a stream, not a
        // parameter). Already applied out of band, so the honest binder must not refuse it —
        // caught by 141's T017 sweep when the refusal flipped the type's capability verdict.
        if (def.Type is "waterElectrolyzer" && p.Name.Equals("powerInput", StringComparison.OrdinalIgnoreCase))
            return;
        // 099 US2 — a per-compound dictionary, which the generic name→property setter cannot express.
        if (def.Type is "componentSeparator" && ComponentSeparatorConfigurator.Handles(p.Name))
        {
            ComponentSeparatorConfigurator.Apply(so, raw, o, compounds, error);
            return;
        }
        // Reactors: an explicit outletTemperature implies OutletTemperature
        // operating mode — otherwise the engine ignores the setpoint and runs
        // adiabatic (its default).
        if (def.Type.StartsWith("reactor", StringComparison.Ordinal) && p.Name == "outletTemperature")
        {
            var modeProp = so.GetType().GetProperty("ReactorOperationMode");
            if (modeProp is not null)
                modeProp.SetValue(so, Enum.Parse(modeProp.PropertyType, "OutletTemperature"));
        }
        // Heaters/coolers: same class of bug as the reactor block above — an
        // explicit outletTemperature implies OutletTemperature calc mode;
        // otherwise the engine stays in heat-duty mode (its default,
        // HeatAdded/HeatRemoved) and silently ignores the setpoint.
        // Spec: 005-unitop-parameter-application.
        if (def.Type is "heater" or "cooler")
        {
            if (p.Name == "outletTemperature")
            {
                var modeProp = so.GetType().GetProperty("CalcMode");
                if (modeProp is not null)
                    modeProp.SetValue(so, Enum.Parse(modeProp.PropertyType, "OutletTemperature"));
            }
            // 099 US5 — same shape, same reason: a setpoint the engine will ignore unless its mode
            // says to read it. Only the COOLER declares `OutletVaporFraction`; the catalog reflects
            // that, so this branch is unreachable on a heater.
            if (p.Name == "outletVaporFraction")
            {
                var modeProp = so.GetType().GetProperty("CalcMode");
                if (modeProp is not null)
                    modeProp.SetValue(so, Enum.Parse(modeProp.PropertyType, "OutletVaporFraction"));
            }
            // Engine efficiency (m_eta) is a percent (constructor default 100);
            // the document convention is a 0–1 fraction. ≤ 1 → fraction, ×100.
            if (p.Name == "efficiency")
            {
                var eta = raw.ValueKind == JsonValueKind.Object
                    ? raw.GetProperty("value").GetDouble() : raw.GetDouble();
                if (!SetEngineProperty(so, p.EngineProperties, eta <= 1.0 ? eta * 100 : eta))
                    RefuseUnbindable(o, def, p, error);   // 141 FR-001 — same rule on the early-return path
                return;
            }
        }
        // Heat exchanger: the SAME bug as the two blocks above, and it had been measured and
        // written down as unfixable. It is not — it just needs the same three lines.
        //
        // The engine constructs in `CalcBothTemp_UA`, where BOTH outlet temperatures are OUTPUTS.
        // So `hotSideOutletTemperature` and `coldSideOutletTemperature` were accepted, converged,
        // and had NO EFFECT: a document asking for a 60 degC outlet came back with whatever
        // U = 1000 W/[m2.K] and A = 1 m2 happen to give, reported as converged, with no warning.
        // Measured: the setpoint, the other setpoint, and both together all produce byte-identical
        // results to sending no parameters at all.
        //
        // The mode name says which temperature the engine SOLVES FOR, so specifying the hot outlet
        // means the COLD one is calculated — `CalcTempColdOut`. That reading is asserted by
        // `HeatExchangerTests` against the live engine rather than trusted, because getting it
        // backwards silently swaps which of two plausible temperatures is honoured.
        // The mode is decided from the WHOLE parameter set, not from the parameter in hand: with
        // both setpoints given, a per-parameter decision would leave whichever one the dictionary
        // happened to enumerate last in charge, so the same document could mean two things.
        //
        // BOTH setpoints together is REFUSED, and that is an engineering answer rather than a
        // limitation. With both inlet states and both flows fixed, the hot outlet temperature
        // already determines the duty; the cold outlet temperature determines it a second time,
        // and the two agree only by coincidence. DWSIM's `CalcBothTemp` looks like the mode for
        // this and is not: measured, it returns duty 0 with both outlets equal to their inlets —
        // converged, and the exchanger does nothing. Refusing beats picking one silently, and
        // beats a mode that quietly disables the unit.
        if (def.Type is "heatExchanger" && p.Name is "hotSideOutletTemperature" or "coldSideOutletTemperature")
        {
            bool Given(string n) => o.Parameters!.Keys.Any(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase));
            var hot = Given("hotSideOutletTemperature");
            if (hot && Given("coldSideOutletTemperature"))
                throw new InvalidOperationException(
                    "hotSideOutletTemperature and coldSideOutletTemperature are both set, which " +
                    "specifies the duty twice — keep one and let the exchanger compute the other, " +
                    "or drop both and size it with overallHeatTransferCoefficient + area");
            var modeProp = so.GetType().GetProperty("CalculationMode");
            if (modeProp is not null)
                modeProp.SetValue(so, Enum.Parse(modeProp.PropertyType,
                    hot ? "CalcTempColdOut" : "CalcTempHotOut"));
        }
        // 099 US5 — a stated Kv implies a Kv calculation mode. The valve's default is `DeltaP` or
        // `OutletPressure`, under which `Kv` is read by nothing: accepted, converged, ignored.
        //
        // `Kv_General` of the four Kv modes (Liquid/Gas/Steam/General), because it is the one that
        // does not require the caller to have already decided the phase — and the phase is the
        // engine's answer, not the engineer's input. 099's tasks named `kvLiquid`/`kvGas` as
        // PARAMETERS; they are MODES, the same confusion as the splitter's `StreamMassFlowSpec`.
        if (def.Type is "valve" && p.Name == "kv")
        {
            var modeProp = so.GetType().GetProperty("CalcMode") ?? so.GetType().GetProperty("CalculationMode");
            if (modeProp is not null && modeProp.PropertyType.IsEnum)
                modeProp.SetValue(so, Enum.Parse(modeProp.PropertyType, "Kv_General"));
        }
        // 099 US5 — a stated outlet flow implies the splitter's flow-spec mode. Its default is
        // `SplitRatios`, under which `StreamFlowSpec` is read by nothing: the setpoint is accepted,
        // the flowsheet converges, and the split is whatever the ratios say. The same silent-ignore
        // this escape-hatch region exists for.
        if (def.Type is "splitter" && p.Name == "outletMassFlow")
        {
            var modeProp = so.GetType().GetProperty("OperationMode") ?? so.GetType().GetProperty("OpMode");
            if (modeProp is not null && modeProp.PropertyType.IsEnum)
                modeProp.SetValue(so, Enum.Parse(modeProp.PropertyType, "StreamMassFlowSpec"));
        }
        if (def.Type is "splitter" && p.Name == "splitRatio1")
        {
            var r1 = raw.ValueKind == JsonValueKind.Object ? raw.GetProperty("value").GetDouble() : raw.GetDouble();
            SetEngineProperty(so, ["Ratios"], null);   // probe: Ratios is a list on DWSIM splitters
            var ratios = so.GetType().GetProperty("Ratios")?.GetValue(so);
            if (ratios is System.Collections.IList list && list.Count >= 2)
            {
                list[0] = r1;
                list[1] = 1 - r1;
                return;
            }
            throw new InvalidOperationException("splitter ratio list not accessible");
        }

        var (value, unit) = raw.ValueKind switch
        {
            JsonValueKind.Object => ((object)raw.GetProperty("value").Deserialize<JsonElement>(),
                                     raw.TryGetProperty("unit", out var u) ? u.GetString() : null),
            _ => ((object)raw, null),
        };
        object engineValue = value is JsonElement je
            ? je.ValueKind switch
            {
                JsonValueKind.Number when p.UnitType == "integer" => je.GetInt32(),
                JsonValueKind.Number => unit is { Length: > 0 }
                    ? DWSIM.SharedClasses.SystemsOfUnits.Converter.ConvertToSI(unit, je.GetDouble())
                    : je.GetDouble(),
                JsonValueKind.String => je.GetString()!,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidOperationException("unsupported parameter value"),
            }
            : value;

        if (p.EngineProperties.Length > 0 && SetEngineProperty(so, p.EngineProperties, engineValue))
            return;
        // Fall back to DWSIM's generic property interface — but only when the object's own
        // property bag actually lists the name. Its return value cannot be trusted (141 T009a,
        // decompiled 9.0.5.0 + probed live): ShortcutColumn's SetPropertyValue override returns
        // TRUE unconditionally, writing nothing for a name it does not recognise — which is how
        // the shortcut column's whole parameter set was silently dropped for three weeks.
        if (so.GetProperties(DWSIM.Interfaces.Enums.PropertyType.ALL)
              .Contains(p.Name, StringComparer.OrdinalIgnoreCase))
        {
            so.SetPropertyValue(p.Name, engineValue);
            return;
        }
        RefuseUnbindable(o, def, p, error);
    }

    // 141 FR-001: a parameter the runner cannot bind is a typed, per-parameter build failure —
    // same envelope as MISSING_REQUIRED_PARAMETER (severity/code/tag/path/message), one step
    // later in the pipeline. Never a silent skip.
    private static void RefuseUnbindable(FlowObject o, UnitOpDef def, ParamDef p,
        Action<string, string?, string, string?> error) =>
        error("UNBINDABLE_PARAMETER", o.Tag,
            $"'{def.Type}' parameter '{p.Name}' on '{o.Tag}' could not be applied: no settable " +
            $"engine property or field matches [{string.Join(", ", p.EngineProperties)}], and the " +
            $"engine's generic property interface does not list '{p.Name}'",
            $"parameters.{p.Name}");

    internal static bool SetEngineProperty(ISimulationObject so, string[] candidates, object? value)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.IgnoreCase;
        foreach (var name in candidates)
        {
            var prop = so.GetType().GetProperty(name, Flags);
            if (prop is not null)
            {
                if (value is null) return true;   // probe-only call
                if (prop.CanWrite)
                {
                    prop.SetValue(so, Coerce(prop.PropertyType, value));
                    return true;
                }
            }
            // 141 FR-003: DWSIM exposes several settable members as public FIELDS, not
            // properties — ShortcutColumn.m_refluxratio, m_lightkey, m_condenserpressure, … —
            // which GetProperty can never see. Measured: the shortcut column's five required
            // parameters all live in fields, so every one of them silently missed.
            var field = so.GetType().GetField(name, Flags);
            if (field is not null && !field.IsInitOnly)
            {
                if (value is null) return true;   // probe-only call
                field.SetValue(so, Coerce(field.FieldType, value));
                return true;
            }
        }
        return false;
    }

    private static object Coerce(Type target, object value) =>
        target.IsEnum ? Enum.Parse(target, value.ToString()!, ignoreCase: true)
        : target == typeof(int) ? Convert.ToInt32(value)
        // 099 US2 — `ComponentSeparator.SpecifiedStreamIndex` is a BYTE. JSON gives Int32, and
        // without this the setter threw "Object of type 'System.Int32' cannot be converted to
        // type 'System.Byte'" — a reflection message about a document the caller wrote.
        : target == typeof(byte) ? Convert.ToByte(value)
        : target == typeof(short) ? Convert.ToInt16(value)
        : target == typeof(long) ? Convert.ToInt64(value)
        : target == typeof(float) ? Convert.ToSingle(value)
        : target == typeof(double) ? Convert.ToDouble(value)
        : target == typeof(double?) ? (double?)Convert.ToDouble(value)
        : value;

    private static void BuildReactions(IFlowsheet fs, FlowDoc doc,
        Dictionary<string, ISimulationObject> byTag, Action<string, string?, string, string?> error)
    {
        if (doc.Reactions is not { Count: > 0 }) return;
        var reactionIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rx in doc.Reactions)
        {
            try
            {
                var phase = rx.Phase ?? "Mixture";
                var basis = rx.Basis ?? "Molar Fractions";
                IReaction created = rx.Type.ToLowerInvariant() switch
                {
                    "conversion" => fs.CreateConversionReaction(rx.Tag, rx.Tag, rx.Stoichiometry, rx.BaseCompound,
                        phase, rx.ConversionExpression ?? "100"),
                    // Signature: (…, basis, units, Tapproach, lnKeq_fT). The K
                    // source (Gibbs / expression / constant) is NOT an argument —
                    // it is the KExprType property, configured below.
                    "equilibrium" => fs.CreateEquilibriumReaction(rx.Tag, rx.Tag, rx.Stoichiometry, rx.BaseCompound,
                        phase, basis, "Pa", 0.0, ""),
                    // The engine indexes both order dictionaries by every
                    // stoichiometry compound — products included (order 0).
                    "kinetic" => fs.CreateKineticReaction(rx.Tag, rx.Tag, rx.Stoichiometry,
                        FullOrders(rx.Stoichiometry, rx.DirectOrders, reactants: true),
                        FullOrders(rx.Stoichiometry, rx.ReverseOrders, reactants: false),
                        rx.BaseCompound, phase, basis, "mol/L", "mol/[L.s]",
                        rx.A ?? 1, rx.E ?? 0, 0, 0, "", ""),
                    "heterogeneouscatalytic" => fs.CreateHetCatReaction(rx.Tag, rx.Tag, rx.Stoichiometry, rx.BaseCompound,
                        phase, basis, "mol/L", "mol/[kg.s]", "", ""),
                    _ => throw new InvalidOperationException($"unknown reaction type '{rx.Type}'"),
                };
                // The create helpers take phase/basis as strings but do not
                // reliably parse our document vocabulary — set the enums
                // explicitly so "molar"/"Vapor" land where they should.
                if (created is DWSIM.Thermodynamics.BaseClasses.Reaction cr)
                {
                    cr.ReactionPhase = phase.ToLowerInvariant() switch
                    {
                        "vapor" => DWSIM.Interfaces.Enums.ReactionPhase.Vapor,
                        "liquid" => DWSIM.Interfaces.Enums.ReactionPhase.Liquid,
                        _ => DWSIM.Interfaces.Enums.ReactionPhase.Mixture,
                    };
                    cr.ReactionBasis = basis.ToLowerInvariant() switch
                    {
                        "mass" or "mass fractions" => DWSIM.Interfaces.Enums.ReactionBasis.MassFrac,
                        "partialpressure" or "partial pressure" => DWSIM.Interfaces.Enums.ReactionBasis.PartialPress,
                        "fugacity" => DWSIM.Interfaces.Enums.ReactionBasis.Fugacity,
                        "activity" => DWSIM.Interfaces.Enums.ReactionBasis.Activity,
                        "molarconcentration" or "molar concentration" => DWSIM.Interfaces.Enums.ReactionBasis.MolarConc,
                        _ => DWSIM.Interfaces.Enums.ReactionBasis.MolarFrac,
                    };

                    if (rx.Type.Equals("equilibrium", StringComparison.OrdinalIgnoreCase))
                    {
                        var source = (rx.EquilibriumConstantSource ?? "Gibbs Energy").Trim();
                        if (double.TryParse(source, System.Globalization.CultureInfo.InvariantCulture, out var keq))
                        {
                            cr.KExprType = DWSIM.Interfaces.Enums.KOpt.Constant;
                            cr.ConstantKeqValue = keq;
                        }
                        else if (source.Contains("gibbs", StringComparison.OrdinalIgnoreCase))
                        {
                            cr.KExprType = DWSIM.Interfaces.Enums.KOpt.Gibbs;
                        }
                        else
                        {
                            cr.KExprType = DWSIM.Interfaces.Enums.KOpt.Expression;
                            cr.Expression = source;   // lnKeq as f(T)
                        }
                    }
                }
                fs.AddReaction(created);
                reactionIds[rx.Tag] = created.ID;
            }
            catch (Exception ex)
            {
                error("BUILD_FAILED", rx.Tag, $"cannot create reaction '{rx.Tag}': {ex.Message}", "reactions");
            }
        }

        foreach (var set in doc.ReactionSets ?? [])
        {
            try
            {
                var created = fs.CreateReactionSet(set.Tag, set.Tag);
                fs.AddReactionSet(created);
                var order = 0;
                foreach (var rxTag in set.Reactions)
                    if (reactionIds.TryGetValue(rxTag, out var rxId))
                        fs.AddReactionToSet(rxId, created.ID, true, order++);

                foreach (var reactorTag in set.AttachTo)
                {
                    if (!byTag.TryGetValue(reactorTag, out var reactor))
                    {
                        error("BUILD_FAILED", reactorTag, $"reaction set '{set.Tag}' attaches to unknown object '{reactorTag}'", "reactionSets");
                        continue;
                    }
                    var prop = reactor.GetType().GetProperty("ReactionSetID");
                    if (prop is not null) prop.SetValue(reactor, created.ID);
                    else error("BUILD_FAILED", reactorTag, $"'{reactorTag}' does not accept a reaction set", "reactionSets");
                }
            }
            catch (Exception ex)
            {
                error("BUILD_FAILED", set.Tag, $"cannot create reaction set '{set.Tag}': {ex.Message}", "reactionSets");
            }
        }
    }

    // Kinetic-order dictionaries covering every stoichiometry compound: user
    // overrides win; reactants default to their stoichiometric order forward,
    // everything else to 0.
    private static Dictionary<string, double> FullOrders(
        Dictionary<string, double> stoichiometry, Dictionary<string, double>? given, bool reactants)
    {
        var orders = stoichiometry.ToDictionary(
            s => s.Key,
            s => reactants && s.Value < 0 ? -s.Value : 0.0);
        foreach (var (k, v) in given ?? [])
            if (orders.ContainsKey(k)) orders[k] = v;
        return orders;
    }

    // Unit → SI via the engine's own converter; bare numbers are taken as SI.
    private static double ToSi(FlowQuantity q, string siUnit) =>
        q.Unit is { Length: > 0 }
            ? DWSIM.SharedClasses.SystemsOfUnits.Converter.ConvertToSI(q.Unit, q.Value)
            : q.Value;
}
