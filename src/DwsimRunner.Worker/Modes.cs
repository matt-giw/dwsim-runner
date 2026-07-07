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
    // Does NOT require CreateFlowsheet() — Automation3.AvailableCompounds and
    // Automation3.AvailablePropertyPackages are populated at construction. This
    // means catalog works on a fresh install with no SkiaSharp/native surface
    // present, which is what the SaaS runner needs for fast startup.
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

        var packages = ((System.Collections.IEnumerable)auto.AvailablePropertyPackages.Values)
            .Cast<IPropertyPackage>()
            .Select(pp => pp.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name =>
            {
                var (id, description) = PackageCatalog.Classify(name);
                return new PropertyPackageOut(id, name, description);
            })
            .ToList();

        return new CatalogResult(engineVersion, compounds, packages, UnitOpCatalog.ToPayload());
    }

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
        TemplateOut? templateOut = null;
        if (job.SaveAsTemplate is { } save && job.SavePath is { Length: > 0 } path)
        {
            if (File.Exists(path) && !save.Overwrite)
                throw new WorkerInputException("TEMPLATE_NAME_CONFLICT",
                    $"a template named '{save.Id}' already exists; pass overwrite:true to replace it");
            try
            {
                auto.SaveFlowsheet2(fs, path);
                templateOut = new TemplateOut(save.Id, "user", SavedAtSave: true);
            }
            catch (Exception ex)
            {
                throw new WorkerInputException("BUILD_FAILED",
                    $"flowsheet built and solved but could not be saved to '{path}': {ex.Message}");
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
            Template: templateOut);
    }

    private static StreamRow HarvestStream(DWSIM.Thermodynamics.Streams.MaterialStream ms)
    {
        var comp = new Dictionary<string, double>();
        foreach (var c in ms.Phases[0].Compounds.Values)
            if (c.MoleFraction is double mf && double.IsFinite(mf) && mf > 1e-9)
                comp[c.Name] = Math.Round(mf, 6);
        return new StreamRow(
            Name:           ms.GraphicObject.Tag,
            Phase:          ms.Phases[0].Properties.molarfraction == 1 ? "vapor" : null,
            TemperatureC:   Round(ms.Phases[0].Properties.temperature, -273.15),
            PressureBar:    Round(ms.Phases[0].Properties.pressure, 0, 1e-5),
            MassFlowKgH:    Round(ms.Phases[0].Properties.massflow, 0, 3600),
            MolarFlowKmolH: Round(ms.Phases[0].Properties.molarflow, 0, 3.6),
            CompositionMol: comp);

        static double? Round(double? si, double offset = 0, double scale = 1) =>
            si is double v && double.IsFinite(v) ? Math.Round(v * scale + offset, 3) : null;
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
        var engineNames = ((System.Collections.IEnumerable)auto.AvailablePropertyPackages.Values)
            .Cast<IPropertyPackage>().Select(p => p.Name).ToList();
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
        var fs = auto.CreateFlowsheet()
            ?? throw new WorkerInputException("WORKER_CRASH", "engine failed to create a flowsheet");
        foreach (var c in resolvedCompounds) fs.AddCompound(c);
        fs.CreateAndAddPropertyPackage(ppName);
        var package = fs.PropertyPackages.Values.First();

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
            default:
                throw new WorkerInputException("FLASH_INVALID", $"flashType '{flash.FlashType}' not supported (TP|PH|PS)");
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
        double? enthalpyKJKg = result.CalculatedEnthalpy is double h && double.IsFinite(h) ? h / 1000.0 : null;
        double? entropyKJKgK = result.CalculatedEntropy is double se && double.IsFinite(se) ? se / 1000.0 : null;

        return new FlashResult(
            VaporFraction: Math.Round(vaporFraction, 6),
            TemperatureC:  RoundC(result.CalculatedTemperature),
            PressureBar:   RoundBar(result.CalculatedPressure),
            Phases: phases,
            EnthalpyKJKg:  enthalpyKJKg is double e ? Math.Round(e, 3) : null,
            EntropyKJKgK:  entropyKJKgK is double ek ? Math.Round(ek, 3) : null);

        static PhaseOut BuildPhase(string label, double moleFrac, IReadOnlyList<double> moleFracs, List<string> compounds)
        {
            var comp = new Dictionary<string, double>();
            for (var i = 0; i < Math.Min(compounds.Count, moleFracs.Count); i++)
                if (double.IsFinite(moleFracs[i]) && moleFracs[i] > 1e-9)
                    comp[compounds[i]] = Math.Round(moleFracs[i], 6);
            return new PhaseOut(label, Math.Round(moleFrac, 6), comp);
        }
        static double? RoundC(double? k) => k is double v && double.IsFinite(v) ? Math.Round(v - 273.15, 3) : null;
        static double? RoundBar(double? pa) => pa is double v && double.IsFinite(v) ? Math.Round(v * 1e-5, 3) : null;
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
        // The headless SkiaSharp surface can render to a PNG stream. T054
        // wires this up; until the native libs are confirmed on the build
        // image, we surface a structured failure.
        try
        {
            var surfaceField = fs.GetType().GetMethod("GetSurface");
            if (surfaceField is null)
                throw new RenderFailedException("PFD rendering not available in this engine build (no GetSurface)");
            // TODO T054: surface.WriteToPNG(stream); base64.
            throw new RenderFailedException("PFD rendering not yet implemented (T054)");
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
    List<PropertyPackageOut> PropertyPackages, object UnitOpTypes);
record CompoundOut(string Name, string? Formula, string? CasNumber);
record PropertyPackageOut(string Id, string Name, string Description);

record ValidationOutcome(bool Valid, List<IssueOut> Issues);

record BuildReport(bool Converged, long ElapsedMs, List<StreamRow> Streams,
    List<EnergyRow> Energy, List<UnitOpRow> UnitOps, List<string> Warnings,
    BuildInfo Build, TemplateOut? Template);
record TemplateOut(string Id, string Source, bool SavedAtSave);

record FlashRequest(List<string> Compounds, FlowComposition Composition, string PropertyPackage,
    string FlashType, FlowQuantity? Temperature, FlowQuantity? Pressure,
    FlowQuantity? Enthalpy, FlowQuantity? Entropy);

record FlashResult(double VaporFraction, double? TemperatureC, double? PressureBar,
    List<PhaseOut> Phases, double? EnthalpyKJKg, double? EntropyKJKgK);
record PhaseOut(string Phase, double MolarFraction, Dictionary<string, double> Composition);

record PfdResult(string PngBase64);