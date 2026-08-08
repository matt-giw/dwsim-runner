// dwsim-runner Worker — GPL-3.0
// One job per process. argv[0] = path to a job JSON file. The mode field
// selects a handler; each prints exactly one JSON document to stdout and
// exits with a typed code (data-model.md error taxonomy):
//   0 = success (converged flag is in the payload for solve/build-solve)
//   2 = invalid input  → { error, message, detail? }
//   3 = template load failed
//   4 = build failed / unknown compound (issues attached)
//   5 = render failed (pfd)
//   1 = unexpected crash (full detail on stderr only)
// The API process owns timeouts and exit-code → HTTP mapping.

using System.Text.Json;
using System.Text.Json.Serialization;
using DwsimRunner.Worker;

// MUST run before any native library can print to fd 1. Console.SetOut below
// only moves the MANAGED writer; Ipopt writes its banner to the descriptor and
// corrupted the protocol for every solve that loaded it. See ProtocolChannel.
ProtocolChannel.Divert();

DwsimResolver.Install();   // MUST run before any DWSIM-typed code is JIT'd

var job = JsonSerializer.Deserialize<Job>(
    File.ReadAllText(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

// The contract with the API is "exactly one JSON document on stdout" — but
// DWSIM writes solver progress to the console. Redirect stdout to stderr for
// the duration of all DWSIM work, then write the result as the final act.
var realOut = Console.Out;
Console.SetOut(Console.Error);

object payload;
int exitCode = 0;
try
{
    payload = (job.Mode?.ToLowerInvariant()) switch
    {
        "inspect"      => Solver.Inspect(job),
        "catalog"      => Modes.Catalog(job),
        "validate"     => Modes.Validate(job),
        "build-solve"  => Modes.BuildSolve(job),
        "flash"        => Modes.Flash(job),
        "pfd"          => Modes.Pfd(job),
        _              => Solver.Run(job),   // "solve" (default for spec-001 back-compat)
    };
}
catch (WorkerInputException ex)
{
    exitCode = 2;
    payload = new ErrorDoc(ex.Code, ex.Message, ex.Detail);
}
catch (TemplateLoadException ex)
{
    exitCode = 3;
    payload = new ErrorDoc("TEMPLATE_LOAD_FAILED", ex.Message, null);
}
catch (BuildAbortException ex)
{
    exitCode = 4;
    payload = new BuildErrorDoc(
        ex.Code, ex.Message, ex.Issues.Select(i => new IssueOut(i.Severity, i.Code, i.Tag, i.Path, i.Message)).ToList());
}
catch (RenderFailedException ex)
{
    exitCode = 5;
    payload = new ErrorDoc("RENDER_FAILED", ex.Message, null);
}
catch (Exception ex)
{
    exitCode = 1;
    Console.Error.WriteLine(ex);   // stack trace stays server-side
    payload = new ErrorDoc("WORKER_CRASH", ex.Message, null);
}
finally
{
    Console.SetOut(realOut);
}

ProtocolChannel.WriteResult(JsonSerializer.Serialize(payload, payload.GetType(), new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
}));
return exitCode;

// ── job DTO ─────────────────────────────────────────────────────────────────
// Extended for 002 modes: documents, flash specs, template save targets.
// The 001 fields (template, overrides, mode) stay first for back-compat with
// the existing /solve and /inspect API code paths.
record Job(
    string? Template,
    List<Override>? Overrides,
    string? Mode,
    JsonElement? Document,
    JsonElement? Flash,
    SaveAsTemplate? SaveAsTemplate,
    string? SavePath);

record Override(string Object, string Property, double Value, string? Unit);
record SaveAsTemplate(string Id, bool Overwrite);

record ErrorDoc(string Error, string Message, string? Detail);
record BuildErrorDoc(string Error, string Message, List<IssueOut> Issues);
record IssueOut(string Severity, string Code, string? Tag, string? Path, string Message);

// DensityKgM3 last, so every named-argument construction is unaffected.
//
// Harvested because iskra's eval tier could not verify a density it asks the co-pilot to report:
// nothing on the wire carried one, so the agent answered ~997 kg/m3 for water at 25 C FROM MEMORY
// and the check could only be recorded as unverifiable. DWSIM knows it; we simply never picked it up.
record StreamRow(string Name, string? Phase, double? TemperatureC, double? PressureBar,
                 double? MassFlowKgH, double? MolarFlowKmolH, Dictionary<string, double>? CompositionMol,
                 double? DensityKgM3 = null,
                 // Mass basis, because a separation is stated that way. Nullable and last: absence is
                 // "the engine did not report it", never an implied zero.
                 Dictionary<string, double>? CompositionMass = null,
                 // 120 US1: molar vapor fraction and one block per phase actually present.
                 // Phase (above) is now DERIVED from these, never from Phases[0] — the Mixture
                 // phase's molarfraction is 1.0 by definition, which made the old label noise.
                 double? VaporFraction = null,
                 List<StreamPhaseBlock>? Phases = null);

// Physics-named phase blocks ("vapor"/"liquid"/"liquid2"/"solid") — never engine slot indexes.
record StreamPhaseBlock(string Name, double MoleFraction,
                        Dictionary<string, double>? Composition = null,
                        double? DensityKgM3 = null,
                        double? MolecularWeight = null,
                        double? HeatCapacityKJKgK = null,
                        double? ViscosityPaS = null);
record EnergyRow(string Name, double? DutyKw);
record UnitOpRow(string Name, string Type, double? PowerKw, double? DutyKw,
                 double? OutletTemperatureC, double? OutletPressureBar);
record SolveResult(bool Converged, long ElapsedMs, List<StreamRow> Streams,
                   List<EnergyRow> Energy, List<UnitOpRow> UnitOps, List<string> Warnings);

record ObjectInfo(string Tag, string Type, List<string> SettableProperties);
record InventoryResult(List<ObjectInfo> Objects);

class WorkerInputException(string code, string message, string? detail = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}

class TemplateLoadException(string message) : Exception(message);
class RenderFailedException(string message) : Exception(message);

static class Solver
{
    private static (DWSIM.Automation.Automation3 Auto, DWSIM.Interfaces.IFlowsheet Fs) Load(string template)
    {
        var auto = new DWSIM.Automation.Automation3();
        object? fsObj;
        try { fsObj = auto.LoadFlowsheet(template); }
        catch (Exception ex) { throw new TemplateLoadException($"failed to load '{Path.GetFileName(template)}': {ex.Message}"); }
        var fs = (fsObj as DWSIM.Interfaces.IFlowsheet)
                 ?? throw new TemplateLoadException($"failed to load '{Path.GetFileName(template)}'");
        return (auto, fs);
    }

    // Stable, engine-agnostic type names crossing the HTTP boundary (FR-014).
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

    private static readonly List<string> StreamProperties =
        ["massflow", "temperature", "pressure", "molarflow"];

    /// <summary>Flowsheet load without solving: object inventory (FR-014).</summary>
    public static InventoryResult Inspect(Job job)
    {
        var (_, fs) = Load(job.Template ?? throw new WorkerInputException("INVALID_REQUEST", "template is required for inspect mode"));
        var objects = fs.SimulationObjects.Values
            .Select(o => new ObjectInfo(
                Tag: o.GraphicObject.Tag,
                Type: FriendlyType(o),
                SettableProperties: o is DWSIM.Thermodynamics.Streams.MaterialStream
                    ? StreamProperties : []))
            .OrderBy(o => o.Tag, StringComparer.Ordinal)
            .ToList();
        return new InventoryResult(objects);
    }

    // ONE override application, shared by the template path (Run) and — since 120 US5 —
    // the document path (Modes.BuildSolve, for /compare and /optimize document cases).
    // Extracted for the same reason the harvest was: two callers drifting is how a property
    // gets settable on one solve path and not the other.
    internal static void ApplyOverrides(DWSIM.Interfaces.IFlowsheet fs, List<Override>? overrides)
    {
        string AvailableTags() =>
            "available: " + string.Join(", ",
                fs.SimulationObjects.Values.Select(o => o.GraphicObject.Tag).OrderBy(x => x));

        foreach (var ov in overrides ?? [])
        {
            var obj = fs.GetFlowsheetSimulationObject(ov.Object)
                      ?? throw new WorkerInputException("INVALID_OBJECT",
                             $"no object named '{ov.Object}'", AvailableTags());

            if (obj is DWSIM.Thermodynamics.Streams.MaterialStream ms)
            {
                var v = ov.Unit is null ? ov.Value.ToString() : $"{ov.Value} {ov.Unit}";
                switch (ov.Property.ToLowerInvariant())
                {
                    case "massflow":    ms.SetMassFlow(v); break;
                    case "temperature": ms.SetTemperature(v); break;
                    case "pressure":    ms.SetPressure(v); break;
                    case "molarflow":   ms.SetMolarFlow(v); break;
                    default:
                        throw new WorkerInputException("INVALID_PROPERTY",
                            $"unsupported stream property '{ov.Property}' on '{ov.Object}'",
                            "supported: massflow, temperature, pressure, molarflow");
                }
            }
            else
            {
                // Generic path for unit ops: DWSIM property-value interface.
                try { obj.SetPropertyValue(ov.Property, ov.Value); }
                catch (Exception ex)
                {
                    throw new WorkerInputException("INVALID_PROPERTY",
                        $"cannot set '{ov.Property}' on '{ov.Object}': {ex.Message}");
                }
            }
        }
    }

    // Reflection read of a numeric engine property (e.g. DeltaQ) — unit-op
    // classes vary; a missing/non-finite value is null, never an error.
    private static double? Num(object obj, string property)
    {
        try
        {
            var value = obj.GetType().GetProperty(property)?.GetValue(obj);
            if (value is null) return null;
            var d = Convert.ToDouble(value);
            return double.IsFinite(d) ? d : null;
        }
        catch { return null; }
    }

    public static SolveResult Run(Job job)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();

        var (auto, fs) = Load(job.Template ?? throw new WorkerInputException("INVALID_REQUEST", "template is required for solve mode"));

        ApplyOverrides(fs, job.Overrides);

        // ── solve ──────────────────────────────────────────────────────────
        auto.CalculateFlowsheet2(fs);
        bool converged = fs.Solved;
        if (!converged && !string.IsNullOrEmpty(fs.ErrorMessage))
            warnings.Add(fs.ErrorMessage);

        // ── harvest results ────────────────────────────────────────────────
        var streams = new List<StreamRow>();
        var energy  = new List<EnergyRow>();
        var unitOps = new List<UnitOpRow>();

        foreach (var obj in fs.SimulationObjects.Values)
        {
            switch (obj)
            {
                case DWSIM.Thermodynamics.Streams.MaterialStream ms:
                {
                    // ONE harvest, shared with build-solve (Modes.HarvestStream). This was a
                    // byte-for-byte copy of it, which is how a field gets added to one solve path
                    // and not the other — the same duplication-drift this repo has been bitten by
                    // before. Two callers, one definition.
                    streams.Add(Modes.HarvestStream(ms));
                    break;
                }
                case DWSIM.UnitOperations.Streams.EnergyStream es:
                    // 099 US1 — a synthesized electrolyzer power stream is not in the DOCUMENT, so
                    // reporting it would have the app fold back a stream its own side does not
                    // contain. Hidden in BOTH harvests: the worker has two entry points, and fixing
                    // one makes the answer depend on which one ran (Hazard 7).
                    if (ElectrolyzerConfigurator.IsSynthesizedPower(es.GraphicObject.Tag)) break;
                    energy.Add(new EnergyRow(es.GraphicObject.Tag,
                        es.EnergyFlow is double ef && double.IsFinite(ef)
                            ? Math.Round(ef, 1) : null)); // DWSIM SI energy flow is already kW
                    break;

                default:  // equipment-level results for downstream sizing (FR-015)
                {
                    var type = FriendlyType(obj);
                    var deltaQ = Num(obj, "DeltaQ") ?? Num(obj, "Q");   // DWSIM SI: kW
                    var isDriver = type is "compressor" or "pump" or "expander";
                    unitOps.Add(new UnitOpRow(
                        Name:    obj.GraphicObject.Tag,
                        Type:    type,
                        PowerKw: isDriver ? RoundN(deltaQ, 1) : null,
                        DutyKw:  isDriver ? null : RoundN(deltaQ, 1),
                        OutletTemperatureC: RoundN(Num(obj, "TOut") is double to ? to - 273.15 : null, 3),
                        OutletPressureBar:  RoundN(Num(obj, "POut") is double po ? po * 1e-5 : null, 3)));
                    break;
                }
            }
        }

        return new SolveResult(converged, sw.ElapsedMilliseconds, streams, energy, unitOps, warnings);

        static double? RoundN(double? v, int digits) =>
            v is double d && double.IsFinite(d) ? Math.Round(d, digits) : null;

        // Non-finite values (diverged/unsolved streams) become null — they are
        // not representable in JSON and must never kill the worker.
        static double? Round(double? si, double offset = 0, double scale = 1) =>
            si is double v && double.IsFinite(v) ? Math.Round(v * scale + offset, 3) : null;
    }
}