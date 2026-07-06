// FakeWorker — test double for DwsimRunner.Worker. No DWSIM anywhere.
// Speaks the same protocol: argv[0] = job JSON file, one JSON document on
// stdout, typed exit code. Behavior is driven by reserved override object
// names so API tests can provoke every worker outcome:
//   __sleep:N          sleep N seconds before responding (timeout/concurrency tests)
//   __exit:N           write an error document and exit with code N
//   __garbage-stdout   print noise before the JSON document (protocol tests)
// mode == "inspect" returns a canned object inventory instead of a solve.
// Every invocation drops run-{guid}.start/.end marker files (UTC ticks) into
// the job template's directory — the per-test temp TEMPLATES_PATH — so tests
// can count spawns and prove runs don't overlap.

using System.Text.Json;

var job = JsonSerializer.Deserialize<FakeJob>(
    File.ReadAllText(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

var markerDir = Path.GetDirectoryName(Path.GetFullPath(job.Template))!;
var runId = Guid.NewGuid().ToString("N");
File.WriteAllText(Path.Combine(markerDir, $"run-{runId}.start"), DateTime.UtcNow.Ticks.ToString());

int exitCode = 0;
string? errorDoc = null;
bool garbage = false;

foreach (var ov in job.Overrides ?? [])
{
    if (ov.Object is null) continue;
    if (ov.Object.StartsWith("__sleep:") && int.TryParse(ov.Object["__sleep:".Length..], out var s))
        Thread.Sleep(TimeSpan.FromSeconds(s));
    else if (ov.Object.StartsWith("__exit:") && int.TryParse(ov.Object["__exit:".Length..], out var code))
    {
        exitCode = code;
        errorDoc = code switch
        {
            2 => """{"error":"INVALID_OBJECT","message":"no object named 'bogus'","detail":"available: Syngas, Comp-1"}""",
            3 => """{"error":"TEMPLATE_LOAD_FAILED","message":"engine could not load template"}""",
            _ => """{"error":"WORKER_CRASH","message":"synthetic crash"}""",
        };
    }
    else if (ov.Object == "__garbage-stdout")
        garbage = true;
}

if (garbage)
    Console.WriteLine("DWSIM-style console noise that must not corrupt the response");

if (errorDoc is not null)
{
    Console.WriteLine(errorDoc);
    File.WriteAllText(Path.Combine(markerDir, $"run-{runId}.end"), DateTime.UtcNow.Ticks.ToString());
    return exitCode;
}

if (string.Equals(job.Mode, "inspect", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("""
    {"objects":[
      {"tag":"Syngas","type":"materialStream","settableProperties":["massflow","temperature","pressure","molarflow"]},
      {"tag":"Comp-1","type":"compressor","settableProperties":[]},
      {"tag":"W_comp","type":"energyStream","settableProperties":[]}
    ]}
    """.ReplaceLineEndings(""));
}
else
{
    Console.WriteLine("""
    {"converged":true,"elapsedMs":5,
     "streams":[{"name":"Syngas","temperatureC":26.85,"pressureBar":30.0,"massFlowKgH":115.351,
                 "molarFlowKmolH":10.8,"compositionMol":{"Hydrogen":0.666667,"Carbon monoxide":0.333333}}],
     "energy":[{"name":"W_comp","dutyKw":11.5}],
     "unitOps":[{"name":"Comp-1","type":"compressor","powerKw":11.5,
                 "outletTemperatureC":156.9,"outletPressureBar":80.0}],
     "warnings":[]}
    """.ReplaceLineEndings(""));
}

File.WriteAllText(Path.Combine(markerDir, $"run-{runId}.end"), DateTime.UtcNow.Ticks.ToString());
return 0;

record FakeJob(string Template, List<FakeOverride>? Overrides, string? Mode);
record FakeOverride(string? Object, string? Property, double Value, string? Unit);
