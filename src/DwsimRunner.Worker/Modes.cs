// dwsim-runner Worker — GPL-3.0
// Per-mode handlers dispatched by Program.Main (T006). Each takes a Job and
// returns a payload serialized as the single JSON document on stdout. Engine
// work goes through DWSIM.Automation.Automation3; nothing here touches HTTP,
// the file system beyond the job's template path, or the API process.
//
// Constitution I (DWSIM types only in Worker files) is preserved: every
// reference to DWSIM.* lives here or in FlowsheetBuilder/UnitOpCatalog, never
// in the API project.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DWSIM.Automation;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;

namespace DwsimRunner.Worker;

static class Modes
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── catalog (T009) ───────────────────────────────────────────────────────
    // Engine compounds + property packages + the static UnitOpCatalog allowlist.
    // Compounds come straight off Automation3 (populated at construction);
    // property packages need a flowsheet — Automation3.AvailablePropertyPackages
    // stays empty headless, so we go through CreateFlowsheet() like
    // FlowsheetBuilder does.
    public static CatalogResult Catalog(Job job)
    {
        var auto = new Automation3();
        var engineVersion = ExtractVersion(auto);

        var compounds = auto.AvailableCompounds
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CompoundOut(
                Name: kv.Key,
                Formula: SafeString(kv.Value, "Formula"),
                CasNumber: SafeString(kv.Value, "CAS_Number")))
            .ToList();

        var packageNames = ((System.Collections.IEnumerable)auto.AvailablePropertyPackages.Values)
            .Cast<IPropertyPackage>().Select(pp => pp.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (packageNames.Count == 0)
        {
            var fs = auto.CreateFlowsheet();
            if (fs is not null)
                packageNames = fs.GetAvailablePropertyPackages().Cast<string>()
                    .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        }
        var packages = packageNames
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name =>
            {
                var (id, description) = PackageCatalog.Classify(name);
                return new PropertyPackageOut(id, name, description);
            })
            .ToList();

        return new CatalogResult(engineVersion, compounds, packages, UnitOpCatalog.ToPayload(),
            EngineInventory(auto));
    }

    /// <summary>
    /// Every unit-op kind the ENGINE declares, and whether this runner exposes it (099 FR-001/FR-004,
    /// implementing 034 FR-020/021 — the part of 034 that did not land).
    /// </summary>
    /// <remarks>
    /// Every ledger in iskra compares the app to the runner's hand-written allowlist. NOTHING compared
    /// either side to what DWSIM itself declares — which is why the claim "DWSIM has no electrolyzer
    /// unit op" had nothing on the other side of it and stood for a year, while `WaterElectrolyzer`
    /// shipped in the DLL we already vendor. This is that other side.
    ///
    /// Three sources, unioned, because DWSIM can construct a unit op two ways and advertise it a third:
    ///   - `ObjectType` enum members — everything the type system knows, including legacy members with
    ///     no factory path left.
    ///   - `GetAvailableFlowsheetObjectTypeNames()` — the engine's own factory list. This is what
    ///     `instantiable` reports, and it is what keeps the ledger's rows meaningful: without it, a
    ///     dozen dead enum members demand ledger rows that say nothing.
    ///   - `ExternalUnitOperations` — the plugin registry. **NOT empty**, contrary to what 099's own
    ///     contract assumed ("the always-empty external set"): this build registers six, including
    ///     Water Electrolyzer, PEM Fuel Cell, Solar Panel, Wind Turbine, Hydroelectric Turbine and a
    ///     Reaktoro Gibbs reactor. `source` is what makes that visible instead of indistinguishable
    ///     from "we never looked".
    ///
    /// `exposedAs` is a REVERSE LOOKUP over `UnitOpCatalog.Types`, computed here at response time and
    /// never stored. That is deliberate and it is the whole reason this endpoint can be trusted: a
    /// stored mapping is a second table free to drift from the allowlist, which is the exact defect
    /// class this endpoint exists to expose.
    /// </remarks>
    private static List<EngineInventoryEntry> EngineInventory(Automation3 auto)
    {
        // engine ObjectType name → the runner's wire type. Reverse of the allowlist, so it cannot lie.
        var exposed = UnitOpCatalog.Types.Values
            .GroupBy(d => d.ObjectType.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Type, StringComparer.Ordinal);

        var instantiable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var externals = new List<string>();
        try
        {
            var fs = auto.CreateFlowsheet();
            if (fs is not null)
            {
                // `.Cast<string>()` because this returns a NON-generic collection — same shape as
                // `GetAvailablePropertyPackages()` a few lines up, which casts for the same reason.
                //
                // KEYED ON `Key(...)`, NOT ON THE RAW STRING. The factory list returns DISPLAY names —
                // "Water Electrolyzer", "Gibbs Reactor (Reaktoro)" — while `ObjectType` members are
                // Pascal-case identifiers. Comparing them raw matched only the single-word types and
                // reported 15 instantiable against a true 49: every multi-word type looked dead. The
                // failure was invisible in the sense that mattered, because the 15 it did report were
                // all real, so the answer was plausible and quietly two-thirds short.
                foreach (var n in fs.GetAvailableFlowsheetObjectTypeNames().Cast<string>())
                    if (!string.IsNullOrWhiteSpace(n)) instantiable.Add(Key(n));
                // `ExternalUnitOperations` is a dictionary of plugin-supplied ops. Best-effort: an
                // engine build without the member must not fail the whole catalog.
                if (fs.GetType().GetProperty("ExternalUnitOperations")?.GetValue(fs)
                    is System.Collections.IDictionary ext)
                    foreach (var k in ext.Keys) if (k is not null) externals.Add(k.ToString()!);
            }
        }
        catch
        {
            // A reflection failure here degrades `instantiable` to false for everything, which reads
            // as "nothing is buildable" — visibly wrong rather than quietly wrong, and the endpoint
            // still answers. The catalog must not die because an inventory field could not be filled.
        }

        // INSTANTIABLE IS MEASURED BY CONSTRUCTING THE THING, not by reading a list.
        //
        // The first cut of this used `GetAvailableFlowsheetObjectTypeNames()`, and the result was
        // self-refuting: `separator` and `mixer` came back NOT instantiable while carrying an
        // `exposedAs` — the runner builds and solves both, in tests that pass. That list is a
        // palette, and a palette is a statement about a GUI. Believing it would have put "the engine
        // cannot construct a separator" into the artifact that exists to be the authority on what the
        // engine can construct.
        //
        // So each type is actually added to a scratch flowsheet. Failures are expected and are the
        // answer — a legacy enum member with no factory path throws, and that is the fact worth
        // recording. Wrapped per type: one throwing member must not cost the other 72.
        var probe = auto.CreateFlowsheet();
        bool CanBuild(ObjectType t)
        {
            if (probe is null) return instantiable.Contains(Key(t.ToString()));
            try { return probe.AddObject(t, 50, 50, $"probe_{t}") is not null; }
            catch { return false; }
        }

        var entries = Enum.GetNames<ObjectType>()
            .Select(name => new EngineInventoryEntry(
                Name: name,
                DisplayName: Humanize(name),
                Source: "enum",
                Instantiable: Enum.TryParse<ObjectType>(name, out var ot)
                    ? CanBuild(ot)
                    // Unparseable is a contradiction (the name came FROM the enum), so fall back to
                    // the palette rather than silently reporting false.
                    : instantiable.Contains(Key(name)),
                ExposedAs: exposed.TryGetValue(name, out var wire) ? wire : null))
            .ToList();

        // Dedup on `Key(...)` for the same reason. `WaterElectrolyzer` is BOTH an `ObjectType` member
        // and an external unit operation called "Water Electrolyzer", so a raw comparison listed one
        // unit op twice — once as an enum member reported not instantiable, once as an external
        // reported instantiable. Two rows for one thing, disagreeing, in the artifact whose entire job
        // is to be the authority on what exists.
        var known = new HashSet<string>(entries.Select(e => Key(e.Name)), StringComparer.OrdinalIgnoreCase);
        entries.AddRange(externals.Where(n => !known.Contains(Key(n)))
            .Select(n => new EngineInventoryEntry(n, Humanize(n), "external", true, null)));

        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>`WaterElectrolyzer` → `Water Electrolyzer`. Presentation only — never a join key.</summary>
    private static string Humanize(string pascal) =>
        System.Text.RegularExpressions.Regex.Replace(pascal, "(?<=[a-z0-9])(?=[A-Z])", " ");

    /// <summary>
    /// The comparison key for one unit-op kind: alphanumerics only, case-folded.
    /// </summary>
    /// <remarks>
    /// DWSIM names the same unit op three ways — `ObjectType.WaterElectrolyzer`, the factory list's
    /// "Water Electrolyzer", and the external registry's "Gibbs Reactor (Reaktoro)" with punctuation.
    /// Every comparison in this inventory goes through here, so a spelling difference cannot turn into
    /// a capability claim. It is a KEY, never a display value and never a wire type: `exposedAs` still
    /// carries the runner's real wire string.
    /// </remarks>
    private static string Key(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string ExtractVersion(Automation3 auto)
    {
        try
        {
            // auto.GetVersion() returns "DWSIM version 9.0.5.0 (...)".
            var raw = auto.GetVersion() ?? "";
            var tokens = raw.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in tokens)
                if (Version.TryParse(t, out var v) && v.Major > 0)
                    return v.ToString();
        }
        catch { /* fall through */ }
        // Fall back to the assembly version of DWSIM.Automation.dll.
        try { return typeof(Automation3).Assembly.GetName().Version?.ToString() ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static string SafeString(object obj, string prop) =>
        obj.GetType().GetProperty(prop)?.GetValue(obj) as string ?? "";

    // ── validate (T022) ─────────────────────────────────────────────────────
    // Document → FlowsheetBuilder.Build (skip solve) → engine issues. Collects
    // every issue the engine raises before any abort; semantic validation per
    // FR-VAL-002. Returns { valid, issues }.
    public static ValidationOutcome Validate(Job job)
    {
        var doc = RequireDocument(job);
        var auto = new Automation3();
        try
        {
            var (_, info, warnings) = FlowsheetBuilder.Build(auto, FlowsheetBuilder.ParseDocument(doc));
            // Construction succeeded → no errors. Surface warnings as issues.
            var issues = warnings.Select(w => new IssueOut(w.Severity, w.Code, w.Tag, w.Path, w.Message)).ToList();
            return new ValidationOutcome(true, issues);
        }
        catch (BuildAbortException ex)
        {
            // validate emits every issue collected, no matter which Code is on the abort.
            return new ValidationOutcome(false,
                ex.Issues.Select(i => new IssueOut(i.Severity, i.Code, i.Tag, i.Path, i.Message)).ToList());
        }
    }

    // ── build-solve (T024) ──────────────────────────────────────────────────
    // Build → CalculateFlowsheet → harvest BuildReport (FR-BUILD-001..003). On
    // success, optionally save the flowsheet into USER_TEMPLATES_PATH via the
    // engine's SaveFlowsheet2 (.dwxmz). Non-convergence is a 200 with
    // converged:false (never an error code).
    public static BuildReport BuildSolve(Job job)
    {
        var doc = RequireDocument(job);
        var sw = Stopwatch.StartNew();
        var auto = new Automation3();

        var (fs, build, warnings) = FlowsheetBuilder.Build(auto, FlowsheetBuilder.ParseDocument(doc));

        // 120 US5 — document-scoped /compare and /optimize cases: the same per-case
        // overrides the template path applies, via the same shared helper (drift is how a
        // property becomes settable on one solve path and not the other).
        Solver.ApplyOverrides(fs, job.Overrides);

        // ── solve ──────────────────────────────────────────────────────────
        auto.CalculateFlowsheet2(fs);
        bool converged = fs.Solved;
        var engineWarnings = new List<string>();
        if (!converged && !string.IsNullOrEmpty(fs.ErrorMessage))
            engineWarnings.Add(fs.ErrorMessage);

        // ── harvest streams/energy/unitOps (reusing the spec-001 shape) ───────
        var streams = new List<StreamRow>();
        var energy = new List<EnergyRow>();
        var unitOps = new List<UnitOpRow>();

        foreach (var obj in fs.SimulationObjects.Values)
        {
            switch (obj)
            {
                case DWSIM.Thermodynamics.Streams.MaterialStream ms:
                    streams.Add(HarvestStream(ms));
                    break;
                case DWSIM.UnitOperations.Streams.EnergyStream es:
                    // 099 US1 — a synthesized electrolyzer power stream is not in the DOCUMENT, so
                    // reporting it would have the app fold back a stream its own side does not
                    // contain. Hidden in BOTH harvests: the worker has two entry points, and fixing
                    // one makes the answer depend on which one ran (Hazard 7).
                    if (ElectrolyzerConfigurator.IsSynthesizedPower(es.GraphicObject.Tag)) break;
                    energy.Add(new EnergyRow(es.GraphicObject.Tag,
                        es.EnergyFlow is double ef && double.IsFinite(ef) ? Math.Round(ef, 1) : null));
                    break;
                default:
                    unitOps.Add(HarvestUnitOp(obj));
                    break;
            }
        }

        engineWarnings.AddRange(warnings.Select(w => $"[{w.Code}] {w.Message}"));

        // ── optional save (T037) ─────────────────────────────────────────────
        // Conflict/overwrite policy and listing metadata (sidecar, template
        // block) are owned by the API; the worker's only job is the engine
        // save. Persist even when unsolved (US3: solvedAtSave:false).
        // Spec 011 Cut 2: a save failure MUST NOT throw — the solve result
        // is the user's answer; the save is a best-effort side-effect. The
        // API infers save failure from the absence of the file and reports
        // a soft `template.saved:false` block rather than a 500.
        if (job.SavePath is { Length: > 0 } path)
        {
            try
            {
                auto.SaveFlowsheet2(fs, path);
            }
            catch (Exception)
            {
                // Swallow: the solve succeeded; the file won't exist, and
                // the API sets template.saved:false in the response.
            }
        }

        return new BuildReport(
            Converged: converged,
            ElapsedMs: sw.ElapsedMilliseconds,
            Streams: streams,
            Energy: energy,
            UnitOps: unitOps,
            Warnings: engineWarnings,
            Build: build,
            Template: null);
    }

    internal static StreamRow HarvestStream(DWSIM.Thermodynamics.Streams.MaterialStream ms)
    {
        var comp = new Dictionary<string, double>();
        // MASS fractions alongside mole fractions. Water/ethanol separations are stated on a mass
        // basis by every tutorial that uses them, and mole->mass needs molar masses that the caller
        // does not have — DWSIM does, and reports both, so harvesting both is one line instead of a
        // molar-mass table nobody should be maintaining twice.
        var compMass = new Dictionary<string, double>();
        foreach (var c in ms.Phases[0].Compounds.Values)
        {
            if (c.MoleFraction is double mf && double.IsFinite(mf) && mf > 1e-9)
                comp[c.Name] = Math.Round(mf, 6);
            if (c.MassFraction is double xf && double.IsFinite(xf) && xf > 1e-9)
                compMass[c.Name] = Math.Round(xf, 6);
        }
        // 120 US1: per-phase blocks, iterated by the phase's NAME — never its slot index.
        // Phases[0] is the MIXTURE aggregate (molarfraction 1.0 by definition), which is exactly
        // why the old label (`Phases[0].Properties.molarfraction == 1 ? "vapor" : null`) was noise
        // in both directions. "OverallLiquid" is likewise an aggregate of the liquid slots and is
        // skipped. Everything asserted here is pinned by Tier B PerPhaseTests against the live
        // engine (liquid water is liquid), per specs/036-runner-fidelity/research.md:390.
        var blocks = new List<StreamPhaseBlock>();
        var liquidSeen = 0;
        foreach (var p in ms.Phases.Values)
        {
            var engineName = p.Name ?? "";
            if (engineName is "Mixture" or "OverallLiquid") continue;
            if (p.Properties.molarfraction is not double beta || !double.IsFinite(beta) || beta <= 1e-9)
                continue;

            string name;
            if (engineName.StartsWith("Vapor", StringComparison.OrdinalIgnoreCase)) name = "vapor";
            else if (engineName.StartsWith("Solid", StringComparison.OrdinalIgnoreCase)) name = "solid";
            else name = ++liquidSeen == 1 ? "liquid" : "liquid2";   // Liquid1/Liquid2/Liquid3/Aqueous

            var phaseComp = new Dictionary<string, double>();
            foreach (var c in p.Compounds.Values)
                if (c.MoleFraction is double pmf && double.IsFinite(pmf) && pmf > 1e-9)
                    phaseComp[c.Name] = Math.Round(pmf, 6);

            blocks.Add(new StreamPhaseBlock(
                Name: name,
                MoleFraction: Math.Round(beta, 6),
                Composition: phaseComp.Count > 0 ? phaseComp : null,
                DensityKgM3: Round(p.Properties.density),
                MolecularWeight: Round(p.Properties.molecularWeight),
                HeatCapacityKJKgK: Round(p.Properties.heatCapacityCp, 0, 1, 4),
                // Pa*s, magnitude ~1e-3..1e-5: fixed 3 decimals would DESTROY it (the entropy
                // lesson a hundred lines down) — 8 decimals keeps ~4 significant figures.
                ViscosityPaS: Round(p.Properties.viscosity, 0, 1, 8)));
        }
        var vaporFraction = blocks.FirstOrDefault(b => b.Name == "vapor")?.MoleFraction ?? 0.0;
        string? phaseLabel = blocks.Count == 0 ? null
            : vaporFraction > 0.9999 ? "vapor"
            : vaporFraction < 1e-4
                ? (blocks.All(b => b.Name == "solid") ? "solid" : "liquid")
                : "two-phase";

        return new StreamRow(
            Name:           ms.GraphicObject.Tag,
            Phase:          phaseLabel,
            TemperatureC:   Round(ms.Phases[0].Properties.temperature, -273.15),
            // SIX decimals of bar (0.1 Pa), not three. At three, 1.01325 bar — one atmosphere —
            // came back as 1.013, and a caller asking for pascals got 101300 instead of 101325. The
            // 25 Pa was not approximated, it was DESTROYED: multiplying by 1e5 cannot recover it,
            // which is the lesson this file already records about entropy a hundred lines down.
            //
            // Only pressure needs it, because it is the one quantity whose SI unit is 100000x the
            // reported unit — 3 decimals of bar is 100 Pa of resolution. Temperature (°C) and the
            // flows are reported near their SI magnitude, where 3 decimals is finer than the engine
            // converges to.
            PressureBar:    Round(ms.Phases[0].Properties.pressure, 0, 1e-5, 6),
            MassFlowKgH:    Round(ms.Phases[0].Properties.massflow, 0, 3600),
            MolarFlowKmolH: Round(ms.Phases[0].Properties.molarflow, 0, 3.6),
            CompositionMol: comp,
            // Already kg/m3 in DWSIM's SI store, so no scale — unlike massflow (kg/s -> kg/h) and
            // pressure (Pa -> bar) above. Getting that wrong is the enthalpy/1000 bug in this file's
            // own comments: self-consistent, unflagged, and wrong by three orders of magnitude.
            DensityKgM3:    Round(ms.Phases[0].Properties.density),
            CompositionMass: compMass.Count > 0 ? compMass : null,
            VaporFraction:  blocks.Count > 0 ? Math.Round(vaporFraction, 6) : null,
            Phases:         blocks.Count > 0 ? blocks : null);

        static double? Round(double? si, double offset = 0, double scale = 1, int digits = 3) =>
            si is double v && double.IsFinite(v) ? Math.Round(v * scale + offset, digits) : null;
    }

    private static UnitOpRow HarvestUnitOp(ISimulationObject obj)
    {
        static double? Num(object o, string prop)
        {
            try
            {
                var v = o.GetType().GetProperty(prop)?.GetValue(o);
                return v is not null && double.IsFinite(Convert.ToDouble(v)) ? Convert.ToDouble(v) : null;
            }
            catch { return null; }
        }
        static double? RoundN(double? v, int d) => v is double x && double.IsFinite(x) ? Math.Round(x, d) : null;

        var type = FriendlyType(obj);
        var deltaQ = Num(obj, "DeltaQ") ?? Num(obj, "Q");
        var isDriver = type is "compressor" or "pump" or "expander";
        return new UnitOpRow(
            Name: obj.GraphicObject.Tag,
            Type: type,
            PowerKw: isDriver ? RoundN(deltaQ, 1) : null,
            DutyKw: isDriver ? null : RoundN(deltaQ, 1),
            OutletTemperatureC: RoundN(Num(obj, "TOut") is double to ? to - 273.15 : null, 3),
            OutletPressureBar: RoundN(Num(obj, "POut") is double po ? po * 1e-5 : null, 3));
    }

    private static string FriendlyType(object obj) => obj switch
    {
        DWSIM.Thermodynamics.Streams.MaterialStream => "materialStream",
        DWSIM.UnitOperations.Streams.EnergyStream => "energyStream",
        _ => obj.GetType().Name switch
        {
            "WaterElectrolyzer" => "waterElectrolyzer",
            "Compressor" => "compressor",
            "Pump" => "pump",
            "Expander" or "Turbine" => "expander",
            "Heater" => "heater",
            "Cooler" => "cooler",
            "HeatExchanger" => "heatExchanger",
            "Valve" => "valve",
            "Mixer" => "mixer",
            "Splitter" => "splitter",
            "Vessel" => "separator",
            var n when n.Contains("Reactor", StringComparison.OrdinalIgnoreCase) => "reactor",
            "Recycle" => "recycle",
            var n => char.ToLowerInvariant(n[0]) + n[1..],
        },
    };

    // ── flash (T044) ────────────────────────────────────────────────────────
    // T-P / P-H / P-S flash without a flowsheet. The engine's
    // IPropertyPackage.CalculateEquilibrium2(FlashCalculationType, spec1, spec2, amount)
    // runs the flash directly on a composition vector — no flowsheet objects
    // needed. Request validation per FR-VAL (compounds non-empty, fractions
    // normalize, flashType/spec pair); FLASH_INVALID covers all pre-engine
    // failures so the API surfaces a single taxonomy code.
    public static FlashResult Flash(Job job)
    {
        if (job.Flash is not { ValueKind: JsonValueKind.Object } flashEl)
            throw new WorkerInputException("FLASH_INVALID", "flash request is missing");

        var flash = flashEl.Deserialize<FlashRequest>(JsonOpts)
            ?? throw new WorkerInputException("FLASH_INVALID", "flash request did not parse");

        if (flash.Compounds is not { Count: >= 1 }) throw new WorkerInputException("FLASH_INVALID", "compounds must be non-empty");
        if (flash.Composition is null || flash.Composition.Fractions is null || flash.Composition.Fractions.Count == 0)
            throw new WorkerInputException("FLASH_INVALID", "composition is required");
        var sum = flash.Composition.Fractions.Values.Sum();
        if (Math.Abs(sum - 1.0) > 1e-4)
            throw new WorkerInputException("FLASH_INVALID", $"composition fractions sum to {sum:G6}; must be 1 (±1e-4)");
        if (flash.FlashType is null) throw new WorkerInputException("FLASH_INVALID", "flashType is required (TP|PH|PS)");
        var pp = (flash.PropertyPackage ?? "").Trim();
        if (pp.Length == 0) throw new WorkerInputException("FLASH_INVALID", "propertyPackage is required");

        var auto = new Automation3();

        // Headless engines often report an empty/null-named
        // AvailablePropertyPackages — fall back to the flowsheet-level listing
        // (same workaround as catalog mode).
        var engineNames = ((System.Collections.IEnumerable)auto.AvailablePropertyPackages.Values)
            .Cast<IPropertyPackage>().Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var flowsheetForNames = engineNames.Count == 0 ? auto.CreateFlowsheet() : null;
        if (flowsheetForNames is not null)
            engineNames = flowsheetForNames.GetAvailablePropertyPackages().Cast<string>()
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var ppName = PackageCatalog.Resolve(pp, engineNames)
            ?? throw new WorkerInputException("FLASH_INVALID",
                $"property package '{pp}' not found; available ids: {string.Join(", ", engineNames.Select(n => PackageCatalog.Classify(n).Id).Distinct().Order())}");

        // Resolve the engine compound names (case-insensitive) and build the
        // composition vector in the engine's compound order. Unknown compound
        // → FLASH_INVALID with suggestions, matching FlowsheetBuilder.
        var available = auto.AvailableCompounds.Keys.ToList();
        var resolvedCompounds = new List<string>();
        var compositionVector = new List<double>();
        foreach (var requested in flash.Compounds)
        {
            var match = available.FirstOrDefault(k => string.Equals(k, requested, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var suggestions = available
                    .Where(k => k.Contains(requested, StringComparison.OrdinalIgnoreCase)
                             || requested.Length >= 4 && k.StartsWith(requested[..4], StringComparison.OrdinalIgnoreCase))
                    .Take(5).ToList();
                throw new WorkerInputException("FLASH_INVALID",
                    $"compound '{requested}' not found" + (suggestions.Count > 0 ? $"; did you mean: {string.Join(", ", suggestions)}?" : ""));
            }
            resolvedCompounds.Add(match);
            compositionVector.Add(flash.Composition.Fractions.FirstOrDefault(f =>
                string.Equals(f.Key, requested, StringComparison.OrdinalIgnoreCase)).Value);
        }

        // Build a temporary flowsheet holding compounds + property package so
        // the package's CalculateEquilibrium2 has a working backing store.
        var fs = flowsheetForNames ?? auto.CreateFlowsheet()
            ?? throw new WorkerInputException("WORKER_CRASH", "engine failed to create a flowsheet");
        foreach (var c in resolvedCompounds) fs.AddCompound(c);
        fs.CreateAndAddPropertyPackage(ppName);
        var package = fs.PropertyPackages.Values.First();

        // CalculateEquilibrium2 pulls the feed composition from the package's
        // CurrentMaterialStream (RET_VMOL) — a bare package NREs. Feed it a
        // scratch stream carrying the requested overall composition.
        var feed = (IMaterialStream)fs.AddObject(
            DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.MaterialStream, 50, 50, "FLASH-FEED");
        if (string.Equals(flash.Composition.Basis, "mass", StringComparison.OrdinalIgnoreCase))
            feed.SetOverallMassComposition([.. compositionVector]);
        else
            feed.SetOverallMolarComposition([.. compositionVector]);
        package.CurrentMaterialStream = feed;

        // Map flashType → FlashCalculationType + the two spec values in SI.
        DWSIM.Interfaces.Enums.FlashCalculationType calcType;
        double spec1, spec2;
        switch (flash.FlashType.ToUpperInvariant())
        {
            case "TP":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.PressureTemperature;
                spec1 = RequireSi(flash.Pressure, "pressure", "bar");
                spec2 = RequireSi(flash.Temperature, "temperature", "K");
                break;
            case "PH":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.PressureEnthalpy;
                spec1 = RequireSi(flash.Pressure, "pressure", "bar");
                spec2 = RequireSi(flash.Enthalpy, "enthalpy", "kJ/kg");
                break;
            case "PS":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.PressureEntropy;
                spec1 = RequireSi(flash.Pressure, "pressure", "bar");
                spec2 = RequireSi(flash.Entropy, "entropy", "kJ/kg.K");
                break;
            // 120 US2 — the remaining measurable pairs, by MEASUREMENT (2026-08-01, 9.0.5.0):
            // - PVF/TVF work (TVF finds Psat(100 C) = 1.014 bar) and are exposed below.
            // - TH/TS (TemperatureEnthalpy/TemperatureEntropy) CRASH the engine — hard worker
            //   death, not an exception — under both STEAM and PR. Deliberately NOT exposed;
            //   the capability fixture records the crash verdict. Re-measure before re-adding.
            // - PSF/TSF (solid fraction): solids are ledgered will-not-yet (no flash-algorithm
            //   selection), so a solid-fraction flash would be a knob wired to nothing.
            case "PVF":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.PressureVaporFraction;
                spec1 = RequireSi(flash.Pressure, "pressure", "bar");
                spec2 = RequireSi(flash.VaporFraction, "vaporFraction", "");
                break;
            case "TVF":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.TemperatureVaporFraction;
                spec1 = RequireSi(flash.Temperature, "temperature", "K");
                spec2 = RequireSi(flash.VaporFraction, "vaporFraction", "");
                break;
            default:
                throw new WorkerInputException("FLASH_INVALID", $"flashType '{flash.FlashType}' not supported (TP|PH|PS|PVF|TVF)");
        }

        var result = package.CalculateEquilibrium2(calcType, spec1, spec2, 1.0);
        if (result?.ResultException is not null)
            throw new WorkerInputException("FLASH_INVALID",
                $"flash calculation failed: {result.ResultException.Message}");

        // ── harvest phases (Vapor / Liquid1 / Liquid2 / Solid) ────────────────
        var phases = new List<PhaseOut>();
        double vaporFraction = 0;
        var compoundsInOrder = resolvedCompounds;

        if (result.GetVaporPhaseMoleFraction() is double vf && double.IsFinite(vf) && vf > 1e-9)
        {
            vaporFraction = vf;
            var moleFracs = result.GetVaporPhaseMoleFractions() ?? [];
            phases.Add(BuildPhase("Vapor", vf, moleFracs, compoundsInOrder));
        }
        if (result.GetLiquidPhase1MoleFraction() is double l1 && double.IsFinite(l1) && l1 > 1e-9)
            phases.Add(BuildPhase("Liquid", l1, result.GetLiquidPhase1MoleFractions() ?? [], compoundsInOrder));
        if (result.GetLiquidPhase2MoleFraction() is double l2 && double.IsFinite(l2) && l2 > 1e-9)
            phases.Add(BuildPhase("Liquid2", l2, result.GetLiquidPhase2MoleFractions() ?? [], compoundsInOrder));
        if (result.GetSolidPhaseMoleFraction() is double sf && double.IsFinite(sf) && sf > 1e-9)
            phases.Add(BuildPhase("Solid", sf, result.GetSolidPhaseMoleFractions() ?? [], compoundsInOrder));

        // Engine-side T/P/h/s; null when the calc didn't converge on them.
        //
        // NO UNIT CONVERSION. DWSIM's CalculatedEnthalpy/CalculatedEntropy are ALREADY kJ/kg and
        // kJ/kg.K — the same units RequireSi converts the PH/PS *inputs* into, a few lines up.
        // These two lines divided by 1000, so the response carried MJ/kg under a kJ/kg field name.
        //
        // The round trip made it unmistakable: a PH flash fed h = 63 kJ/kg at 3 bar solved to
        // 14.94 C — correct — and then reported enthalpyKJKg = 0.063 for that same state. Input
        // and output disagreed by 1000x about what the field meant.
        //
        // Why this mattered more than a display glitch: a duty computed from it is 1000x too small
        // and entirely self-consistent (Q = 0.052 kW instead of 52 kW), with no error and no
        // warning — a number an engineer can put on a datasheet. It surfaced only because a
        // caller happened to cross-check against Cp*dT.
        //
        // Entropy was worse than mislabelled, it was DESTROYED: water at 15 C has
        // s = 0.2244 kJ/kg.K, which /1000 makes 0.000224, which Math.Round(_, 3) below turns
        // into 0. Multiplying cannot recover a value that has been rounded away.
        double? enthalpyKJKg = result.CalculatedEnthalpy is double h && double.IsFinite(h) ? h : null;
        double? entropyKJKgK = result.CalculatedEntropy is double se && double.IsFinite(se) ? se : null;

        // DENSITY. `FlashCalculationResult` carries T/P/h/s and phase mole fractions — no density —
        // so it comes from the scratch feed stream instead: set it to the state the flash just found
        // and let the engine populate the phase properties the same way a solved flowsheet does.
        // That is deliberately the SAME member the solve harvest reads
        // (`Phases[0].Properties.density`), so the two paths cannot report different densities for
        // the same state.
        //
        // Best-effort: a null density must never fail a flash that otherwise converged. The caller
        // distinguishes "not reported" from "wrong" and says so.
        double? densityKgM3 = null;
        try
        {
            if (result.CalculatedTemperature is double tK && double.IsFinite(tK) &&
                result.CalculatedPressure is double pPa && double.IsFinite(pPa) &&
                feed is DWSIM.Thermodynamics.Streams.MaterialStream feedMs)
            {
                feedMs.SetTemperature(tK);
                feedMs.SetPressure(pPa);
                feedMs.SetMassFlow(1.0);
                feedMs.PropertyPackage = (DWSIM.Thermodynamics.PropertyPackages.PropertyPackage)package;
                feedMs.Calculate(true, true);
                if (feedMs.Phases[0].Properties.density is double rho && double.IsFinite(rho) && rho > 0)
                    densityKgM3 = Math.Round(rho, 3);
            }
        }
        catch { densityKgM3 = null; }

        return new FlashResult(
            VaporFraction: Math.Round(vaporFraction, 6),
            TemperatureC:  RoundC(result.CalculatedTemperature),
            PressureBar:   RoundBar(result.CalculatedPressure),
            Phases: phases,
            EnthalpyKJKg:  enthalpyKJKg is double e ? Math.Round(e, 3) : null,
            EntropyKJKgK:  entropyKJKgK is double ek ? Math.Round(ek, 3) : null,
            DensityKgM3:   densityKgM3);

        static PhaseOut BuildPhase(string label, double moleFrac, IReadOnlyList<double> moleFracs, List<string> compounds)
        {
            var comp = new Dictionary<string, double>();
            for (var i = 0; i < Math.Min(compounds.Count, moleFracs.Count); i++)
                if (double.IsFinite(moleFracs[i]) && moleFracs[i] > 1e-9)
                    comp[compounds[i]] = Math.Round(moleFracs[i], 6);
            return new PhaseOut(label, Math.Round(moleFrac, 6), comp);
        }
        static double? RoundC(double? k) => k is double v && double.IsFinite(v) ? Math.Round(v - 273.15, 3) : null;
        // Six decimals, matching the solve harvest — the two must not disagree about one pressure.
        static double? RoundBar(double? pa) => pa is double v && double.IsFinite(v) ? Math.Round(v * 1e-5, 6) : null;
    }

    private static double RequireSi(FlowQuantity? q, string name, string siUnit)
    {
        if (q is null) throw new WorkerInputException("FLASH_INVALID", $"{name} spec is required for this flashType");
        return q.Unit is { Length: > 0 }
            ? DWSIM.SharedClasses.SystemsOfUnits.Converter.ConvertToSI(q.Unit, q.Value)
            : q.Value;
    }

    // ── pfd (T054) ──────────────────────────────────────────────────────────
    // Renders a PFD PNG from a document (POST /flowsheets/pfd) or a saved
    // template (GET /templates/{id}/pfd.png). T054 fully implements this; until
    // then we surface a clear RENDER_FAILED so the API returns 422 rather than
    // a phantom success.
    public static PfdResult Pfd(Job job)
    {
        if (job.Document is { ValueKind: JsonValueKind.Object } docEl)
        {
            // Build (no solve) to obtain the flowsheet, then render. If we
            // can't render, surface RENDER_FAILED with the build issues.
            try
            {
                var auto = new Automation3();
                var (fs, _, _) = FlowsheetBuilder.Build(auto, FlowsheetBuilder.ParseDocument(docEl));
                return RenderPfd(fs);
            }
            catch (BuildAbortException ex)
            {
                throw new RenderFailedException(
                    $"build failed with {ex.Issues.Count} issue(s): {string.Join("; ", ex.Issues.Take(3).Select(i => i.Message))}");
            }
        }

        // Template-based render — load, render. SavePath is unused here.
        if (job.Template is { Length: > 0 } template)
        {
            var auto = new Automation3();
            object? fsObj;
            try { fsObj = auto.LoadFlowsheet(template); }
            catch (Exception ex) { throw new TemplateLoadException($"failed to load '{Path.GetFileName(template)}': {ex.Message}"); }
            var fs = (fsObj as IFlowsheet) ?? throw new TemplateLoadException($"failed to load '{Path.GetFileName(template)}'");
            return RenderPfd(fs);
        }

        throw new WorkerInputException("INVALID_REQUEST", "pfd mode requires a document or a template");
    }

    private static PfdResult RenderPfd(IFlowsheet fs)
    {
        // Draw the flowsheet's headless SkiaSharp surface into an offscreen
        // bitmap. Needs libSkiaSharp (shipped with DWSIM, resolved by
        // DwsimResolver) and fontconfig on the image (Dockerfile).
        try
        {
            var surfaceObj = fs.GetType().GetMethod("GetSurface")?.Invoke(fs, null)
                ?? throw new RenderFailedException("PFD rendering not available in this engine build (no GetSurface)");
            if (surfaceObj is not DWSIM.Drawing.SkiaSharp.GraphicsSurface surface)
                throw new RenderFailedException($"unexpected drawing surface type '{surfaceObj.GetType().Name}'");

            const int width = 1600, height = 1000;
            if (fs.GraphicObjects.Count == 0)
                throw new RenderFailedException("flowsheet has no drawable objects");

            surface.ZoomAll(width, height);

            using var bitmap = new SkiaSharp.SKBitmap(width, height);
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            {
                canvas.Clear(SkiaSharp.SKColors.White);
                surface.UpdateCanvas(canvas);
            }
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90)
                ?? throw new RenderFailedException("PNG encoding produced no data");
            return new PfdResult(Convert.ToBase64String(png.ToArray()));
        }
        catch (RenderFailedException) { throw; }
        catch (Exception ex)
        {
            throw new RenderFailedException($"PFD rendering failed: {ex.Message}");
        }
    }

    // ── shared helpers ──────────────────────────────────────────────────────
    private static JsonElement RequireDocument(Job job)
    {
        if (job.Document is not { ValueKind: JsonValueKind.Object } doc)
            throw new WorkerInputException("INVALID_REQUEST", "document is required for this mode");
        return doc;
    }
}

// ── mode DTOs ──────────────────────────────────────────────────────────────

record CatalogResult(string EngineVersion, List<CompoundOut> Compounds,
    List<PropertyPackageOut> PropertyPackages, object UnitOpTypes,
    List<EngineInventoryEntry> EngineInventory);

/// One unit-op kind the engine declares. `ExposedAs` is null when this runner has no wire type for it
/// — which is the whole point of the record: an absent capability that says so (099 FR-004).
record EngineInventoryEntry(string Name, string DisplayName, string Source, bool Instantiable,
    string? ExposedAs);
record CompoundOut(string Name, string? Formula, string? CasNumber);
record PropertyPackageOut(string Id, string Name, string Description);

record ValidationOutcome(bool Valid, List<IssueOut> Issues);

record BuildReport(bool Converged, long ElapsedMs, List<StreamRow> Streams,
    List<EnergyRow> Energy, List<UnitOpRow> UnitOps, List<string> Warnings,
    BuildInfo Build, TemplateOut? Template);
record TemplateOut(string Id, string Source, bool SavedAtSave);

record FlashRequest(List<string> Compounds, FlowComposition Composition, string PropertyPackage,
    string FlashType, FlowQuantity? Temperature, FlowQuantity? Pressure,
    FlowQuantity? Enthalpy, FlowQuantity? Entropy,
    // 120 US2 — dimensionless molar vapor fraction spec for PVF/TVF.
    FlowQuantity? VaporFraction = null);

record FlashResult(double VaporFraction, double? TemperatureC, double? PressureBar,
    List<PhaseOut> Phases, double? EnthalpyKJKg, double? EntropyKJKgK,
    // Nullable and last: a density the engine would not give must not fail a converged flash.
    double? DensityKgM3 = null);
record PhaseOut(string Phase, double MolarFraction, Dictionary<string, double> Composition);

record PfdResult(string PngBase64);