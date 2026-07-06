// dwsim-runner API — GPL-3.0
// HTTP front door. Spawns one Worker process per solve; never loads DWSIM
// itself. Error taxonomy, caching, and queue limits per
// specs/001-dwsim-headless-runner/contracts/runner-api.md.

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
var app = builder.Build();

// Settings come from IConfiguration (env vars in production; in-memory
// overrides in tests) — never read Environment directly here.
string Cfg(string key, string fallback) =>
    app.Configuration[key] is { Length: > 0 } v ? v : fallback;

string dwsimPath      = Cfg("DWSIM_PATH", "/opt/dwsim");
string templatesPath  = Path.GetFullPath(Cfg("TEMPLATES_PATH", "/templates"));
string workerDll      = Cfg("WORKER_PATH", "/app/worker/DwsimRunner.Worker.dll");
int    defaultTimeout = int.TryParse(app.Configuration["SOLVE_TIMEOUT_SECONDS"], out var t) ? t : 60;
int    maxConcurrent  = int.TryParse(app.Configuration["MAX_CONCURRENT_SOLVES"], out var c) ? c : 4;
int    cacheSize      = int.TryParse(app.Configuration["CACHE_SIZE"], out var cs) ? cs : 256;

var gate  = new SemaphoreSlim(maxConcurrent);
var cache = new ResultCache(cacheSize);
int maxAdmitted = maxConcurrent * 5;   // running + queued (queue cap = 4×concurrency)
int admitted = 0;

var templateIdPattern = new Regex("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

// Optional shared API key (FR-016): set RUNNER_API_KEY and every route except
// GET /health requires X-Api-Key. Unset = open (local dev). User-level
// authn/authz stays in the consuming platform.
if (app.Configuration["RUNNER_API_KEY"] is { Length: > 0 } apiKey)
{
    var keyBytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path == "/health") { await next(); return; }

        var presented = ctx.Request.Headers["X-Api-Key"].ToString();
        var ok = presented.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented), keyBytes);
        if (!ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(ErrorBody("UNAUTHORIZED", "missing or invalid X-Api-Key header"));
            return;
        }
        await next();
    });
}

// Engine version via FILE METADATA only — the API process never loads DWSIM
// assemblies (Constitution I). We parse the PE assembly manifest with
// System.Reflection.Metadata (a pure reader, part of .NET's standard library),
// which works cross-platform; FileVersionInfo returns empty for PE files on
// Linux. Supported range per research.md R3.
const string SupportedRange = ">=9.0 <10";

(bool Found, string? Version, bool Supported) ProbeDwsim()
{
    var automationDll = Path.Combine(dwsimPath, "DWSIM.Automation.dll");
    if (!File.Exists(automationDll)) return (false, null, false);
    string? version = null;
    try
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(automationDll));
        if (pe.HasMetadata)
        {
            var asm = pe.GetMetadataReader().GetAssemblyDefinition();
            var v = asm.Version;
            version = v is null ? null : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
    }
    catch { /* metadata unreadable — version stays null */ }
    var supported = Version.TryParse(version, out var parsed) && parsed.Major == 9;
    return (true, version, supported);
}

// One status call answers: is it up, what engine version, what templates (FR-007).
app.MapGet("/health", () =>
{
    var (found, version, supported) = ProbeDwsim();
    return Results.Ok(new
    {
        ok = found,
        dwsimPath,
        dwsimFound = found,
        dwsimVersion = version,
        supportedRange = SupportedRange,
        versionSupported = supported,
        templatesPath,
        templates = ListTemplateIds(),
        maxConcurrent,
        hint = found ? null :
            $"DWSIM not found at '{dwsimPath}'. Install DWSIM (https://dwsim.org) and set DWSIM_PATH " +
            "to its install directory (on-prem: mount the install at /opt/dwsim).",
    });
});

string?[] ListTemplateIds() =>
    Directory.Exists(templatesPath)
        ? Directory.EnumerateFiles(templatesPath, "*.dwxmz")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray()
        : [];

app.MapGet("/templates", () => Results.Ok(ListTemplateIds()));

// Object inventory (FR-014): flowsheet load without solving, via the worker's
// inspect mode; cached by template mtime like solve results.
app.MapGet("/templates/{id}/objects", async (string id, HttpContext http, CancellationToken ct) =>
{
    var (templateFile, error) = ResolveTemplate(id);
    if (error is not null) return error;

    var outcome = await RunCaseAsync(id, templateFile!, [], TimeSpan.FromSeconds(defaultTimeout), ct, mode: "inspect");
    if (outcome.Status == StatusCodes.Status429TooManyRequests)
        http.Response.Headers.RetryAfter = "5";
    return Results.Content(outcome.Body, "application/json", statusCode: outcome.Status);
});

app.MapPost("/solve", async (SolveRequest req, HttpContext http, CancellationToken ct) =>
{
    var (templateFile, error) = ResolveTemplate(req.TemplateId);
    if (error is not null) return error;

    var timeout = TimeSpan.FromSeconds(req.TimeoutSeconds is > 0 and <= 600 ? req.TimeoutSeconds.Value : defaultTimeout);
    var outcome = await RunCaseAsync(req.TemplateId, templateFile!, req.Overrides ?? [], timeout, ct);

    if (outcome.Status == StatusCodes.Status429TooManyRequests)
        http.Response.Headers.RetryAfter = "5";
    return Results.Content(outcome.Body, "application/json", statusCode: outcome.Status);
});

app.MapPost("/compare", async (CompareRequest req, HttpContext http, CancellationToken ct) =>
{
    var (templateFile, error) = ResolveTemplate(req.TemplateId);
    if (error is not null) return error;

    if (req.Cases is not { Count: >= 1 and <= 10 })
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "cases must contain between 1 and 10 entries");

    // Rough whole-request admission check; per-case races still degrade to a
    // per-case QUEUE_FULL error rather than failing the set.
    if (Volatile.Read(ref admitted) + req.Cases.Count > maxAdmitted)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.Content(ErrorBody("QUEUE_FULL", "not enough queue capacity for this case set; retry shortly"),
            "application/json", statusCode: StatusCodes.Status429TooManyRequests);
    }

    var timeout = TimeSpan.FromSeconds(req.TimeoutSeconds is > 0 and <= 600 ? req.TimeoutSeconds.Value : defaultTimeout);

    // Fan out concurrently — each case flows through the same semaphore + cache
    // as /solve, so results are identical across endpoints (FR-008).
    var caseTasks = req.Cases.ToDictionary(
        kv => kv.Key,
        kv => RunCaseAsync(req.TemplateId, templateFile!, kv.Value ?? [], timeout, ct));
    await Task.WhenAll(caseTasks.Values);

    // Bodies are raw JSON strings (SolveResult or CaseError) — stitch by hand.
    using var buffer = new MemoryStream();
    using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
    {
        w.WriteStartObject();
        w.WritePropertyName("results");
        w.WriteStartObject();
        foreach (var (name, task) in caseTasks)
        {
            w.WritePropertyName(name);
            using var doc = JsonDocument.Parse(task.Result.Body);
            doc.RootElement.WriteTo(w);
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }
    return Results.Content(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), "application/json");
});

app.Run("http://0.0.0.0:8080");

// ── helpers ────────────────────────────────────────────────────────────────

(string? File, IResult? Error) ResolveTemplate(string? id)
{
    if (string.IsNullOrEmpty(id) || !templateIdPattern.IsMatch(id))
        return (null, ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "templateId is required and must match ^[A-Za-z0-9._-]+$"));

    var file = Path.GetFullPath(Path.Combine(templatesPath, id + ".dwxmz"));
    if (!file.StartsWith(templatesPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        return (null, ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "templateId escapes the templates directory"));

    if (!File.Exists(file))
        return (null, ErrorResult(StatusCodes.Status404NotFound, "TEMPLATE_NOT_FOUND",
            $"unknown template '{id}'"));

    return (file, null);
}

static IResult ErrorResult(int status, string error, string message) =>
    Results.Json(new { error, message }, statusCode: status);

static string ErrorBody(string error, string message) =>
    JsonSerializer.Serialize(new { error, message });

// One solve case end-to-end: cache → admission control → worker process →
// exit-code mapping. Shared by /solve (and /compare later). Returns the HTTP
// status and the exact JSON body.
async Task<CaseOutcome> RunCaseAsync(string templateId, string templateFile,
    List<PropertyOverride> overrides, TimeSpan timeout, CancellationToken ct, string? mode = null)
{
    var solveId = Guid.NewGuid().ToString("N")[..8];
    var clock = Stopwatch.StartNew();
    void LogOutcome(string outcome, bool cacheHit = false) => app.Logger.LogInformation(
        "solve {SolveId}: template={Template} overrides={OverrideCount} mode={Mode} outcome={Outcome} cacheHit={CacheHit} elapsedMs={ElapsedMs}",
        solveId, templateId, overrides.Count, mode ?? "solve", outcome, cacheHit, clock.ElapsedMilliseconds);

    var cacheKey = ResultCache.KeyFor(mode is null ? templateId : $"{templateId}\n#{mode}", templateFile, overrides);
    if (cache.TryGet(cacheKey, out var cached))
    {
        LogOutcome("ok", cacheHit: true);
        return new(StatusCodes.Status200OK, cached);
    }

    if (Interlocked.Increment(ref admitted) > maxAdmitted)
    {
        Interlocked.Decrement(ref admitted);
        LogOutcome("QUEUE_FULL");
        return new(StatusCodes.Status429TooManyRequests,
            ErrorBody("QUEUE_FULL", $"solve queue is full ({maxAdmitted} requests admitted); retry shortly"));
    }

    // Job handed to the worker via a temp file (keeps argv clean, avoids stdin plumbing).
    var jobFile = Path.Combine(Path.GetTempPath(), $"dwsim-job-{Guid.NewGuid():N}.json");
    try
    {
        await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(new
        {
            template = templateFile,
            overrides,
            mode,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), ct);

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
                LogOutcome("SOLVE_TIMEOUT");
                return new(StatusCodes.Status504GatewayTimeout,
                    ErrorBody("SOLVE_TIMEOUT", $"solve timed out after {timeout.TotalSeconds}s"));
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode == 0)
                app.Logger.LogDebug("solve {SolveId} worker stderr: {Stderr}", solveId, stderr);
            else
                app.Logger.LogWarning("solve {SolveId} worker exit {Code}, stderr: {Stderr}", solveId, proc.ExitCode, stderr);

            switch (proc.ExitCode)
            {
                case 0:
                    // Normalize (minify) so /solve and /compare emit identical
                    // bytes for the same result, and reject protocol violations.
                    string body;
                    bool converged;
                    try
                    {
                        var node = System.Text.Json.Nodes.JsonNode.Parse(stdout)!;
                        converged = node["converged"]?.GetValue<bool>() ?? false;

                        // Engine outside the supported range solves best-effort
                        // with an explicit warning (research.md R3).
                        var (found, version, supported) = ProbeDwsim();
                        if (found && !supported && node["warnings"] is System.Text.Json.Nodes.JsonArray warnings)
                            warnings.Add($"DWSIM version {version ?? "unknown"} is outside supported range {SupportedRange} — results are best-effort");

                        body = node.ToJsonString();
                    }
                    catch (JsonException)
                    {
                        app.Logger.LogError("worker stdout was not a JSON document for template '{Template}'", templateId);
                        LogOutcome("WORKER_CRASH");
                        return new(StatusCodes.Status500InternalServerError,
                            ErrorBody("WORKER_CRASH", "simulation worker returned an invalid response"));
                    }
                    if (converged || mode == "inspect")   // inventories are pure functions of the template file
                        cache.Set(cacheKey, body);
                    LogOutcome(converged ? "ok" : "not-converged");
                    return new(StatusCodes.Status200OK, body);

                case 2:   // invalid input (unknown object / property) — worker's error doc is client-safe
                    LogOutcome("INVALID_INPUT");
                    return new(StatusCodes.Status400BadRequest,
                        WorkerErrorOrDefault(stdout, "INVALID_REQUEST", "worker rejected the request input"));

                case 3:   // template exists but the engine could not load it
                    LogOutcome("TEMPLATE_LOAD_FAILED");
                    return new(StatusCodes.Status422UnprocessableEntity,
                        WorkerErrorOrDefault(stdout, "TEMPLATE_LOAD_FAILED", "engine could not load the template"));

                default:  // crash — detail stays in server logs only
                    app.Logger.LogError("worker crashed (exit {Code}) for template '{Template}': {Stderr}",
                        proc.ExitCode, templateId, stderr);
                    LogOutcome("WORKER_CRASH");
                    return new(StatusCodes.Status500InternalServerError,
                        ErrorBody("WORKER_CRASH", "simulation worker failed unexpectedly"));
            }
        }
        finally
        {
            gate.Release();
        }
    }
    finally
    {
        Interlocked.Decrement(ref admitted);
        try { File.Delete(jobFile); } catch { /* best effort */ }
    }
}

// Pass the worker's structured error document through when it is valid JSON
// with an "error" field; otherwise synthesize a taxonomy body.
static string WorkerErrorOrDefault(string stdout, string fallbackCode, string fallbackMessage)
{
    var text = stdout.Trim();
    try
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("error", out _))
            return text;
    }
    catch (JsonException) { /* fall through */ }
    return ErrorBody(fallbackCode, fallbackMessage);
}

record SolveRequest(string TemplateId, List<PropertyOverride>? Overrides, int? TimeoutSeconds);
record CompareRequest(string TemplateId, Dictionary<string, List<PropertyOverride>?>? Cases, int? TimeoutSeconds);
public record PropertyOverride(string Object, string Property, double Value, string? Unit);
record CaseOutcome(int Status, string Body);

public partial class Program { } // WebApplicationFactory hook for tests
