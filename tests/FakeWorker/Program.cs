// FakeWorker — test double for DwsimRunner.Worker. No DWSIM anywhere.
// Speaks the same protocol: argv[0] = job JSON file, one JSON document on
// stdout, typed exit code. Behavior is driven by reserved override object
// names so API tests can provoke every worker outcome:
//   __sleep:N          sleep N seconds before responding (timeout/concurrency tests)
//   __exit:N           write an error document and exit with code N
//   __garbage-stdout   print noise before the JSON document (protocol tests)
// mode == "inspect"     returns a canned object inventory instead of a solve.
// mode == "catalog"     returns a canned engine catalog (no template needed).
// mode == "validate"    returns canned semantic issues; a document containing
//                       an object tagged "__semantic-issue" yields an error issue.
// mode == "build-solve" returns a canned BuildReport; reserved tags provoke
//                       failures: "__unknown-compound" → exit 4 UNKNOWN_COMPOUND,
//                       "__build-fail" → exit 4 BUILD_FAILED, "__not-converged"
//                       → converged:false, "__sleep:N" (tag) → sleep first.
//                       When savePath is set, writes a fake .dwxmz there.
// mode == "flash"       returns a canned flash result; compound "__bad" → exit 2.
// mode == "pfd"         returns { "pngBase64": <1x1 PNG> }; tag "__render-fail" → exit 5.
// Every invocation drops run-{guid}.start/.end marker files (UTC ticks) into
// the job template's directory — or $DWSIM_PATH when the job has no template —
// so tests can count spawns and prove runs don't overlap.

using System.Text.Json;

var job = JsonSerializer.Deserialize<FakeJob>(
    File.ReadAllText(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

var markerDir = job.Template is { Length: > 0 } t
    ? Path.GetDirectoryName(Path.GetFullPath(t))!
    : Environment.GetEnvironmentVariable("DWSIM_PATH") is { Length: > 0 } dp && Directory.Exists(dp)
        ? dp
        : Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
var runId = Guid.NewGuid().ToString("N");
File.WriteAllText(Path.Combine(markerDir, $"run-{runId}.start"), DateTime.UtcNow.Ticks.ToString());
int Done(int code)
{
    File.WriteAllText(Path.Combine(markerDir, $"run-{runId}.end"), DateTime.UtcNow.Ticks.ToString());
    return code;
}

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

// Reserved tags inside a document drive validate/build/pfd behavior.
var docTags = new List<string>();
if (job.Document is { ValueKind: JsonValueKind.Object } doc
    && doc.TryGetProperty("objects", out var objs) && objs.ValueKind == JsonValueKind.Array)
    foreach (var o in objs.EnumerateArray())
        if (o.TryGetProperty("tag", out var tag) && tag.GetString() is { } tg)
            docTags.Add(tg);

foreach (var tg in docTags)
    if (tg.StartsWith("__sleep:") && int.TryParse(tg["__sleep:".Length..], out var ds))
        Thread.Sleep(TimeSpan.FromSeconds(ds));

if (garbage)
    Console.WriteLine("DWSIM-style console noise that must not corrupt the response");

if (errorDoc is not null)
{
    Console.WriteLine(errorDoc);
    return Done(exitCode);
}

switch (job.Mode?.ToLowerInvariant())
{
    case "inspect":
        Console.WriteLine("""
        {"objects":[
          {"tag":"Syngas","type":"materialStream","settableProperties":["massflow","temperature","pressure","molarflow"]},
          {"tag":"Comp-1","type":"compressor","settableProperties":[]},
          {"tag":"W_comp","type":"energyStream","settableProperties":[]}
        ]}
        """.ReplaceLineEndings(""));
        break;

    case "catalog":
        Console.WriteLine("""
        {"engineVersion":"9.0.5.0",
         "compounds":[
           {"name":"Methane","formula":"CH4","casNumber":"74-82-8"},
           {"name":"Ethane","formula":"C2H6","casNumber":"74-84-0"},
           {"name":"Water","formula":"H2O","casNumber":"7732-18-5"},
           {"name":"Methanol","formula":"CH4O","casNumber":"67-56-1"}],
         "propertyPackages":[
           {"id":"PR","name":"Peng-Robinson (PR)","description":"Cubic EOS; hydrocarbons and light gases"},
           {"id":"SRK","name":"Soave-Redlich-Kwong (SRK)","description":"Cubic EOS"},
           {"id":"NRTL","name":"NRTL","description":"Activity model for polar/non-ideal mixtures"}],
         "unitOpTypes":[
           {"type":"separator","displayName":"Gas-Liquid Separator",
            "ports":[{"name":"Inlet","direction":"in","accepts":"material","required":true},
                     {"name":"Vapor Outlet","direction":"out","accepts":"material","required":true},
                     {"name":"Liquid Outlet","direction":"out","accepts":"material","required":true}],
            "parameters":[],"requiresReactionSet":false},
           {"type":"heater","displayName":"Heater",
            "ports":[{"name":"Inlet","direction":"in","accepts":"material","required":true},
                     {"name":"Outlet","direction":"out","accepts":"material","required":true},
                     {"name":"Energy Inlet","direction":"in","accepts":"energy","required":false}],
            "parameters":[{"name":"outletTemperature","unitType":"temperature","required":true}],
            "requiresReactionSet":false},
           {"type":"shortcutColumn","displayName":"Shortcut Column",
            "ports":[{"name":"Feed","direction":"in","accepts":"material","required":true},
                     {"name":"Distillate","direction":"out","accepts":"material","required":true},
                     {"name":"Bottoms","direction":"out","accepts":"material","required":true}],
            "parameters":[{"name":"refluxRatio","unitType":"dimensionless","required":true}],
            "requiresReactionSet":false}]}
        """.ReplaceLineEndings(""));
        break;

    case "validate":
        if (docTags.Contains("__semantic-issue"))
            Console.WriteLine("""
            {"valid":false,"issues":[
              {"severity":"error","code":"UNKNOWN_COMPOUND","tag":"__semantic-issue",
               "path":"compounds[0]","message":"compound 'Methan' not found; did you mean Methane?"}]}
            """.ReplaceLineEndings(""));
        else
            Console.WriteLine("""{"valid":true,"issues":[]}""");
        break;

    case "build-solve":
        if (docTags.Contains("__unknown-compound"))
        {
            Console.WriteLine("""
            {"error":"UNKNOWN_COMPOUND","message":"compound 'Methan' not found",
             "issues":[{"severity":"error","code":"UNKNOWN_COMPOUND","tag":"__unknown-compound",
                        "message":"compound 'Methan' not found; did you mean Methane?"}]}
            """.ReplaceLineEndings(""));
            return Done(4);
        }
        if (docTags.Contains("__build-fail"))
        {
            Console.WriteLine("""
            {"error":"BUILD_FAILED","message":"engine rejected construction",
             "issues":[{"severity":"error","code":"BUILD_FAILED","tag":"__build-fail",
                        "message":"cannot connect stream to itself"}]}
            """.ReplaceLineEndings(""));
            return Done(4);
        }
        if (job.SavePath is { Length: > 0 } sp)
            File.WriteAllText(sp, "fake dwxmz written by FakeWorker");
        var converged = docTags.Contains("__not-converged") ? "false" : "true";
        Console.WriteLine($$$"""
        {"converged":{{{converged}}},"elapsedMs":7,
         "build":{"objectsCreated":{{{Math.Max(docTags.Count, 1)}}},"connectionsMade":3,"elapsedMs":2},
         "streams":[{"name":"VAP","temperatureC":0.0,"pressureBar":50.0,"massFlowKgH":62.1,
                     "molarFlowKmolH":3.4,"compositionMol":{"Methane":0.58,"Ethane":0.42}},
                    {"name":"LIQ","temperatureC":0.0,"pressureBar":50.0,"massFlowKgH":37.9,
                     "molarFlowKmolH":1.4,"compositionMol":{"Methane":0.11,"Ethane":0.89}}],
         "energy":[],
         "unitOps":[{"name":"V-1","type":"separator"}],
         "warnings":[]}
        """.ReplaceLineEndings(""));
        break;

    case "flash":
        if (job.Flash is { ValueKind: JsonValueKind.Object } fl
            && fl.TryGetProperty("compounds", out var comps) && comps.ValueKind == JsonValueKind.Array
            && comps.EnumerateArray().Any(c => c.GetString() == "__bad"))
        {
            Console.WriteLine("""{"error":"FLASH_INVALID","message":"compound '__bad' not found"}""");
            return Done(2);
        }
        Console.WriteLine("""
        {"vaporFraction":0.83,"temperatureC":0.0,"pressureBar":10.0,
         "phases":[{"phase":"Vapor","molarFraction":0.83,"composition":{"Methane":0.58,"Ethane":0.42}},
                   {"phase":"Liquid","molarFraction":0.17,"composition":{"Methane":0.11,"Ethane":0.89}}],
         "enthalpyKJKg":-120.4,"entropyKJKgK":-1.02}
        """.ReplaceLineEndings(""));
        break;

    case "pfd":
        if (docTags.Contains("__render-fail"))
        {
            Console.WriteLine("""{"error":"RENDER_FAILED","message":"synthetic render failure"}""");
            return Done(5);
        }
        // Smallest valid PNG (1×1 transparent pixel).
        Console.WriteLine("""{"pngBase64":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="}""");
        break;

    default:
        Console.WriteLine("""
        {"converged":true,"elapsedMs":5,
         "streams":[{"name":"Syngas","temperatureC":26.85,"pressureBar":30.0,"massFlowKgH":115.351,
                     "molarFlowKmolH":10.8,"compositionMol":{"Hydrogen":0.666667,"Carbon monoxide":0.333333}}],
         "energy":[{"name":"W_comp","dutyKw":11.5}],
         "unitOps":[{"name":"Comp-1","type":"compressor","powerKw":11.5,
                     "outletTemperatureC":156.9,"outletPressureBar":80.0}],
         "warnings":[]}
        """.ReplaceLineEndings(""));
        break;
}

return Done(0);

record FakeJob(string? Template, List<FakeOverride>? Overrides, string? Mode,
               JsonElement? Document, JsonElement? Flash, string? SavePath);
record FakeOverride(string? Object, string? Property, double? Value, string? Unit);
