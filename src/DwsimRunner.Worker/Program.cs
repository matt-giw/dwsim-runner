// dwsim-runner Worker — GPL-3.0
// One job per process. argv[0] = path to a job JSON file. The mode field
// selects a handler; each prints exactly one JSON document to stdout and
// exits with a typed code (data-model.md error taxonomy):
//   0 = success (converged flag is in the payload for solve/build-solve)
//   2 = invalid input  → { error, message, detail? }
//   3 = template load failed
//   4 = build failed / unknown compound (issues attached)
//   5 = render failed (pfd)
//   6 = the worker's own wall-clock deadline fired (Watchdog.cs)
//   7 = read mode only: the sandbox could not be applied, so nothing ran (ReadSandbox.cs)
//   1 = unexpected crash (full detail on stderr only)
// The API maps exit codes → HTTP. It ALSO applies a timeout of its own, but this process no
// longer depends on that: FND-0103/FND-0104 were filed against the sentence that used to stand
// here — "the API process owns timeouts" — because it names an enforcement point outside this
// process that nothing here can verify. See Watchdog.cs.

using System.Text.Json;
using System.Text.Json.Serialization;
using DwsimRunner.Worker;

// iskra 285 — the read worker's sandbox, FIRST: `read` applies it and re-execs itself (so it must come
// before ProtocolChannel moves fd 1, and before any engine code or the upload). Every other mode
// passes straight through. Exit 7 = SANDBOX_UNAVAILABLE: the sandbox could not be applied, nothing ran.
if (ReadSandbox.EnterIfRequired(args[0]) is int sandboxExit) return sandboxExit;

// MUST run before any native library can print to fd 1. Console.SetOut below
// only moves the MANAGED writer; Ipopt writes its banner to the descriptor and
// corrupted the protocol for every solve that loaded it. See ProtocolChannel.
ProtocolChannel.Divert();

DwsimResolver.Install();   // MUST run before any DWSIM-typed code is JIT'd

var job = JsonSerializer.Deserialize<Job>(
    File.ReadAllText(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

// FND-0103/FND-0104 — armed before the first engine call and covering the WHOLE job, so a solve
// that never converges dies here rather than waiting for a caller timeout that may not exist.
using var deadline = Watchdog.Arm(
    TimeSpan.FromSeconds(Watchdog.DeadlineSeconds(Environment.GetEnvironmentVariable)),
    Environment.Exit);

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
        "read"         => Reader.Read(job),   // iskra 285 — a DWSIM file → runner document
        "sandbox-probe" => ReadSandbox.Probe(args[0]),   // iskra 285 — /health and the conformance tests
        _              => Solver.Run(job),   // "solve" (default for spec-001 back-compat)
    };
}
catch (WorkerInputException ex)
{
    exitCode = 2;
    payload = new ErrorDoc(ex.Code, ex.Message, ex.Detail);
}
catch (SandboxUnavailableException ex)
{
    exitCode = ReadSandbox.ExitUnavailable;
    payload = new ErrorDoc("SANDBOX_UNAVAILABLE", ex.Message, null);
}
catch (TemplateLoadException ex)
{
    exitCode = 3;
    payload = new ErrorDoc(ex.Code, ex.Message, null);
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
                 List<StreamPhaseBlock>? Phases = null,
                 // 259 — everything else the engine computed for the BULK (Mixture) phase, keyed by
                 // the DWSIM member name and valued in the engine's SI store, UNCONVERTED. The
                 // named fields above are the curated, converted set callers already read; this is
                 // additive beside them and the two must agree (OpenApiContractTests).
                 // Units are declared once per response, never here — see SolveResult.PropertyUnits.
                 Dictionary<string, double>? Properties = null);

// Physics-named phase blocks ("vapor"/"liquid"/"liquid2"/"solid") — never engine slot indexes.
record StreamPhaseBlock(string Name, double MoleFraction,
                        Dictionary<string, double>? Composition = null,
                        double? DensityKgM3 = null,
                        double? MolecularWeight = null,
                        double? HeatCapacityKJKgK = null,
                        double? ViscosityPaS = null,
                        // 259 — the same bag, for THIS phase.
                        Dictionary<string, double>? Properties = null);
record EnergyRow(string Name, double? DutyKw);
record UnitOpRow(string Name, string Type, double? PowerKw, double? DutyKw,
                 double? OutletTemperatureC, double? OutletPressureBar,
                 // 143 — nullable and last (the StreamRow convention): a column's solver
                 // configuration, read back off the engine object. Null on everything else.
                 string? SolvingMethod = null, int? MaxIterations = null);
record SolveResult(bool Converged, long ElapsedMs, List<StreamRow> Streams,
                   List<EnergyRow> Energy, List<UnitOpRow> UnitOps, List<string> Warnings,
                   // 259 FR-003 — the SI unit of every key a `properties` bag can carry, ONCE.
                   // It is a constant of the engine version; repeating it on every phase of every
                   // stream would be most of the payload.
                   Dictionary<string, string>? PropertyUnits = null);

record ObjectInfo(string Tag, string Type, List<string> SettableProperties);
record InventoryResult(List<ObjectInfo> Objects);

class WorkerInputException(string code, string message, string? detail = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}

// `code` is the error the API passes through: TEMPLATE_LOAD_FAILED for a stored template, and
// iskra 285's LOAD_FAILED for an uploaded file, whose message is the full exception chain.
class TemplateLoadException(string message, string code = "TEMPLATE_LOAD_FAILED") : Exception(message)
{
    public string Code { get; } = code;
}
class RenderFailedException(string message) : Exception(message);

static class Solver
{
    private static (DWSIM.Automation.Automation3 Auto, DWSIM.Interfaces.IFlowsheet Fs) Load(string template)
    {
        var auto = new DWSIM.Automation.Automation3();
        return (auto, Engine.LoadTemplate(auto, template));
    }

    private static readonly List<string> StreamProperties =
        ["massflow", "temperature", "pressure", "molarflow"];

    /// <summary>Flowsheet load without solving: object inventory (FR-014).</summary>
    public static InventoryResult Inspect(Job job)
    {
        var (_, fs) = Load(job.Template ?? throw new WorkerInputException("INVALID_REQUEST", "template is required for inspect mode"));
        var objects = fs.SimulationObjects.Values
            .Select(o => new ObjectInfo(
                Tag: o.GraphicObject.Tag,
                Type: Engine.FriendlyType(o),
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
        // ONE harvest, shared with build-solve (Modes.Harvest): the stream, energy and unit-op rows
        // a template solve reports cannot differ from a document solve's for the same flowsheet.
        var (streams, energy, unitOps) = Modes.Harvest(fs);

        // 281 — a template carries no compound definitions, so there is no solid to guard; the
        // melting-point warning still applies, through the helper build-solve uses.
        warnings.AddRange(Modes.MeltingPointWarnings(fs, streams, new HashSet<string>()));

        return new SolveResult(converged, sw.ElapsedMilliseconds, streams, energy, unitOps, warnings,
                               PropertyUnits: PhaseProperties.UnitsForResponse());
    }
}