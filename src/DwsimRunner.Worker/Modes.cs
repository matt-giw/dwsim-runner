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
    /// <summary>An energy stream's duty as the engine holds it (DWSIM SI energy flow is kW); not finite → null.</summary>
    internal static double? EnergyFlowKw(double? ef) => ef is double v && double.IsFinite(v) ? v : null;

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

        var (packageNames, _) = Engine.PropertyPackageNames(auto);
        var packages = packageNames
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name =>
            {
                var (id, description) = PackageCatalog.Classify(name);
                return new PropertyPackageOut(id, name, description);
            })
            .ToList();

        return new CatalogResult(engineVersion, compounds, packages, UnitOpCatalog.ToPayload(),
            EngineInventory(auto),
            // 281 — the allowlist this runner ENFORCES, served from the dictionary that enforces it.
            new SolidsOut(CompoundDefinitions.SolidsAllowlist.Keys.Order().ToList(),
                CompoundDefinitions.SolidValues.Select(v => new SolidValueOut(v.Key, v.Unit)).ToList()));
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

        // `ExposedAs` is `UnitOpCatalog.WireTypeFor` — the reverse of the allowlist, computed from it,
        // so it cannot lie. It is the same lookup a solve uses to name a unit op's type.
        var entries = Enum.GetNames<ObjectType>()
            .Select(name =>
            {
                var parsed = Enum.TryParse<ObjectType>(name, out var ot);
                return new EngineInventoryEntry(
                    Name: name,
                    DisplayName: Humanize(name),
                    Source: "enum",
                    Instantiable: parsed
                        ? CanBuild(ot)
                        // Unparseable is a contradiction (the name came FROM the enum), so fall back
                        // to the palette rather than silently reporting false.
                        : instantiable.Contains(Key(name)),
                    ExposedAs: parsed ? UnitOpCatalog.WireTypeFor(ot) : null);
            })
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

    internal static string ExtractVersion(Automation3 auto)
    {
        try
        {
            // auto.GetVersion() returns "DWSIM version 9.0.5.0 (...)".
            var raw = auto.GetVersion() ?? "";
            // The char overload: `raw.Split([' '], ...)` is ambiguous (CS0121) on the .NET 8 SDK CI
            // builds with, which is how the Worker came to build only on a newer SDK.
            var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

    // 168: engine error strings arrive with embedded newlines and stack-trace tails;
    // one warning is one line, capped so a pathological message cannot flood the report.
    private static string Squash(string msg)
    {
        var line = string.Join(" ", msg.Split('\n', '\r')
            .Select(l => l.Trim()).Where(l => l.Length > 0));
        return line.Length > 300 ? line[..300] + "…" : line;
    }

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
            engineWarnings.Add(Squash(fs.ErrorMessage));

        // 168: five corpus non-convergences (159/179/215/216/217) were hard zeros
        // reading "solve did not converge" and nothing else. The flowsheet-level
        // ErrorMessage above turned out to be populated all along — the HARNESS was
        // dropping it (its reader never touched the 200 body's warnings; fixed on the
        // evals side the same day). What it lacks is attribution, so this harvest
        // adds the PER-OBJECT half: BaseClass.ErrorMessage carries the throw's own
        // sentence on the unit that died, and GraphicObject.Calculated names every
        // unit stranded behind it. 162's move (the engine's own words), one level
        // down. Measured payoff, all five cases: "R101: The Element Matrix is not
        // defined", "RC-101: Recycle reached the maximum number of iterations",
        // "Seawater-Feed: Salt compound not found", plus the stranded-unit trail.
        if (!converged)
        {
            foreach (var obj in fs.SimulationObjects.Values)
            {
                string tag = obj.GraphicObject?.Tag ?? "?";
                string msg = (obj as DWSIM.SharedClasses.UnitOperations.BaseClass)?.ErrorMessage ?? "";
                if (!string.IsNullOrWhiteSpace(msg))
                    engineWarnings.Add($"{tag}: {Squash(msg)}");
                else if (obj.GraphicObject is { Calculated: false }
                         && obj is not DWSIM.Thermodynamics.Streams.MaterialStream
                         && obj is not DWSIM.UnitOperations.Streams.EnergyStream)
                    engineWarnings.Add($"{tag}: did not calculate (no engine message)");
            }
        }

        // ── harvest streams/energy/unitOps (reusing the spec-001 shape) ───────
        var (streams, energy, unitOps) = Harvest(fs);

        engineWarnings.AddRange(warnings.Select(w => $"[{w.Code}] {w.Message}"));

        (streams, energy, unitOps) = ApplySolidsRules(fs, streams, energy, unitOps, engineWarnings);

        // ── optional save (T037) ─────────────────────────────────────────────
        // Conflict/overwrite policy and listing metadata (sidecar, template
        // block) are owned by the API; the worker's only job is the engine
        // save. Persist even when unsolved (US3: solvedAtSave:false).
        // Spec 011 Cut 2: a save failure MUST NOT throw — the solve result
        // is the user's answer; the save is a best-effort side-effect. The
        // API infers save failure from the absence of the file and reports
        // a soft `template.saved:false` block rather than a 500.
        if (job.SavePath is { Length: > 0 } path)
            SafeSave.Run(path, p => auto.SaveFlowsheet2(fs, p));

        return new BuildReport(
            Converged: converged,
            ElapsedMs: sw.ElapsedMilliseconds,
            Streams: streams,
            Energy: energy,
            UnitOps: unitOps,
            Warnings: engineWarnings,
            Build: build,
            Template: null,
            PropertyUnits: PhaseProperties.UnitsForResponse());
    }

    /// <summary>
    /// ONE result harvest over a solved flowsheet, shared by both worker entry points (`solve` in
    /// Program.cs and `build-solve` here). The loop itself was the last copy left after the stream
    /// and unit-op harvests were shared: the energy row lived in it twice, and a field added to one
    /// copy would have appeared on one solve path and not the other (Hazard 7).
    /// </summary>
    internal static (List<StreamRow> Streams, List<EnergyRow> Energy, List<UnitOpRow> UnitOps) Harvest(IFlowsheet fs)
    {
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
                    // contain.
                    if (ElectrolyzerConfigurator.IsSynthesizedPower(es.GraphicObject.Tag)) break;
                    energy.Add(new EnergyRow(es.GraphicObject.Tag,
                        // DWSIM SI energy flow is already kW
                        EnergyFlowKw(es.EnergyFlow)));
                    break;
                default:  // equipment-level results for downstream sizing (FR-015)
                    unitOps.Add(HarvestUnitOp(obj));
                    break;
            }
        }
        return (streams, energy, unitOps);
    }

    /// <summary>
    /// 281 — everything a solved flowsheet's result must obey about solids, in ONE place for BOTH solve
    /// paths (document and template; review of #34 found the template path running without it):
    ///   1. a solid the flowsheet carries that came back in a fluid phase REFUSES the result, with the
    ///      engine's own warnings attached so a non-convergence is not hidden behind the refusal;
    ///   2. a liquid phase that is mostly an ordinary compound below its melting point is warned;
    ///   3. with any solid present, every energy result and every COMPUTED temperature is withheld
    ///      (T009: the solid's enthalpy follows the placeholders, and an enthalpy balance — a heater on
    ///      a duty, a mixer, a compressor — computes a temperature from it). A feed's temperature was
    ///      stated by the caller and stays; a feed is a material stream nothing feeds.
    /// </summary>
    internal static (List<StreamRow>, List<EnergyRow>, List<UnitOpRow>) ApplySolidsRules(
        IFlowsheet fs, List<StreamRow> streams, List<EnergyRow> energy, List<UnitOpRow> unitOps, List<string> warnings)
    {
        var solids = CompoundDefinitions.SolidsOf(fs);
        var solidIssues = streams.SelectMany(s => SolidsCheck.SolidInFluid(s.Name, PhasesOf(s), solids)).ToList();
        if (solidIssues.Count > 0)
            throw new BuildAbortException("SOLID_REPORTED_AS_FLUID",
                "the engine reported a defined solid in a fluid phase",
                [.. solidIssues, .. warnings.Select(w => new BuildIssue("warning", "ENGINE", null, null, w))]);

        var meltingPointK = CompoundDefinitions.MeltingPoints(fs);
        warnings.AddRange(streams.SelectMany(s => SolidsCheck.BelowMeltingPoint(s.Name, s.TemperatureC, PhasesOf(s), meltingPointK, solids)));
        if (solids.Count == 0) return (streams, energy, unitOps);

        // 4. Measured 2026-10-05 (iskra 281 US3): an equilibrium reactor fed a solid converges and
        //    sends the solid to its liquid outlet BY MASS — the balance closes — but that stream comes
        //    back with no composition and no phase. 4,100 kg/h of something. The mislabel guard above
        //    cannot see it (nothing is in a fluid phase; nothing is anywhere), so this one does: a
        //    stream that carries mass and says nothing about what it is, is refused by name.
        var unresolved = streams
            .Where(s => s.MassFlowKgH is > 1e-9 && (s.Phases is null || s.Phases.Count == 0) && (s.CompositionMol is null || s.CompositionMol.Count == 0))
            .Select(s => new BuildIssue("error", "SOLID_STREAM_UNRESOLVED", s.Name, null,
                $"{s.Name}: carries {s.MassFlowKgH:0.###} kg/h but the engine reported no phase and no composition for it; " +
                "a unit that received a defined solid left this outlet unresolved (measured on the equilibrium reactor), " +
                "so the result is refused rather than reported"))
            .ToList();
        if (unresolved.Count > 0)
            throw new BuildAbortException("SOLID_STREAM_UNRESOLVED", "a stream carrying a defined solid came back unresolved",
                [.. unresolved, .. warnings.Select(w => new BuildIssue("warning", "ENGINE", null, null, w))]);

        var feeds = new HashSet<string>(fs.SimulationObjects.Values
            .OfType<DWSIM.Thermodynamics.Streams.MaterialStream>()
            .Where(ms => ms.GraphicObject?.InputConnectors is not { Count: > 0 } ins || !ins[0].IsAttached)
            .Select(ms => ms.GraphicObject.Tag), StringComparer.Ordinal);
        warnings.Add(SolidsCheck.EnergyWarning);
        return (
            streams.Select(s => s with
            {
                TemperatureC = feeds.Contains(s.Name) ? s.TemperatureC : null,
                Properties = SolidsCheck.WithoutEnergy(s.Properties),
                Phases = s.Phases?.Select(p => p with { Properties = SolidsCheck.WithoutEnergy(p.Properties) }).ToList(),
            }).ToList(),
            energy.Select(e => e with { DutyKw = null }).ToList(),
            unitOps.Select(u => u with { PowerKw = null, DutyKw = null, OutletTemperatureC = null }).ToList());
    }

    private static IEnumerable<(string Phase, IReadOnlyDictionary<string, double>? Composition)> PhasesOf(StreamRow s) =>
        (s.Phases ?? []).Select(p => (p.Name, (IReadOnlyDictionary<string, double>?)p.Composition));

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
                ViscosityPaS: Round(p.Properties.viscosity, 0, 1, 8),
                // 259 — everything else this phase carries, in SI, unconverted and unrounded.
                // NOT rounded: the four named fields above each chose a decimal count for their own
                // magnitude, and there is exactly one correct number of decimals per quantity. A
                // single rounding rule across 60 quantities spanning 1e-5 (viscosity) to 1e5
                // (pressure) would destroy the small ones — which is the entropy bug, generalised.
                Properties: PhaseProperties.Harvest(p.Properties)));
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
            Phases:         blocks.Count > 0 ? blocks : null,
            // 259 — the BULK bag, from the Mixture aggregate. `Phases[0]` is read here for the same
            // reason the four named fields above read it: it IS the bulk. The block loop still skips
            // "Mixture" by name, so this does not reintroduce the 120 bug of treating slot 0 as a
            // real phase — it treats it as the aggregate it is.
            Properties:     PhaseProperties.Harvest(ms.Phases[0].Properties));

        static double? Round(double? si, double offset = 0, double scale = 1, int digits = 3) =>
            si is double v && double.IsFinite(v) ? Math.Round(v * scale + offset, digits) : null;
    }

    /// <summary>ONE unit-op harvest, shared with the `solve` entry point (Program.cs), which
    /// used to carry a byte-for-byte copy — the duplication 099 recorded as debt, and the reason
    /// a reported field could depend on which entry point ran (Hazard 7). Two callers, one
    /// definition; 143 needed to add a field and fixing the fork was smaller than adding it twice.</summary>
    internal static UnitOpRow HarvestUnitOp(ISimulationObject obj)
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
        static string? Str(object o, string prop)
        {
            try { return o.GetType().GetProperty(prop)?.GetValue(o) as string; }
            catch { return null; }
        }

        var type = Engine.FriendlyType(obj);
        var deltaQ = Num(obj, "DeltaQ") ?? Num(obj, "Q");
        var isDriver = type is "compressor" or "pump" or "expander";
        return new UnitOpRow(
            Name: obj.GraphicObject.Tag,
            Type: type,
            PowerKw: isDriver ? RoundN(deltaQ, 1) : null,
            DutyKw: isDriver ? null : RoundN(deltaQ, 1),
            OutletTemperatureC: RoundN(Num(obj, "TOut") is double to ? to - 273.15 : null, 3),
            OutletPressureBar: RoundN(Num(obj, "POut") is double po ? po * 1e-5 : null, 3),
            // 143 FR-003 — READ BACK off the engine object, never echoed from the request. A
            // setting that is accepted and ignored looks identical to one that took effect, and
            // that ambiguity is exactly what 141 removed on the binder. Null for everything that
            // is not a column, which is how the app can tell "no solver here" from "the default".
            SolvingMethod: Str(obj, "SolvingMethodName"),
            MaxIterations: Num(obj, "MaxIterations") is double mi ? (int)mi : null);
    }

    // ── flash (T044) ────────────────────────────────────────────────────────
    // T-P / P-H / P-S flash without a flowsheet. The engine's
    // IPropertyPackage.CalculateEquilibrium2(FlashCalculationType, spec1, spec2, amount)
    // runs the flash directly on a composition vector — no flowsheet objects
    // needed. Request validation per FR-VAL (compounds non-empty, fractions
    // normalize, flashType/spec pair); FLASH_INVALID covers all pre-engine
    // failures so the API surfaces a single taxonomy code.
    // iskra spec 227 (ISK-266): returns a FlashResult, or a FlashBatchResult when the request
    // carries `states` — the compounds, package and scratch feed are built ONCE and every state is
    // evaluated against them. A state the engine refuses is an error ENTRY in its slot, never a
    // failed batch: a sweep with one non-converging point still answers the other nine.
    public static object Flash(Job job)
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
        if (flash.FlashType is null) throw new WorkerInputException("FLASH_INVALID", "flashType is required (TP|PH|PS|PVF|TVF)");
        var pp = (flash.PropertyPackage ?? "").Trim();
        if (pp.Length == 0) throw new WorkerInputException("FLASH_INVALID", "propertyPackage is required");

        var auto = new Automation3();

        // 281 — definitions first: the name resolution below, and any flowsheet created from here
        // on, must see them. Flash has no issue list, so the first problem refuses the request.
        var definitionIssues = new List<BuildIssue>();
        var definitions = CompoundDefinitions.Parse(flash.CompoundDefinitions, flash.Compounds, definitionIssues);
        CompoundDefinitions.Register(auto, definitions, definitionIssues);
        if (definitionIssues.FirstOrDefault() is { } bad)
            throw new WorkerInputException(bad.Code, bad.Message);
        var solids = CompoundDefinitions.SolidNames(definitions);

        // Headless engines often report an empty/null-named
        // AvailablePropertyPackages — fall back to the flowsheet-level listing
        // (same workaround as catalog mode).
        var (engineNames, flowsheetForNames) = Engine.PropertyPackageNames(auto);
        var ppName = PackageCatalog.Resolve(pp, engineNames)
            ?? throw new WorkerInputException("FLASH_INVALID", Engine.UnknownPackageMessage(pp, engineNames));
        if (CompoundDefinitions.SolidsRefusal(solids, ppName) is { } solidsRefusal)
            throw new WorkerInputException("SOLIDS_UNSUPPORTED_PACKAGE", solidsRefusal);
        // A PH or PS flash SOLVES FOR the quantity the placeholders corrupt. Refused, not answered.
        if (solids.Count > 0 && flash.FlashType.ToUpperInvariant() is "PH" or "PS")
            throw new WorkerInputException("FLASH_INVALID",
                $"flashType '{flash.FlashType}' is not available with a defined solid: " + SolidsCheck.EnergyWarning);

        // Resolve the engine compound names (case-insensitive) and build the
        // composition vector in the engine's compound order. Unknown compound
        // → FLASH_INVALID with suggestions, matching FlowsheetBuilder.
        var available = auto.AvailableCompounds.Keys.ToList();
        var resolvedCompounds = new List<string>();
        var compositionVector = new List<double>();
        foreach (var requested in flash.Compounds)
        {
            var match = Engine.ResolveCompound(available, requested, out var notFound)
                ?? throw new WorkerInputException("FLASH_INVALID", notFound);
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
        CompoundDefinitions.ApplySolidsSetting(fs, solids, ppName);
        var package = fs.PropertyPackages.Values.First();
        var solidsContext = new SolidsContext(solids, CompoundDefinitions.MeltingPoints(fs));

        // CalculateEquilibrium2 pulls the feed composition from the package's
        // CurrentMaterialStream (RET_VMOL) — a bare package NREs. Feed it a
        // scratch stream carrying the requested overall composition.
        var massBasis = string.Equals(flash.Composition.Basis, "mass", StringComparison.OrdinalIgnoreCase);
        IMaterialStream NewFeed(string tag)
        {
            var f = (IMaterialStream)fs.AddObject(
                DWSIM.Interfaces.Enums.GraphicObjects.ObjectType.MaterialStream, 50, 50, tag);
            if (massBasis) f.SetOverallMassComposition([.. compositionVector]);
            else f.SetOverallMolarComposition([.. compositionVector]);
            package.CurrentMaterialStream = f;
            return f;
        }
        var feed = NewFeed("FLASH-FEED");

        if (flash.States is { Count: > 0 } states)
        {
            // A FRESH scratch feed per state. Measured 2026-09-06 on the first batch build: every
            // state after the first died with a NullReferenceException inside CalculateEquilibrium2.
            // The density harvest calculates the feed at the found state (`feedMs.Calculate`), and a
            // feed the engine has calculated is no longer the bare composition carrier the package's
            // CurrentMaterialStream must be for RET_VMOL. A new stream is cheap; the process is not.
            var results = new List<object>(states.Count);
            var i = 0;
            foreach (var st in states)
            {
                if (i > 0) feed = NewFeed($"FLASH-FEED-{i}");
                i++;
                var merged = flash with
                {
                    Temperature = st.Temperature ?? flash.Temperature,
                    Pressure = st.Pressure ?? flash.Pressure,
                    Enthalpy = st.Enthalpy ?? flash.Enthalpy,
                    Entropy = st.Entropy ?? flash.Entropy,
                    VaporFraction = st.VaporFraction ?? flash.VaporFraction,
                };
                try { results.Add(FlashOne(merged, package, feed, resolvedCompounds, solidsContext)); }
                catch (WorkerInputException ex) { results.Add(new ErrorDoc(ex.Code, ex.Message, ex.Detail)); }
            }
            return new FlashBatchResult(results);
        }
        return FlashOne(flash, package, feed, resolvedCompounds, solidsContext);
    }

    /// <summary>281 — what FlashOne needs to judge a result: the defined solids and every compound's melting point.</summary>
    private sealed record SolidsContext(IReadOnlySet<string> Solids, IReadOnlyDictionary<string, double> MeltingPointK);

    /// <summary>One state against an already-built package and feed. The body of the pre-227 method, verbatim.</summary>
    private static FlashResult FlashOne(FlashRequest flash, IPropertyPackage package, IMaterialStream feed,
        List<string> resolvedCompounds, SolidsContext solidsContext)
    {
        // Map flashType → FlashCalculationType + the two spec values in SI.
        DWSIM.Interfaces.Enums.FlashCalculationType calcType;
        double spec1, spec2;
        switch (flash.FlashType.ToUpperInvariant())
        {
            case "TP":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.PressureTemperature;
                spec1 = RequireSi(flash.Pressure, "pressure");
                spec2 = RequireSi(flash.Temperature, "temperature");
                break;
            case "PH":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.PressureEnthalpy;
                spec1 = RequireSi(flash.Pressure, "pressure");
                spec2 = RequireSi(flash.Enthalpy, "enthalpy");
                break;
            case "PS":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.PressureEntropy;
                spec1 = RequireSi(flash.Pressure, "pressure");
                spec2 = RequireSi(flash.Entropy, "entropy");
                break;
            // 120 US2 — the remaining measurable pairs, by MEASUREMENT (2026-08-01, 9.0.5.0):
            // - PVF works and is exposed below. TVF is exposed for mixtures only: on a single
            //   compound it ignores vaporFraction (returns saturated liquid for any value), so the
            //   API's FlashPrecheck refuses it there.
            // - TH/TS (TemperatureEnthalpy/TemperatureEntropy) CRASH the engine — hard worker
            //   death, not an exception — under both STEAM and PR. Deliberately NOT exposed;
            //   the capability fixture records the crash verdict. Re-measure before re-adding.
            // - PSF/TSF (solid fraction): solids are ledgered will-not-yet (no flash-algorithm
            //   selection), so a solid-fraction flash would be a knob wired to nothing.
            case "PVF":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.PressureVaporFraction;
                spec1 = RequireSi(flash.Pressure, "pressure");
                spec2 = RequireSi(flash.VaporFraction, "vaporFraction");
                break;
            case "TVF":
                calcType = DWSIM.Interfaces.Enums.FlashCalculationType.TemperatureVaporFraction;
                spec1 = RequireSi(flash.Temperature, "temperature");
                spec2 = RequireSi(flash.VaporFraction, "vaporFraction");
                break;
            default:
                throw new WorkerInputException("FLASH_INVALID", $"flashType '{flash.FlashType}' not supported (TP|PH|PS|PVF|TVF)");
        }

        // The 4th argument is the initial TEMPERATURE estimate (Tref), verbatim: CalculateEquilibrium2
        // forwards it into Flash_PH(Vz, P, H, Tref, ...) / Flash_PS(...), read from the IL of the
        // vendored DWSIM.Thermodynamics.dll (spec 162, 2026-08-11). Flash_PH_1 opens with
        // `If Tref = 0 Then Tref = 298.15` — zero means "no estimate, use the engine default".
        // This used to pass 1.0, which DEFEATS that default: a legal-looking 1-kelvin start, from
        // which every PH/PS flash on a mixture (≥2 nonzero fractions) died in the inner Flash_PT
        // ("T = 0.00 K"). Pure compounds took a different branch and survived, which is why four
        // specs' pure-compound probes never saw it. Pass 0.0; never invent a temperature here.
        IFlashCalculationResult result;
        try
        {
            result = package.CalculateEquilibrium2(calcType, spec1, spec2, 0.0);
        }
        catch (Exception ex)
        {
            // CalculateEquilibrium2 THROWS on non-convergence (AggregateException wrapping the
            // flash algorithm's own message); ResultException below covers only errors the engine
            // RETURNS. Without this catch the worker process died (exit 1) and the caller got
            // "WORKER_CRASH — simulation worker failed unexpectedly": a message with no
            // information, retried by the co-pilot 37 times in the 2026-08-11 corpus. The engine's
            // own sentence ("PT Flash: Error calculating amount of the vapor phase...") is the
            // legible refusal; surface it.
            var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
            throw new WorkerInputException("FLASH_INVALID",
                $"flash calculation failed: {inner.Message}");
        }
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

        // 281 — the same two rules the solve harvest applies, on the same shape.
        var judged = phases.Select(p => (p.Phase, (IReadOnlyDictionary<string, double>?)p.Composition)).ToList();
        if (SolidsCheck.SolidInFluid(null, judged, solidsContext.Solids).FirstOrDefault() is { } mislabelled)
            throw new WorkerInputException("SOLID_REPORTED_AS_FLUID", mislabelled.Message);
        var flashWarnings = SolidsCheck.BelowMeltingPoint(null,
            result.CalculatedTemperature is double tKelvin && double.IsFinite(tKelvin) ? tKelvin - 273.15 : null,
            judged, solidsContext.MeltingPointK, solidsContext.Solids);
        var withholdEnergy = solidsContext.Solids.Count > 0;
        if (withholdEnergy) flashWarnings.Add(SolidsCheck.EnergyWarning);

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
        double? enthalpyKJKg = !withholdEnergy && result.CalculatedEnthalpy is double h && double.IsFinite(h) ? h : null;
        double? entropyKJKgK = !withholdEnergy && result.CalculatedEntropy is double se && double.IsFinite(se) ? se : null;

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
            DensityKgM3:   densityKgM3,
            Warnings:      flashWarnings.Count > 0 ? flashWarnings : null);

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

    // A required flash spec in the engine's SI (Pa, K, kJ/kg, kJ/kg.K, dimensionless). The unit's
    // DIMENSION is checked by the API before the worker runs — see Engine.ToSi.
    private static double RequireSi(FlowQuantity? q, string name) =>
        q is null
            ? throw new WorkerInputException("FLASH_INVALID", $"{name} spec is required for this flashType")
            : Engine.ToSi(q);

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
            return RenderPfd(Engine.LoadTemplate(new Automation3(), template));
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
    List<EngineInventoryEntry> EngineInventory,
    SolidsOut Solids);

/// 281 — the packages a defined solid may be solved under, and the values a solid definition must
/// carry with the one unit each is accepted in. Both read off the tables that do the enforcing.
record SolidsOut(List<string> Packages, List<SolidValueOut> Values);
record SolidValueOut(string Key, string Unit);

/// One unit-op kind the engine declares. `ExposedAs` is null when this runner has no wire type for it
/// — which is the whole point of the record: an absent capability that says so (099 FR-004).
record EngineInventoryEntry(string Name, string DisplayName, string Source, bool Instantiable,
    string? ExposedAs);
record CompoundOut(string Name, string? Formula, string? CasNumber);
record PropertyPackageOut(string Id, string Name, string Description);

record ValidationOutcome(bool Valid, List<IssueOut> Issues);

record BuildReport(bool Converged, long ElapsedMs, List<StreamRow> Streams,
    List<EnergyRow> Energy, List<UnitOpRow> UnitOps, List<string> Warnings,
    BuildInfo Build, TemplateOut? Template,
    // 259 FR-003 — as on SolveResult: the SI unit of every `properties` key, once per response.
    Dictionary<string, string>? PropertyUnits = null);
record TemplateOut(string Id, string Source, bool SavedAtSave);

record FlashRequest(List<string> Compounds, FlowComposition Composition, string PropertyPackage,
    string FlashType, FlowQuantity? Temperature, FlowQuantity? Pressure,
    FlowQuantity? Enthalpy, FlowQuantity? Entropy,
    // 120 US2 — dimensionless molar vapor fraction spec for PVF/TVF.
    FlowQuantity? VaporFraction = null,
    // iskra spec 227 — a batch: N spec sets against this base; a state's spec overrides the base's.
    List<FlashState>? States = null,
    // 281 — compounds the engine does not ship, defined on the request (see CompoundDefinitions).
    JsonElement? CompoundDefinitions = null);
record FlashState(FlowQuantity? Temperature, FlowQuantity? Pressure, FlowQuantity? Enthalpy,
    FlowQuantity? Entropy, FlowQuantity? VaporFraction);
/// <summary>One entry per state, in order: a FlashResult, or an ErrorDoc for a state the engine refused.</summary>
record FlashBatchResult(List<object> Results);

record FlashResult(double VaporFraction, double? TemperatureC, double? PressureBar,
    List<PhaseOut> Phases, double? EnthalpyKJKg, double? EntropyKJKgK,
    // Nullable and last: a density the engine would not give must not fail a converged flash.
    double? DensityKgM3 = null,
    // 281 — absent unless there is something to say.
    List<string>? Warnings = null);
record PhaseOut(string Phase, double MolarFraction, Dictionary<string, double> Composition);

record PfdResult(string PngBase64);