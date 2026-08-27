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

                // 166: DWSIM's Vessel defaults to CalculationModes.Legacy, whose mixed-feed flash is
                // (T, P) — ill-posed for a pure compound on the saturation line, where (T, P) does not
                // determine the vapor fraction. Every two-phase pure feed collapsed to one outlet
                // (0 kg/h on the other) with a phantom duty equal to the destroyed phase's latent heat,
                // under converged:true on EOS packages. Adiabatic mode copies a single feed's state
                // verbatim (VF preserved by construction) and PH-flashes a multi-feed mix — correct in
                // both cases. Measured: specs/166-separator-degenerate-split/research.md.
                if (created is DWSIM.UnitOperations.UnitOperations.Vessel vessel)
                    vessel.CalculationMode = DWSIM.UnitOperations.UnitOperations.Vessel.CalculationModes.Adiabatic;

                // 170: the Recycle block ships MaximumIterations = 50 (m_MaxIterations,
                // vendored IL) — 143's iteration-budget shape on the tear stream: corpus
                // 179 (HDA gas recycle) died on "Recycle reached the maximum number of
                // iterations without converging" (168's diagnosis), and two more recycle
                // cases (130 at 1.94%, 167 at 0.17%) "converged" inside the block's own
                // loose defaults while leaving real mass unclosed (VazaoMassica 0.01,
                // Composicao 0.001, Temperatura 0.1 K). Same standard as 143: raise the
                // budget 10x — the solve timeout is the real backstop — and tighten the
                // tear tolerances one decade so "converged" means closed to ~0.1%.
                if (created is DWSIM.UnitOperations.SpecialOps.Recycle recycle)
                {
                    recycle.MaximumIterations = 500;
                    // One decade was not enough for 130 (1.94% -> 0.18% against a 0.1%
                    // conservation gate — the tear stream is a fraction of the total, so
                    // its tolerance must sit well under the gate). Two decades, measured.
                    recycle.ConvergenceParameters.VazaoMassica = 0.0001;  // mass flow
                    recycle.ConvergenceParameters.Composicao = 0.00001;   // composition
                    recycle.ConvergenceParameters.Temperatura = 0.01;     // K
                    recycle.ConvergenceParameters.Entalpia = 0.1;         // enthalpy
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

        // 169: a Gibbs reactor minimizes over an ELEMENT MATRIX the GUI's editor
        // populates and the automation path never does — `Calculate_GibbsMin` opens
        // with `Elements.Length - 1 < 0 -> throw "The Element Matrix is not defined."`,
        // which is exactly what both corpus Gibbs cases (159/217) died on once 168 made
        // non-convergence legible. The engine ships its own setup, `CreateElementMatrix()`
        // (public; reads the ATTACHED INLET, so this must run after connections) — but it
        // enumerates `ComponentIDs`, and the only engine routine touching that list
        // (`CheckCompoundIDs`) REMOVES stale entries and never adds: on a fresh automation
        // reactor the list is empty and CreateElementMatrix faithfully builds a 0x0 matrix
        // (measured: calling it alone left both cases dying on the same message). The GUI
        // seeds the list from the user's compound picks; here every flowsheet compound
        // participates. Fourth instance of the shape: a GUI-populated default the headless
        // path must set itself (162 Tref, 166 Vessel.CalculationMode, this twice over).
        foreach (var so in byTag.Values)
        {
            if (so is not DWSIM.UnitOperations.Reactors.Reactor_Gibbs gr) continue;
            try
            {
                gr.ComponentIDs.Clear();
                foreach (var compoundName in fs.SelectedCompounds.Keys)
                    gr.ComponentIDs.Add(compoundName);
                gr.CreateElementMatrix();
                // Second GUI-only default, found one layer under the first: with
                // InitializeFromPreviousSolution true (and no previous solution to read),
                // Calculate_GibbsMin throws "invalid initial estimates." — the false path
                // computes estimates from the feed, which is the only sane choice on a
                // freshly built flowsheet.
                gr.InitializeFromPreviousSolution = false;
            }
            catch (Exception ex)
            {
                // An unfed inlet dereferences in CreateElementMatrix before the solver
                // would refuse it anyway — report, don't die at build.
                Error("BUILD_FAILED", gr.GraphicObject?.Tag ?? "?",
                    $"cannot create Gibbs element matrix: {ex.Message}");
            }
        }

        // ── unit-op parameters ─────────────────────────────────────────────
        foreach (var o in doc.Objects.Where(o => o.Kind == "unitOp" && o.Parameters is { Count: > 0 }))
        {
            if (!byTag.TryGetValue(o.Tag, out var so)) continue;
            var def = UnitOpCatalog.Types[o.Type!];

            // 199 — the calculation mode is resolved and WRITTEN FIRST, before any parameter.
            //
            // FR-002 is an ordering requirement, and this loop iterates a Dictionary: the mode
            // cannot simply be another entry in it, because the engine reads the parameters that
            // the mode in force at write time selects. Resolving here is also what lets the filter
            // below exist at all.
            var (mode, modeExplicit) = ResolveCalcMode(so, o, def, Error, Warn);

            foreach (var (name, raw) in o.Parameters!)
            {
                // 199 — `calcMode` is consumed above, not by the generic setter. It is declared in
                // the catalog so it crosses the wire and appears in the parameter list the app and
                // the capture read; it is not a property the binder can write, because which
                // PROPERTY holds it differs per unit op (four names) and its value is an enum
                // member, not a quantity.
                if (string.Equals(name, "calcMode", StringComparison.OrdinalIgnoreCase)) continue;

                // 199 FR-003 — a parameter the active mode does not read is NOT SENT. This one line
                // is what makes the ignored-input bug structurally impossible: before it, five
                // parameters were accepted, echoed, converged and ignored, because the engine reads
                // only what its mode selects and nothing ever chose the mode.
                if (mode is not null && def.CalcMode is { } cmd && !cmd.Reads(mode, name))
                {
                    var readers = cmd.ModesReading(name);
                    var alternatives = readers.Length > 0
                        ? $"Modes that read it: {string.Join(", ", readers)}."
                        : "No mode on this unit op reads it.";
                    if (modeExplicit)
                        // FR-003a — an EXPLICIT mode plus a parameter it cannot read is a
                        // contradiction the caller stated, and there is no reading of the request
                        // the runner can honour. The heatExchanger precedent: refusing beats
                        // picking one silently.
                        Error("PARAMETER_NOT_READ_BY_MODE", o.Tag,
                            $"'{o.Tag}' is in calculation mode '{mode}', which does not read '{name}'. " +
                            $"{alternatives} Remove the parameter or change the mode.");
                    else
                        // The mode was INFERRED, so the contradiction is the system's, not the
                        // caller's. Refusing here would fail documents that converge today — which
                        // is precisely the back-compat risk D2 names — so it drops and says so.
                        Warn("PARAMETER_NOT_READ_BY_MODE", o.Tag,
                            $"'{o.Tag}' has no explicit calculation mode; '{mode}' was inferred. " +
                            $"'{name}' is not read in that mode and was dropped. {alternatives}");
                    continue;
                }

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
    /// <summary>
    /// 199 — which calculation mode this unit op runs, and whether the caller said so.
    /// </summary>
    /// <remarks>
    /// An EXPLICIT `calcMode` always wins. With none, the declared inference rules run in order and
    /// the first whose parameter is present takes it — which is the pre-199 behaviour, so a document
    /// saved before this feature solves unchanged (FR-004). With neither, nothing is written and the
    /// unit op keeps whatever the engine (or the creation path) already set.
    ///
    /// The inference rules used to be seven hand-written hatches: six inside `ApplyParameter` and one
    /// on `Vessel` at creation, which is why searching this method alone found six and missed the
    /// seventh. They are declarations on `CalcModeDef` now — the rule and its exceptions in one
    /// place, and readable by the catalog endpoint so the app can show what will happen.
    ///
    /// Returns the WIRE mode name, never the engine member: everything downstream keys on the
    /// normalized name, and the engine member is a per-unit-op detail that must not leak.
    /// </remarks>
    private static (string? Mode, bool Explicit) ResolveCalcMode(
        ISimulationObject so, FlowObject o, UnitOpDef def,
        Action<string, string?, string, string?> error, Action<string, string?, string> warn)
    {
        if (def.CalcMode is not { } cm) return (null, false);

        string? wire = null;
        var isExplicit = false;
        if (o.Parameters is not null &&
            o.Parameters.FirstOrDefault(kv => string.Equals(kv.Key, "calcMode", StringComparison.OrdinalIgnoreCase))
                is { Key: not null } entry &&
            entry.Value.ValueKind == JsonValueKind.String)
        {
            wire = entry.Value.GetString();
            isExplicit = true;
            if (!cm.TryResolve(wire!, out _))
            {
                // Name the alternatives, not just the failure. Spec 138 measured that the model
                // guesses names the system already holds; an error that lists them is an
                // instruction rather than a dead end.
                error("UNKNOWN_CALC_MODE", o.Tag,
                    $"'{o.Tag}' has no calculation mode '{wire}'. Known modes for '{def.Type}': " +
                    string.Join(", ", cm.Modes().Select(m => m.Name)) + ".", null);
                return (null, false);
            }
        }
        else
        {
            foreach (var (param, inferred) in cm.Infer)
                if (o.Parameters is not null &&
                    o.Parameters.Keys.Any(k => string.Equals(k, param, StringComparison.OrdinalIgnoreCase)))
                {
                    wire = inferred;
                    break;
                }
        }

        if (wire is null) return (null, false);
        if (!cm.TryResolve(wire, out var member)) return (null, false);

        var prop = so.GetType().GetProperty(cm.ClrProperty);
        if (prop is null)
        {
            // The catalog claims a property this DWSIM build does not have. Loud, because it means
            // the declaration and the engine have diverged — the exact drift this spec removes.
            error("BUILD_FAILED", o.Tag,
                $"'{def.Type}' declares calculation-mode property '{cm.ClrProperty}', " +
                "which this engine build does not expose.", null);
            return (null, false);
        }
        prop.SetValue(so, Enum.Parse(prop.PropertyType, member!));
        return (wire, isExplicit);
    }

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
        // 199 — the reactor and heater/cooler MODE hatches that used to live here are gone. They
        // are declared `Infer` rules on `CalcModeDef` now and run in `ResolveCalcMode`, BEFORE any
        // parameter is written, which is what FR-002 requires and what this location could never
        // provide: a hatch fires when its own parameter happens to be reached in dictionary order.
        // What remains below is NOT a mode hatch — it is a unit conversion, and it stays.
        if (def.Type is "heater" or "cooler")
        {
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
            // 199 — the mode SELECTION moved to `ResolveCalcMode`'s declared Infer rules. The
            // refusal above did not, and must not: "both outlet temperatures are set" is an
            // over-specification of the DUTY, true in every mode, and not something a mode map can
            // express. It is also the precedent FR-003a generalizes.
            _ = hot;
        }
        // 099 US5 — a stated Kv implies a Kv calculation mode. The valve's default is `DeltaP` or
        // `OutletPressure`, under which `Kv` is read by nothing: accepted, converged, ignored.
        //
        // `Kv_General` of the four Kv modes (Liquid/Gas/Steam/General), because it is the one that
        // does not require the caller to have already decided the phase — and the phase is the
        // engine's answer, not the engineer's input. 099's tasks named `kvLiquid`/`kvGas` as
        // PARAMETERS; they are MODES, the same confusion as the splitter's `StreamMassFlowSpec`.
        // 199 — declared as `("kv", "kvGeneral")` on the valve's CalcModeDef. Exposing the mode is
        // also what finally makes Kv_Liquid/Gas/Steam reachable: the hatch could only ever pick the
        // one mode it named.
        // 099 US5 — a stated outlet flow implies the splitter's flow-spec mode. Its default is
        // `SplitRatios`, under which `StreamFlowSpec` is read by nothing: the setpoint is accepted,
        // the flowsheet converges, and the split is whatever the ratios say. The same silent-ignore
        // this escape-hatch region exists for.
        // 199 — declared as `("outletMassFlow", "streamMassFlowSpec")`. `streamMoleFlowSpec` is
        // reachable for the first time: the engine reads the same setpoint as a MOLE flow in that
        // mode, and no hatch could select it.
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
