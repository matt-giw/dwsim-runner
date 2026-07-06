// dwsim-runner API — GPL-3.0
// HTTP front door. Spawns one Worker process per solve; never loads DWSIM itself.

using System.Diagnostics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
var app = builder.Build();

string dwsimPath     = Env("DWSIM_PATH", "/opt/dwsim");
string templatesPath = Env("TEMPLATES_PATH", "/templates");
string workerDll     = Env("WORKER_PATH", "/app/worker/DwsimRunner.Worker.dll");
int    defaultTimeout = int.TryParse(Environment.GetEnvironmentVariable("SOLVE_TIMEOUT_SECONDS"), out var t) ? t : 60;
int    maxConcurrent  = int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_SOLVES"), out var c) ? c : 4;

var gate = new SemaphoreSlim(maxConcurrent);

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    dwsimPath,
    dwsimFound = File.Exists(Path.Combine(dwsimPath, "DWSIM.Automation.dll")),
    templatesPath,
    maxConcurrent,
}));

app.MapGet("/templates", () =>
    Results.Ok(Directory.Exists(templatesPath)
        ? Directory.EnumerateFiles(templatesPath, "*.dwxmz")
            .Select(Path.GetFileNameWithoutExtension)
        : Enumerable.Empty<string?>()));

app.MapPost("/solve", async (SolveRequest req, CancellationToken ct) =>
{
    var template = Path.Combine(templatesPath, req.TemplateId + ".dwxmz");
    if (!File.Exists(template))
        return Results.NotFound(new { error = $"unknown template '{req.TemplateId}'" });

    var timeout = TimeSpan.FromSeconds(req.TimeoutSeconds is > 0 and <= 600 ? req.TimeoutSeconds.Value : defaultTimeout);

    // Job handed to the worker via a temp file (keeps argv clean, avoids stdin plumbing).
    var jobFile = Path.Combine(Path.GetTempPath(), $"dwsim-job-{Guid.NewGuid():N}.json");
    await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(new
    {
        template,
        overrides = req.Overrides ?? [],
    }), ct);

    await gate.WaitAsync(ct);
    try
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{workerDll}\" \"{jobFile}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["DWSIM_PATH"] = dwsimPath;

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            proc.Kill(entireProcessTree: true);   // the hard timeout — solver hung or diverged
            return Results.Json(new { converged = false, error = $"solve timed out after {timeout.TotalSeconds}s" },
                                statusCode: 504);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
            return Results.Json(new { converged = false, error = "worker failed", detail = stderr }, statusCode: 500);

        // Worker prints exactly one JSON document on stdout.
        return Results.Content(stdout, "application/json");
    }
    finally
    {
        gate.Release();
        try { File.Delete(jobFile); } catch { /* best effort */ }
    }
});

app.Run("http://0.0.0.0:8080");

static string Env(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

record SolveRequest(string TemplateId, List<PropertyOverride>? Overrides, int? TimeoutSeconds);
record PropertyOverride(string Object, string Property, double Value, string? Unit);
