// dwsim-runner Worker — GPL-3.0
// One solve per process. argv[0] = path to a job JSON file:
//   { "template": "/templates/pem-ref-plant.dwxmz",
//     "overrides": [ { "object": "feed water", "property": "massflow", "value": 98041, "unit": "kg/h" } ] }
// Prints one JSON result document to stdout and exits. The API process owns timeouts.

using System.Text.Json;
using System.Text.Json.Serialization;
using DwsimRunner.Worker;

DwsimResolver.Install();   // MUST run before Solver is touched

var job = JsonSerializer.Deserialize<Job>(
    File.ReadAllText(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

// Solver lives in a separate class so its DWSIM-typed code isn't JIT'd
// until the resolver hook is installed.
var result = Solver.Run(job);

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
}));

record Job(string Template, List<Override> Overrides);
record Override(string Object, string Property, double Value, string? Unit);

record StreamRow(string Name, string? Phase, double? TemperatureC, double? PressureBar,
                 double? MassFlowKgH, double? MolarFlowKmolH, Dictionary<string, double>? CompositionMol);
record EnergyRow(string Name, double? DutyKw);
record SolveResult(bool Converged, long ElapsedMs, List<StreamRow> Streams,
                   List<EnergyRow> Energy, List<string> Warnings);

static class Solver
{
    public static SolveResult Run(Job job)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();

        var auto = new DWSIM.Automation.Automation3();
        var fs = auto.LoadFlowsheet(job.Template)
                 ?? throw new InvalidOperationException($"failed to load '{job.Template}'");

        // ── apply overrides ────────────────────────────────────────────────
        foreach (var ov in job.Overrides)
        {
            var obj = fs.GetFlowsheetSimulationObject(ov.Object)
                      ?? throw new InvalidOperationException($"no object named '{ov.Object}'");

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
                        warnings.Add($"unsupported stream property '{ov.Property}' on '{ov.Object}'");
                        break;
                }
            }
            else
            {
                // Generic path for unit ops: DWSIM property-value interface.
                try { obj.SetPropertyValue(ov.Property, ov.Value); }
                catch (Exception ex) { warnings.Add($"{ov.Object}.{ov.Property}: {ex.Message}"); }
            }
        }

        // ── solve ──────────────────────────────────────────────────────────
        auto.CalculateFlowsheet2(fs);
        bool converged = fs.Solved;
        if (!converged && !string.IsNullOrEmpty(fs.ErrorMessage))
            warnings.Add(fs.ErrorMessage);

        // ── harvest results ────────────────────────────────────────────────
        var streams = new List<StreamRow>();
        var energy  = new List<EnergyRow>();

        foreach (var obj in fs.SimulationObjects.Values)
        {
            switch (obj)
            {
                case DWSIM.Thermodynamics.Streams.MaterialStream ms:
                {
                    var comp = new Dictionary<string, double>();
                    foreach (var c in ms.Phases[0].Compounds.Values)
                        if (c.MoleFraction is double mf && double.IsFinite(mf) && mf > 1e-9)
                            comp[c.Name] = Math.Round(mf, 6);

                    streams.Add(new StreamRow(
                        Name:           ms.GraphicObject.Tag,
                        Phase:          ms.Phases[0].Properties.molarfraction == 1 ? "vapor" : null, // simplistic; refine per phase report
                        TemperatureC:   Round(ms.Phases[0].Properties.temperature, -273.15),
                        PressureBar:    Round(ms.Phases[0].Properties.pressure, 0, 1e-5),
                        MassFlowKgH:    Round(ms.Phases[0].Properties.massflow, 0, 3600),
                        MolarFlowKmolH: Round(ms.Phases[0].Properties.molarflow, 0, 3.6),
                        CompositionMol: comp));
                    break;
                }
                case DWSIM.UnitOperations.Streams.EnergyStream es:
                    energy.Add(new EnergyRow(es.GraphicObject.Tag,
                        es.EnergyFlow is double ef && double.IsFinite(ef)
                            ? Math.Round(ef, 1) : null)); // DWSIM SI energy flow is already kW
                    break;
            }
        }

        return new SolveResult(converged, sw.ElapsedMilliseconds, streams, energy, warnings);

        // Non-finite values (diverged/unsolved streams) become null — they are
        // not representable in JSON and must never kill the worker.
        static double? Round(double? si, double offset = 0, double scale = 1) =>
            si is double v && double.IsFinite(v) ? Math.Round(v * scale + offset, 3) : null;
    }
}
