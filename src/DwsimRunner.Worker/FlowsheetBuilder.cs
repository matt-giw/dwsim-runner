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
                    ApplyParameter(so, o, def, paramDef, raw);
                }
                catch (Exception ex)
                {
                    Error("INVALID_PARAMETER_VALUE", o.Tag, $"cannot set '{name}' on '{o.Tag}': {ex.Message}");
                }
            }
            if (o.Type == "distillationColumn")
                try { ColumnConfigurator.Finish(so); }
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
    private static void ApplyParameter(ISimulationObject so, FlowObject o, UnitOpDef def, ParamDef p, JsonElement raw)
    {
        if (def.Type is "distillationColumn" && ColumnConfigurator.Handles(p.Name))
        {
            ColumnConfigurator.Apply(so, p.Name, raw);
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
        // Fall back to DWSIM's generic property interface.
        so.SetPropertyValue(p.Name, engineValue);
    }

    private static bool SetEngineProperty(ISimulationObject so, string[] candidates, object? value)
    {
        foreach (var name in candidates)
        {
            var prop = so.GetType().GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.IgnoreCase);
            if (prop is null) continue;
            if (value is null) return true;   // probe-only call
            if (!prop.CanWrite) continue;
            var target = prop.PropertyType;
            var converted = target.IsEnum ? Enum.Parse(target, value.ToString()!, ignoreCase: true)
                : target == typeof(int) ? Convert.ToInt32(value)
                : target == typeof(double) ? Convert.ToDouble(value)
                : target == typeof(double?) ? (double?)Convert.ToDouble(value)
                : value;
            prop.SetValue(so, converted);
            return true;
        }
        return false;
    }

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
