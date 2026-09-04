// dwsim-runner API — GPL-3.0
// HTTP front door. Spawns one Worker process per solve; never loads DWSIM
// itself. Error taxonomy, caching, and queue limits per
// specs/001-dwsim-headless-runner/contracts/runner-api.md.

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using DwsimRunner.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

// OpenAPI (ISK-231). The document is generated from endpoint metadata, so it describes the
// routes this file actually registers rather than a hand-written second opinion about them.
// Response bodies stay worker pass-through at runtime — see Contracts.cs for why declaring
// them beats binding them, and OpenApiContractTests for what stops a declaration drifting.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    // Named "openapi" so UseSwagger's "{documentName}.json" template serves it at /openapi.json.
    o.SwaggerDoc("openapi", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "dwsim-runner",
        Version = "v1",
        Description =
            "Headless DWSIM solve service.\n\n"
            + "**Every route is synchronous** — there are no jobs, no polling and no callbacks. A solve "
            + "holds the connection and is bounded by a timeout, not by a job id.\n\n"
            + "**Non-convergence is not an error**: a flowsheet that fails to converge is `200 OK` with "
            + "`converged: false`. Reserve HTTP failure for \"we could not run your request\".\n\n"
            + "**Results are cached.** Document-scoped results are cached even when they did not converge, "
            + "so a repeated request replays the same failure in ~0 ms and `elapsedMs` is the original "
            + "solve's figure. Restart the service before benchmarking anything.\n\n"
            + "**Auth is required.** Every route except `GET /health` needs `X-Api-Key`; a runner with "
            + "no key configured refuses them all with `503 AUTH_NOT_CONFIGURED`.\n\n"
            + "**Fan-out is budgeted.** `/compare` and `/optimize` refuse up front with "
            + "`400 WORK_BUDGET_EXCEEDED` when `cases x timeoutSeconds` exceeds the runner's budget, "
            + "so the documented maximums are not simultaneously available.\n\n"
            + "Narrative reference, including the caching and queueing rules in full: `docs/api.md`.",
        License = new Microsoft.OpenApi.Models.OpenApiLicense { Name = "GPL-3.0" },
    });

    // Declared so the UI's Authorize button can attach the header to try-it calls. Whether it is
    // actually enforced depends on RUNNER_API_KEY being set — described in the scheme text rather
    // than asserted here, because this document is static and that setting is not.
    o.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "X-Api-Key",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description =
            "Required on every route except GET /health. A runner with no RUNNER_API_KEY configured "
            + "REFUSES every route but /health with 503 AUTH_NOT_CONFIGURED — unset is a refusal, "
            + "never an opening. Clients read the value from SIM_RUNNER_API_KEY.",
    });
    o.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference
            {
                Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                Id = "ApiKey",
            },
        }] = Array.Empty<string>(),
    });

    var xml = Path.Combine(AppContext.BaseDirectory, "DwsimRunner.Api.xml");
    if (File.Exists(xml)) o.IncludeXmlComments(xml, includeControllerXmlComments: true);
});

var app = builder.Build();

// Settings come from IConfiguration (env vars in production; in-memory
// overrides in tests) — never read Environment directly here.
string Cfg(string key, string fallback) =>
    app.Configuration[key] is { Length: > 0 } v ? v : fallback;

// 147 US2 (FR-006) — WHICH BUILD this engine is, baked at image build time from a Docker ARG.
// `dwsimVersion` below is the DWSIM LIBRARY version and cannot tell two runner builds apart:
// measured 2026-08-08, the engine iskra deployed on 2026-07-30 and the one it pins today return
// identical /health while disagreeing about which flash types they accept.
// Unset is an EXPLICIT "unknown", never an absent field — an absent field and a stale one look
// the same to a consumer, and "unknown drift" must not read as "no drift".
string buildRef       = Cfg("BUILD_REF", "unknown");
string dwsimPath      = Cfg("DWSIM_PATH", "/opt/dwsim");
string templatesPath  = Path.GetFullPath(Cfg("TEMPLATES_PATH", "/templates"));
string workerDll      = Cfg("WORKER_PATH", "/app/worker/DwsimRunner.Worker.dll");
int    defaultTimeout = int.TryParse(app.Configuration["SOLVE_TIMEOUT_SECONDS"], out var t) ? t : 60;
int    maxConcurrent  = int.TryParse(app.Configuration["MAX_CONCURRENT_SOLVES"], out var c) ? c : 4;
int    cacheSize      = int.TryParse(app.Configuration["CACHE_SIZE"], out var cs) ? cs : 256;

// FND-0029 — the aggregate work one request may commission, in worker-seconds. `/optimize` and
// `/compare` both fan a single request out into many bounded solves; bounding each one says
// nothing about the total. maxEvaluations(30) x timeoutSeconds(600) is five hours on one slot.
//
// Default 3600 s. The corpus calibrates it: 216 measured solves in spec 143 all finished inside
// the deployed 60 s SOLVE_TIMEOUT_SECONDS, so a request left on the default per-case timeout is
// unaffected at any legal case count (30 x 60 = 1800). Only a caller explicitly asking for long
// per-case timeouts AND many cases is refused, and it is refused BEFORE it takes a slot.
int    maxWorkSeconds = int.TryParse(app.Configuration["MAX_REQUEST_WORK_SECONDS"], out var mw) ? mw : 3600;

// FND-0102 — document construction caps. `MaxObjects`/`MaxDocumentBytes` already shipped as
// constants; connections and reactions were unbounded and pfd bypasses this validator entirely
// (the worker carries its own copy of all four for that reason — FlowsheetBuilder.ParseDocument).
// Largest document in the eval corpus: 47 objects / 48 connections / 4 reactions. These defaults
// are ~10x that.
DocumentValidator.MaxObjects       = int.TryParse(app.Configuration["MAX_DOCUMENT_OBJECTS"], out var mo) ? mo : 500;
DocumentValidator.MaxConnections   = int.TryParse(app.Configuration["MAX_DOCUMENT_CONNECTIONS"], out var mc) ? mc : 1000;
DocumentValidator.MaxReactions     = int.TryParse(app.Configuration["MAX_DOCUMENT_REACTIONS"], out var mr) ? mr : 200;
DocumentValidator.MaxDocumentBytes = int.TryParse(app.Configuration["MAX_DOCUMENT_BYTES"], out var mb) ? mb : 200 * 1024;

var gate  = new SemaphoreSlim(maxConcurrent);
var cache = new ResultCache(cacheSize);
int maxAdmitted = maxConcurrent * 5;   // running + queued (queue cap = 4×concurrency)
int admitted = 0;

// Engine catalog (FR-CAT-001..004): fetched once per engine version via the
// worker's `catalog` mode, then served from memory.
var catalogLock = new SemaphoreSlim(1, 1);
string? catalogVersionKey = null;
string? catalogJson = null;
CatalogModel? catalogModel = null;   // parsed view used by DocumentValidator (port/parameter map)

// USER_TEMPLATES_PATH (T001): where build-solve saves flowsheets; the store
// rebuilds the directory if missing. The same dir hosts the .doc.json
// provenance sidecars per data-model.md.
string userTemplatesPath = Path.GetFullPath(Cfg("USER_TEMPLATES_PATH", Path.Combine(templatesPath, "user")));
var userTemplates = new UserTemplateStore(userTemplatesPath, templatesPath);
userTemplates.EnsureDirectory();

var templateIdPattern = new Regex("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

// ISK-231 — the OpenAPI document and Swagger UI are GATED like every other route by default.
//
// An earlier draft of this exempted them alongside /health, reasoning that an API description is
// not API data and that gating them makes the browser UI unusable (Swagger UI's first act is an
// unauthenticated fetch of the spec, before its Authorize button exists to carry a key). That
// reasoning is fine as far as it goes and it still produced the wrong default: it grew a second
// exemption list in the middleware that FND-0002 exists to remove, and the block below says in
// writing that /health is the only one.
//
// So the exemption is explicit, single, and OFF by default. Note which way it fails: forgetting
// DOCS_PUBLIC leaves the docs CLOSED. That is the opposite of the defect being fixed here, where
// forgetting to classify a route left it OPEN.
bool docsPublic = string.Equals(Cfg("DOCS_PUBLIC", "false"), "true", StringComparison.OrdinalIgnoreCase);
static bool IsDocsPath(PathString p) =>
    p.StartsWithSegments("/openapi.json") || p.StartsWithSegments("/docs");

// Shared API key (FR-016). FND-0002 / FND-0075: this used to read
// `if (RUNNER_API_KEY is { Length: > 0 })` — the middleware was registered ONLY when a key
// was configured, so an absent or empty value meant no auth middleware at all. The failure
// mode of a config omission was silent and OPEN.
//
// UNSET IS A REFUSAL, NEVER AN OPENING. The middleware is now registered unconditionally and
// there is no configuration in which a route below is reachable without a key. This is the
// same rule iskra-app's `checkApiAuth` already enforces — `if (!expected) return { ok: false,
// reason: "API key auth is not configured" }` (iskra-app/lib/auth.ts) — spec 032's "unset =
// open is not a gate", second service. The two now agree.
//
// The unconfigured case answers 503, not 401: a missing server secret is not a client
// credential problem, and a deploy that lost its variable must not read as "your key is
// wrong". `/health` stays exempt so that misconfiguration is diagnosable (and so the
// container healthcheck can still report), which is deliberately the ONLY exemption.
//
// READ ROUTES ARE GATED TOO, and that is a decision rather than an oversight:
//   - GET /templates and GET /templates/{id}/file enumerate and stream saved flowsheets —
//     customer documents, not public metadata.
//   - GET /templates/{id}/objects, /templates/{id}/pfd.png and every /catalog/* route SPAWN A
//     WORKER PROCESS. Engine execution on a GET is the same resource primitive as a POST.
//   - A read/write split needs a route classification list, and the failure mode of forgetting
//     to add a new route to it is silent and open — the exact defect being fixed here. One
//     rule, no list.
var apiKey = app.Configuration["RUNNER_API_KEY"] ?? "";
var apiKeyBytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/health") { await next(); return; }
    if (docsPublic && IsDocsPath(ctx.Request.Path)) { await next(); return; }

    if (apiKey.Length == 0)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(ErrorBody("AUTH_NOT_CONFIGURED",
            "RUNNER_API_KEY is not set on this runner; every route except GET /health is refused"));
        return;
    }

    var presented = ctx.Request.Headers["X-Api-Key"].ToString();
    var ok = presented.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(presented), apiKeyBytes);
    if (!ok)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(ErrorBody("UNAUTHORIZED", "missing or invalid X-Api-Key header"));
        return;
    }
    await next();
});

if (apiKey.Length == 0)
    app.Logger.LogError(
        "RUNNER_API_KEY is not set — every route except GET /health will answer 503 AUTH_NOT_CONFIGURED");

// A malformed body fails in the MODEL BINDER, before the handler runs, so the framework's default
// 400 (an empty body, or ProblemDetails) escapes instead of the taxonomy's { error, message }.
// That was already the case for the routes binding a request record — /solve, /compare, /optimize —
// while the hand-parsed ones answered INVALID_REQUEST: one API with two shapes for one mistake,
// and only the hand-parsed half had a test. Catching it here makes every route answer the same way,
// which is what let the remaining handlers move off JsonDocument parsing (ISK-231).
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (BadHttpRequestException)
    {
        if (ctx.Response.HasStarted) throw;
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(ErrorBody("INVALID_REQUEST", "request body must be JSON"));
    }
});

// Always registered — the auth middleware above decides who reaches them, so there is no second
// on/off switch here. With DOCS_PUBLIC unset these answer 401 (or 503 when no key is configured),
// exactly like every other route.
//
// The document is NAMED "openapi" so this template serves it at /openapi.json. Do not add a
// separate MapGet for that path: this middleware matches {documentName}.json first, would
// resolve documentName to "openapi", and 404s on an unknown document before any endpoint runs.
app.UseSwagger(o => o.RouteTemplate = "{documentName}.json");

app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi.json", "dwsim-runner v1");
    o.RoutePrefix = "docs";
    o.DocumentTitle = "dwsim-runner API";
});

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

// ISK-104 — /health must prove FLOWSHEET CONSTRUCTION, not that a file is on disk.
//
// ProbeDwsim() above stats DWSIM.Automation.dll and reads its PE version. The API process
// never loads DWSIM (Constitution I), so everything that can actually break lives in the
// WORKER: a bad image, a missing native dependency, a truncated DWSIM copy. All of those
// report `ok: true, dwsimFound: true` today and fail every solve — and WORKER_CRASH is the
// largest error class in the field (iskra 137/138 trace census).
//
// The probe is the EXISTING `validate` worker mode (Document → FlowsheetBuilder.Build, no
// solve) against the flash-drum document the live integration suite already builds. It is a
// property of the IMAGE, so it runs ONCE per process — in the BACKGROUND, on the first
// /health call:
//   • background, because /health is the one route that skips the API key and Railway polls
//     it; a worker spawn per call is both slow and an open cost;
//   • on first call rather than at startup, so the API test hosts don't each spawn a worker;
//   • once, because a boot-time answer is exactly the question ("can THIS build construct a
//     flowsheet"). The first /health of a deploy reports `pending`; the next reports the fact.
//
// `ok` deliberately does NOT fold this in. Railway health-checks this path, so a failed probe
// folded into `ok` would hold a deploy out of service on a background result that is not ready
// when the first check arrives. The probe is REPORTED; the reader decides.
// The flash-drum document from tests/DwsimRunner.Integration.Tests/BuildSolveTests.cs — already
// proven to BUILD AND SOLVE against the live engine, which is the only reason to prefer it to a
// bare stream. It exercises compound resolution, package resolution, unit-op instantiation and
// port planning; a probe document nothing else builds would make a red probe ambiguous.
const string FlowsheetProbeDocument = """
{
  "schemaVersion": 1,
  "name": "health flowsheet probe",
  "compounds": ["Methane", "Ethane"],
  "propertyPackage": "PR",
  "objects": [
    { "tag": "FEED", "kind": "materialStream",
      "spec": { "temperature": { "value": -40, "unit": "C" },
                "pressure": { "value": 10, "unit": "bar" },
                "massFlow": { "value": 100, "unit": "kg/h" },
                "composition": { "basis": "molar",
                                 "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
    { "tag": "V-1", "kind": "unitOp", "type": "separator" },
    { "tag": "VAP", "kind": "materialStream" },
    { "tag": "LIQ", "kind": "materialStream" }
  ],
  "connections": [
    { "from": "FEED", "to": "V-1", "port": "Inlet" },
    { "from": "V-1", "to": "VAP", "port": "Vapor Outlet" },
    { "from": "V-1", "to": "LIQ", "port": "Liquid Outlet" }
  ]
}
""";

ProbeReport flowsheetProbe = ProbeReport.Pending;
int flowsheetProbeStarted = 0;

void StartFlowsheetProbe()
{
    if (Interlocked.Exchange(ref flowsheetProbeStarted, 1) != 0) return;
    _ = Task.Run(async () =>
    {
        var sw = Stopwatch.StartNew();
        string? failure = null;
        try
        {
            using var probeDoc = JsonDocument.Parse(FlowsheetProbeDocument);
            var outcome = await RunDocumentModeAsync(probeDoc.RootElement.Clone(), "validate",
                TimeSpan.FromSeconds(defaultTimeout), null, CancellationToken.None);
            // Construction succeeded iff the worker answered 200 with valid:true. A 200
            // carrying valid:false is the engine REFUSING to build, which is the outcome
            // this probe exists to make visible — not a pass with issues attached.
            bool built = outcome.Status == StatusCodes.Status200OK
                && JsonDocument.Parse(outcome.Body).RootElement.TryGetProperty("valid", out var v)
                && v.ValueKind == JsonValueKind.True;
            if (!built) failure = Truncate(outcome.Body);
        }
        catch (Exception ex) { failure = Truncate(ex.Message); }

        flowsheetProbe = new ProbeReport(
            failure is null ? "ok" : "failed",
            sw.ElapsedMilliseconds,
            DateTime.UtcNow.ToString("o"),
            failure);
        if (failure is not null)
            app.Logger.LogError("flowsheet probe FAILED after {ElapsedMs}ms: {Error}", sw.ElapsedMilliseconds, failure);
        else
            app.Logger.LogInformation("flowsheet probe ok in {ElapsedMs}ms", sw.ElapsedMilliseconds);
    });

    static string Truncate(string s) => s.Length > 400 ? s[..400] + "…" : s;
}

// One status call answers: is it up, what engine version, what templates (FR-007).
app.MapGet("/health", () =>
{
    var (found, version, supported) = ProbeDwsim();
    StartFlowsheetProbe();
    return Results.Ok(new
    {
        ok = found,
        dwsimPath,
        dwsimFound = found,
        dwsimVersion = version,
        buildRef,
        supportedRange = SupportedRange,
        versionSupported = supported,
        // ISK-104 — did the WORKER build a flowsheet on this image? pending | ok | failed.
        flowsheetProbe,
        templatesPath,
        templates = ListTemplateIds(),
        maxConcurrent,
        maxEvaluations = 30,     // /optimize budget cap (runner-api-v2.md)
        maxTimeoutSeconds = 600, // per-evaluation timeoutSeconds cap
        hint = found ? null :
            $"DWSIM not found at '{dwsimPath}'. Install DWSIM (https://dwsim.org) and set DWSIM_PATH " +
            "to its install directory (on-prem: mount the install at /opt/dwsim).",
    });
})
    .WithTags("Health")
    .WithSummary("Readiness, engine identity and templates in one call.")
    .WithDescription("The only route never gated by RUNNER_API_KEY.")
    .Produces<HealthResponse>(StatusCodes.Status200OK);

// /health keeps the bare-id readiness array (curated only, spec-001 contract);
// GET /templates is the object-shaped listing (runner-api-v2.md).
string?[] ListTemplateIds() =>
    Directory.Exists(templatesPath)
        ? Directory.EnumerateFiles(templatesPath, "*.dwxmz")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray()
        : [];

app.MapGet("/templates", () => Results.Ok(userTemplates.List().Select(t => new
{
    id = t.Id,
    source = t.Source,
    createdUtc = t.CreatedUtc,
    solvedAtSave = t.SolvedAtSave,
})))
    .WithTags("Templates")
    .WithSummary("List every template, curated and user-saved.")
    .WithDescription("The templates array on /health is curated ids only; this is the complete list.")
    .Produces<List<TemplateListItem>>(StatusCodes.Status200OK);

app.MapDelete("/templates/{id}", (string id) =>
{
    if (string.IsNullOrEmpty(id) || !templateIdPattern.IsMatch(id))
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "template id must match ^[A-Za-z0-9._-]+$");
    if (userTemplates.CuratedExists(id))
        return ErrorResult(StatusCodes.Status403Forbidden, "TEMPLATE_READONLY",
            $"'{id}' is a curated template and cannot be deleted");
    if (!userTemplates.UserExists(id))
        return ErrorResult(StatusCodes.Status404NotFound, "TEMPLATE_NOT_FOUND", $"unknown template '{id}'");
    // No cache purge needed: solve cache keys carry the template file mtime,
    // and ResolveTemplate 404s before any cache lookup once the file is gone.
    userTemplates.Delete(id);
    return Results.NoContent();
})
    .WithTags("Templates")
    .WithSummary("Delete a user template.")
    .WithDescription("User templates only. A curated id is 403 TEMPLATE_READONLY, never a silent no-op.")
    .Produces(StatusCodes.Status204NoContent)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

// GET /templates/{id}/file — stream the .dwxmz bytes (spec 011 Cut 3, D2 option a).
// The iskra-app uses this to pull a freshly-saved user template into its Postgres
// flow_templates table (the SaaS system of record), then DELETEs the runner-side
// copy. Works for curated templates too so the app can mirror a reference plant
// into a project's saved set if desired.
app.MapGet("/templates/{id}/file", (string id) =>
{
    if (string.IsNullOrEmpty(id) || !templateIdPattern.IsMatch(id))
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "template id must match ^[A-Za-z0-9._-]+$");
    var userPath = userTemplates.UserTemplateFile(id);
    var curatedPath = Path.Combine(userTemplates.CuratedTemplatesPath, id + ".dwxmz");
    var path = File.Exists(userPath) ? userPath
             : File.Exists(curatedPath) ? curatedPath
             : null;
    if (path is null)
        return ErrorResult(StatusCodes.Status404NotFound, "TEMPLATE_NOT_FOUND", $"unknown template '{id}'");
    var bytes = File.ReadAllBytes(path);
    return Results.File(bytes, "application/octet-stream", $"{id}.dwxmz");
})
    .WithTags("Templates")
    .WithSummary("Download a template as raw .dwxmz bytes.")
    .WithDescription("Works for curated and user templates. Used to pull a saved flowsheet into a caller-side store.")
    .Produces(StatusCodes.Status200OK, typeof(byte[]), "application/octet-stream")
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

// ── engine catalog (002: FR-CAT-001..004) ──────────────────────────────────

app.MapGet("/catalog/compounds", (CancellationToken ct) => CatalogSection("compounds", ct))
    .WithTags("Catalog")
    .WithSummary("Compounds this engine can resolve by name.")
    .WithDescription("Cached per engine version. The source for the compounds array of a document.")
    .Produces<CompoundsResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
app.MapGet("/catalog/property-packages", (CancellationToken ct) => CatalogSection("propertyPackages", ct))
    .WithTags("Catalog")
    .WithSummary("Thermodynamic property packages this engine offers.")
    .WithDescription("The source for propertyPackage. Note that ids and display names differ.")
    .Produces<PropertyPackagesResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
app.MapGet("/catalog/unit-op-types", (CancellationToken ct) => CatalogSection("unitOpTypes", ct))
    .WithTags("Catalog")
    .WithSummary("Port and parameter schema per unit-op wire type.")
    .WithDescription("The source for legal type, port and parameters names in a document. Do not hardcode them.")
    .Produces<UnitOpTypesResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
// 099 FR-001 / 034 FR-020 — what the ENGINE declares, versus what this runner exposes. A section of
// the already version-keyed catalog payload, so it inherits its cache: no new worker mode, no second
// cache to go stale against the first.
app.MapGet("/catalog/engine-inventory", (CancellationToken ct) => CatalogSection("engineInventory", ct))
    .WithTags("Catalog")
    .WithSummary("What the ENGINE declares, versus what this runner exposes.")
    .WithDescription("An exposedAs of null means DWSIM has the unit op and this runner has no wire type for it.")
    .Produces<EngineInventoryResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
// The unit vocabulary this runner ACCEPTS. Served straight from `DocumentValidator` rather than
// through the worker catalog, because that dictionary is the thing that accepts or rejects a unit —
// anything else would be a second opinion about the first — and the worker cannot see this assembly.
//
// It exists so iskra's `RUNNER_UNITS` can be CHECKED. That table is this one transcribed into
// TypeScript and nothing compared them: app stricter drops a value before it ships, runner stricter
// refuses it with INVALID_UNIT after it does, and both surface far from the edit that caused them.
app.MapGet("/catalog/units", () =>
{
    var (_, probedVersion, _) = ProbeDwsim();
    return Results.Json(new { engineVersion = probedVersion, units = DocumentValidator.UnitVocabulary });
})
    .WithTags("Catalog")
    .WithSummary("The unit vocabulary this runner accepts.")
    .WithDescription("Served from the dictionary that does the accepting, so a client copy can be checked rather than trusted. Available even when the engine is not.")
    .Produces<UnitsResponse>(StatusCodes.Status200OK);

async Task<IResult> CatalogSection(string section, CancellationToken ct)
{
    var (status, fullCatalog) = await GetCatalogAsync(ct);
    if (status != StatusCodes.Status200OK)
        return Results.Content(fullCatalog, "application/json", statusCode: status);

    using var doc = JsonDocument.Parse(fullCatalog);
    using var buffer = new MemoryStream();
    using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
    {
        w.WriteStartObject();
        w.WritePropertyName("engineVersion");
        if (doc.RootElement.TryGetProperty("engineVersion", out var ev)) ev.WriteTo(w);
        else w.WriteNullValue();
        w.WritePropertyName(section);
        if (doc.RootElement.TryGetProperty(section, out var sec)) sec.WriteTo(w);
        else { w.WriteStartArray(); w.WriteEndArray(); }
        w.WriteEndObject();
    }
    return Results.Content(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), "application/json");
}

async Task<(int Status, string Body)> GetCatalogAsync(CancellationToken ct)
{
    var (_, probedVersion, _) = ProbeDwsim();
    var versionKey = probedVersion ?? "unknown";
    if (catalogJson is not null && catalogVersionKey == versionKey)
        return (StatusCodes.Status200OK, catalogJson);

    await catalogLock.WaitAsync(ct);
    try
    {
        if (catalogJson is not null && catalogVersionKey == versionKey)
            return (StatusCodes.Status200OK, catalogJson);

        var run = await SpawnWorkerAsync(new { mode = "catalog" }, TimeSpan.FromSeconds(defaultTimeout), ct, gated: true);
        if (run.ExitCode != 0)
        {
            app.Logger.LogWarning("catalog fetch failed (exit {Code}): {Stderr}", run.ExitCode, run.Stderr);
            return (StatusCodes.Status503ServiceUnavailable,
                ErrorBody("ENGINE_UNAVAILABLE",
                    "the simulation engine is unavailable — the catalog cannot be served; check /health"));
        }
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(run.Stdout)!;
            catalogJson = node.ToJsonString();
            catalogVersionKey = versionKey;
            try { catalogModel = CatalogModel.Parse(catalogJson); }
            catch { catalogModel = null; }   // structural validation best-effort; degrades gracefully
            return (StatusCodes.Status200OK, catalogJson);
        }
        catch (JsonException)
        {
            return (StatusCodes.Status503ServiceUnavailable,
                ErrorBody("ENGINE_UNAVAILABLE", "catalog worker returned an invalid response"));
        }
    }
    finally
    {
        catalogLock.Release();
    }
}

// ── flowsheet pipelines (002: validate + build-solve) ─────────────────────
// Structural validation runs in-process against the cached catalog; semantic
// validation and build-solve go through the worker (gated + cached per FR-VAL-002,
// FR-BUILD-001..005). Structural issues short-circuit semantic — a structurally
// invalid document is never sent to the engine. Both routes spawn the worker
// only when their document passes structural checks.

async Task<CatalogModel> GetCatalogModelAsync(CancellationToken ct)
{
    if (catalogModel is not null) return catalogModel;
    var (status, body) = await GetCatalogAsync(ct);
    if (status != StatusCodes.Status200OK)
        throw new InvalidOperationException($"catalog unavailable: HTTP {status}");
    return catalogModel ?? throw new InvalidOperationException("catalog parsed but the model is empty");
}

app.MapPost("/flowsheets/validate", async (ValidateRequest req, HttpContext http, CancellationToken ct) =>
{
    if (RequireDocument(req.Document) is { } bad) return bad;
    var documentEl = req.Document;

    var semantic = req.Semantic ?? true;

    // Structural validation against the catalog (collect-all). If the catalog
    // engine is unavailable we still run the structural checks that don't need
    // it (schema version, duplicate tags, units) — failure to fetch is silent.
    CatalogModel model;
    try { model = await GetCatalogModelAsync(ct); }
    catch { model = new CatalogModel(); }

    var structuralIssues = DocumentValidator.ValidateStructural(documentEl, model);
    if (structuralIssues.Any(i => i.Severity == "error"))
    {
        var issuesOut = structuralIssues.Select(i => new
        {
            severity = i.Severity, code = i.Code, tag = i.Tag, path = i.Path, message = i.Message
        });
        return Results.Content(
            JsonSerializer.Serialize(new { valid = false, issues = issuesOut }, Program.JsonOpts),
            "application/json", statusCode: StatusCodes.Status200OK);
    }

    if (!semantic)
    {
        // Structural-only pass stops here; no worker spawn, no queue slot.
        return Results.Content(
            JsonSerializer.Serialize(new { valid = true, issues = Array.Empty<object>() }, Program.JsonOpts),
            "application/json");
    }

    // Semantic validation → worker `validate` mode. Honors the same admission
    // control as solve so heavy co-pilot bursts can't starve build-solve.
    var outcome = await RunDocumentModeAsync(documentEl, "validate", TimeSpan.FromSeconds(defaultTimeout), null, ct);
    http.Response.StatusCode = outcome.Status;
    if (outcome.Status == StatusCodes.Status429TooManyRequests) http.Response.Headers.RetryAfter = "5";
    return Results.Content(outcome.Body, "application/json", statusCode: outcome.Status);
})
    .WithTags("Documents")
    .WithSummary("Validate a document without solving it.")
    .WithDescription("Always 200 when the request is well-formed - validity is in the body. Structural checks are collect-all and short-circuit the semantic pass.")
    .Produces<ValidationResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

app.MapPost("/flowsheets/build-solve", async (BuildSolveRequest req, HttpContext http, CancellationToken ct) =>
{
    if (RequireDocument(req.Document) is { } bad) return bad;
    var documentEl = req.Document;

    // CLAMPED, unlike /solve which falls back to the default on an out-of-range value. Both are
    // deliberate and they differ; docs/api.md says so out loud because the asymmetry is surprising.
    var timeoutSeconds = req.TimeoutSeconds is { } to ? Math.Clamp(to, 5, 600) : 120;

    string? savePath = null;
    string? saveTemplateId = null;
    if (req.SaveAsTemplate is { } save)
    {
        if (save.Id is not { Length: > 0 } saveId || !templateIdPattern.IsMatch(saveId))
            return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
                "saveAsTemplate.id is required and must match ^[A-Za-z0-9._-]+$");
        // Spec 011 Cut 2: do NOT reject the request up-front when the store
        // isn't writable — the solve must run regardless. A missing/writable
        // store becomes a soft `template.saved:false` block after the solve.
        var overwrite = save.Overwrite ?? false;
        if (userTemplates.CuratedExists(saveId))
            return ErrorResult(StatusCodes.Status409Conflict, "TEMPLATE_NAME_CONFLICT",
                $"'{saveId}' is a curated template name; choose another id");
        if (userTemplates.UserExists(saveId) && !overwrite)
            return ErrorResult(StatusCodes.Status409Conflict, "TEMPLATE_NAME_CONFLICT",
                $"a template named '{saveId}' already exists; pass overwrite:true to replace it");
        saveTemplateId = saveId;
        // Pre-011: savePath was null when Writable was false, which suppressed
        // the worker save entirely. Now always attempt — the worker swallows
        // IO errors (Modes.cs) and the API reports saved:false when the file
        // isn't on disk after the solve.
        savePath = userTemplates.UserTemplateFile(saveId);
    }

    // Structural validation must pass before the engine sees the document.
    CatalogModel model;
    try { model = await GetCatalogModelAsync(ct); }
    catch { model = new CatalogModel(); }
    var structuralIssues = DocumentValidator.ValidateStructural(documentEl, model);
    if (structuralIssues.Any(i => i.Severity == "error"))
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        var issuesOut = structuralIssues.Select(i => new
        {
            severity = i.Severity, code = i.Code, tag = i.Tag, path = i.Path, message = i.Message
        });
        return Results.Json(new { error = "DOCUMENT_INVALID", issues = issuesOut }, statusCode: StatusCodes.Status400BadRequest);
    }

    // Cache + queue + spawn for build-solve. Save requests bypass the cache
    // lookup — the persistence side effect must actually run.
    var cacheKey = ResultCache.KeyForDocument(documentEl, catalogVersionKey ?? "unknown");
    if (saveTemplateId is null && cache.TryGet(cacheKey, out var cached))
        return Results.Content(cached, "application/json");

    var outcome = await RunDocumentModeAsync(documentEl, "build-solve", TimeSpan.FromSeconds(timeoutSeconds), savePath, ct);
    if (outcome.Status == StatusCodes.Status429TooManyRequests)
        http.Response.Headers.RetryAfter = "5";

    var body = outcome.Body;
    if (outcome.Status == StatusCodes.Status200OK && saveTemplateId is not null)
    {
        // Spec 011 Cut 2: the save is best-effort. If the worker wrote the
        // file, report saved:true + write the provenance sidecar. If it
        // didn't (read-only store, IO error swallowed in Modes.cs), report a
        // soft saved:false block — never a 500 over a directory-write issue.
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(body)!;
            var converged = node["converged"]?.GetValue<bool>() ?? false;
            var saved = File.Exists(savePath);
            if (saved)
            {
                userTemplates.WriteSidecar(saveTemplateId, documentEl, solvedAtSave: converged);
                node["template"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = saveTemplateId,
                    ["source"] = "user",
                    ["saved"] = true,
                };
            }
            else
            {
                node["template"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = saveTemplateId,
                    ["source"] = "user",
                    ["saved"] = false,
                    ["reason"] = userTemplates.Writable ? "WRITE_FAILED" : "STORE_UNAVAILABLE",
                };
            }
            body = node.ToJsonString();
        }
        catch (JsonException) { /* body already validated by MinifyOrPassThrough */ }
    }
    if (outcome.Status == StatusCodes.Status200OK && saveTemplateId is null)
        cache.Set(cacheKey, body);   // save requests are never cache-served (the side effect must run)
    return Results.Content(body, "application/json", statusCode: outcome.Status);
})
    .WithTags("Documents")
    .WithSummary("Build a document into a flowsheet and solve it.")
    .WithDescription("Optionally saves it as a template. A converged of false is still a 200. A failed save is reported as template.saved false, never as a failed solve.")
    .Produces<BuildSolveResponse>(StatusCodes.Status200OK)
    .Produces<DocumentErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
    .Produces<DocumentErrorResponse>(StatusCodes.Status422UnprocessableEntity)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
    .Produces<ErrorResponse>(StatusCodes.Status500InternalServerError)
    .Produces<ErrorResponse>(StatusCodes.Status504GatewayTimeout);

// Flash calculation without a flowsheet (US4, FR-FLASH): thermodynamics run
// in the worker's `flash` mode; the route only rejects structurally hopeless
// requests (bad flashType/spec pairing) before paying for a process spawn.
app.MapPost("/flash", async (FlashRequestDto req, HttpContext http, CancellationToken ct) =>
{
    // Re-serialized to a JsonElement because the worker is handed the request verbatim and
    // FlashPrecheck reads it structurally. Binding buys the schema and the 400-on-garbage;
    // the wire payload the worker sees is unchanged.
    var flashEl = JsonSerializer.SerializeToElement(req, Program.JsonOpts);

    if (FlashPrecheck(flashEl) is { } issue)
        return ErrorResult(StatusCodes.Status400BadRequest, "FLASH_INVALID", issue);

    var cacheKey = ResultCache.KeyForDocument(flashEl, "flash|" + (catalogVersionKey ?? "unknown"));
    if (cache.TryGet(cacheKey, out var cached))
        return Results.Content(cached, "application/json");

    var outcome = await RunDocumentModeAsync(flashEl, "flash", TimeSpan.FromSeconds(defaultTimeout), null, ct, payloadKey: "flash");
    if (outcome.Status == StatusCodes.Status429TooManyRequests)
        http.Response.Headers.RetryAfter = "5";
    if (outcome.Status == StatusCodes.Status200OK)
        cache.Set(cacheKey, outcome.Body);
    return Results.Content(outcome.Body, "application/json", statusCode: outcome.Status);
})
    .WithTags("Flash")
    .WithSummary("Single-point flash. No flowsheet involved.")
    .WithDescription("TP, PH, PS, PVF or TVF. TH and TS are unsupported - they kill the worker process. On a pure compound TVF is accepted but insensitive to vaporFraction; prefer PVF.")
    .Produces<FlashResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
    .Produces<ErrorResponse>(StatusCodes.Status500InternalServerError)
    .Produces<ErrorResponse>(StatusCodes.Status504GatewayTimeout);

static string? FlashPrecheck(JsonElement flash)
{
    bool Has(string name) => flash.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object;
    if (!flash.TryGetProperty("compounds", out var comps) || comps.ValueKind != JsonValueKind.Array
        || comps.GetArrayLength() == 0)
        return "compounds must be a non-empty array";
    if (!flash.TryGetProperty("composition", out var compo) || compo.ValueKind != JsonValueKind.Object)
        return "composition is required";
    var flashType = flash.TryGetProperty("flashType", out var ftEl) && ftEl.ValueKind == JsonValueKind.String
        ? ftEl.GetString()!.ToUpperInvariant() : "(missing)";
    return flashType switch
    {
        "TP" => Has("temperature") && Has("pressure") ? null : "TP flash requires temperature and pressure specs",
        "PH" => Has("pressure") && Has("enthalpy") ? null : "PH flash requires pressure and enthalpy specs",
        "PS" => Has("pressure") && Has("entropy") ? null : "PS flash requires pressure and entropy specs",
        // 120 US2 — measured additions. TH/TS are NOT here: they crash the engine (hard
        // worker death under STEAM and PR, measured 2026-08-01) — fixture records the
        // verdict. PSF/TSF: solids ledgered will-not-yet. NOTE this validator duplicates
        // the worker's switch by design (API answers in 50 ms without spawning a worker) —
        // extend BOTH or the API vetoes the worker, which is exactly the bug hunt that
        // produced this comment.
        "PVF" => Has("pressure") && Has("vaporFraction") ? null : "PVF flash requires pressure and vaporFraction specs",
        "TVF" => Has("temperature") && Has("vaporFraction") ? null : "TVF flash requires temperature and vaporFraction specs",
        _ => $"flashType '{flashType}' not supported (TP|PH|PS|PVF|TVF)",
    };
}

// PFD rendering (US6, FR-PFD): worker `pfd` mode returns {pngBase64}; the
// API decodes to binary image/png. Render failures stay JSON (422).
app.MapPost("/flowsheets/pfd", async (PfdRequest req, HttpContext http, CancellationToken ct) =>
{
    if (RequireDocument(req.Document) is { } bad) return bad;

    var outcome = await RunDocumentModeAsync(req.Document, "pfd", TimeSpan.FromSeconds(defaultTimeout), null, ct);
    return PngOrError(http, outcome);
})
    .WithTags("Documents")
    .WithSummary("Render a document as a PNG diagram.")
    .WithDescription("Auto-layout is applied when object positions are absent. Failures stay JSON.")
    .Produces(StatusCodes.Status200OK, typeof(byte[]), "image/png")
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

app.MapGet("/templates/{id}/pfd.png", async (string id, HttpContext http, CancellationToken ct) =>
{
    var (templateFile, error) = ResolveTemplate(id);
    if (error is not null) return error;

    var outcome = await RunCaseAsync(id, templateFile!, [], TimeSpan.FromSeconds(defaultTimeout), ct, mode: "pfd");
    return PngOrError(http, outcome);
})
    .WithTags("Templates")
    .WithSummary("Render a stored template as a PNG diagram.")
    .WithDescription("Cached by template mtime. Failures stay JSON.")
    .Produces(StatusCodes.Status200OK, typeof(byte[]), "image/png")
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

static IResult PngOrError(HttpContext http, CaseOutcome outcome)
{
    if (outcome.Status == StatusCodes.Status429TooManyRequests)
        http.Response.Headers.RetryAfter = "5";
    if (outcome.Status != StatusCodes.Status200OK)
        return Results.Content(outcome.Body, "application/json", statusCode: outcome.Status);
    try
    {
        var b64 = System.Text.Json.Nodes.JsonNode.Parse(outcome.Body)?["pngBase64"]?.GetValue<string>();
        if (b64 is { Length: > 0 })
            return Results.Bytes(Convert.FromBase64String(b64), "image/png");
    }
    catch (Exception) { /* fall through to RENDER_FAILED */ }
    return Results.Content(ErrorBody("RENDER_FAILED", "worker returned no image data"),
        "application/json", statusCode: StatusCodes.Status422UnprocessableEntity);
}

// Document-mode worker spawn: writes {mode, document|flash, savePath?}
// and maps exit codes (reusing the same concurrency gate + admission control as /solve).
// Worker payload shapes mirror the FakeWorker's expectations.
async Task<CaseOutcome> RunDocumentModeAsync(JsonElement document, string mode, TimeSpan timeout, string? savePath, CancellationToken ct, string payloadKey = "document", List<PropertyOverride>? overrides = null)
{
    var solveId = Guid.NewGuid().ToString("N")[..8];
    var clock = Stopwatch.StartNew();
    void LogOutcome(string outcome, bool cacheHit = false) => app.Logger.LogInformation(
        "docmode {SolveId}: mode={Mode} outcome={Outcome} cacheHit={CacheHit} elapsedMs={ElapsedMs}",
        solveId, mode, outcome, cacheHit, clock.ElapsedMilliseconds);

    if (Interlocked.Increment(ref admitted) > maxAdmitted)
    {
        Interlocked.Decrement(ref admitted);
        LogOutcome("QUEUE_FULL");
        return new(StatusCodes.Status429TooManyRequests,
            ErrorBody("QUEUE_FULL", $"queue is full ({maxAdmitted} requests admitted); retry shortly"));
    }

    var jobFile = Path.Combine(Path.GetTempPath(), $"dwsim-job-{Guid.NewGuid():N}.json");
    try
    {
        var job = new Dictionary<string, object?> { ["mode"] = mode, [payloadKey] = document };
        if (savePath is not null) job["savePath"] = savePath;
        if (overrides is { Count: > 0 }) job["overrides"] = overrides;   // 120 US5 document cases
        await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(job, Program.JsonOpts), ct);

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
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                proc.Kill(entireProcessTree: true);
                LogOutcome("SOLVE_TIMEOUT");
                return new(StatusCodes.Status504GatewayTimeout,
                    ErrorBody("SOLVE_TIMEOUT", $"solve timed out after {timeout.TotalSeconds}s"));
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                app.Logger.LogWarning("docmode {SolveId} worker exit {Code}, stderr: {Stderr}",
                    solveId, proc.ExitCode, stderr);

            switch (proc.ExitCode)
            {
                case 0:
                    LogOutcome("ok");
                    return new(StatusCodes.Status200OK, MinifyOrPassThrough(stdout, "WORKER_CRASH", "worker returned an invalid response"));
                case 2:
                    LogOutcome("INVALID_INPUT");
                    return new(StatusCodes.Status400BadRequest,
                        WorkerErrorOrDefault(stdout, "INVALID_REQUEST", "worker rejected the request input"));
                case 4:
                    LogOutcome("BUILD_FAILED");
                    return new(StatusCodes.Status422UnprocessableEntity,
                        WorkerErrorOrDefault(stdout, "BUILD_FAILED", "engine rejected construction"));
                case 5:
                    LogOutcome("RENDER_FAILED");
                    return new(StatusCodes.Status422UnprocessableEntity,
                        WorkerErrorOrDefault(stdout, "RENDER_FAILED", "PFD rendering failed"));
                case 6:   // FND-0103/0104 — the worker's OWN deadline fired. Same taxonomy as the
                          // API-side kill above: the caller asked for a solve and did not get one
                          // in time, and which watchdog noticed is not their problem.
                    LogOutcome("SOLVE_TIMEOUT");
                    return new(StatusCodes.Status504GatewayTimeout,
                        WorkerErrorOrDefault(stdout, "SOLVE_TIMEOUT", "worker exceeded its own deadline"));
                default:
                    app.Logger.LogError("docmode worker crashed (exit {Code}) for mode {Mode}: {Stderr}",
                        proc.ExitCode, mode, stderr);
                    LogOutcome("WORKER_CRASH");
                    return new(StatusCodes.Status500InternalServerError,
                        ErrorBody("WORKER_CRASH", "simulation worker failed unexpectedly"));
            }
        }
        finally { gate.Release(); }
    }
    finally
    {
        Interlocked.Decrement(ref admitted);
        try { File.Delete(jobFile); } catch { }
    }
}

static string MinifyOrPassThrough(string stdout, string fallbackCode, string fallbackMessage)
{
    try
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(stdout)!;
        return node.ToJsonString();
    }
    catch (JsonException) { return ErrorBody(fallbackCode, fallbackMessage); }
}

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
})
    .WithTags("Templates")
    .WithSummary("Object inventory of a template, without solving it.")
    .WithDescription("Discover legal /solve override targets here: settableProperties is the property vocabulary for that object.")
    .Produces<ObjectsResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

app.MapPost("/solve", async (SolveRequestDto req, HttpContext http, CancellationToken ct) =>
{
    var (templateFile, error) = ResolveTemplate(req.TemplateId);
    if (error is not null) return error;

    var timeout = TimeSpan.FromSeconds(req.TimeoutSeconds is > 0 and <= 600 ? req.TimeoutSeconds.Value : defaultTimeout);
    var outcome = await RunCaseAsync(req.TemplateId, templateFile!, req.Overrides ?? [], timeout, ct);

    if (outcome.Status == StatusCodes.Status429TooManyRequests)
        http.Response.Headers.RetryAfter = "5";
    return Results.Content(outcome.Body, "application/json", statusCode: outcome.Status);
})
    .WithTags("Solving")
    .WithSummary("Solve a stored template with optional overrides.")
    .WithDescription("A converged of false is a 200, not an error. An out-of-range timeoutSeconds falls back to the server default rather than clamping.")
    .Produces<SolveResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
    .Produces<ErrorResponse>(StatusCodes.Status500InternalServerError)
    .Produces<ErrorResponse>(StatusCodes.Status504GatewayTimeout);

app.MapPost("/compare", async (CompareRequestDto req, HttpContext http, CancellationToken ct) =>
{
    // 120 US5 — templateId XOR document. A sweep is a compare whose cases the caller
    // expanded from a range; there is deliberately no /sweep endpoint.
    var hasDoc = req.Document is { ValueKind: JsonValueKind.Object };
    if (hasDoc && !string.IsNullOrEmpty(req.TemplateId))
        return ErrorResult(StatusCodes.Status400BadRequest, "CONFLICTING_PARAMETERS",
            "provide templateId or document, not both");
    if (!hasDoc && string.IsNullOrEmpty(req.TemplateId))
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "provide templateId or document");

    string? templateFile = null;
    if (!hasDoc)
    {
        (templateFile, var error) = ResolveTemplate(req.TemplateId!);
        if (error is not null) return error;
    }

    if (req.Cases is not { Count: >= 1 and <= 25 })
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "cases must contain between 1 and 25 entries");

    var compareTimeout = TimeSpan.FromSeconds(req.TimeoutSeconds is > 0 and <= 600 ? req.TimeoutSeconds.Value : defaultTimeout);
    if (WorkBudgetRefusal(req.Cases.Count, compareTimeout) is { } compareRefusal)
        return compareRefusal;

    // Rough whole-request admission check; per-case races still degrade to a
    // per-case QUEUE_FULL error rather than failing the set.
    if (Volatile.Read(ref admitted) + req.Cases.Count > maxAdmitted)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.Content(ErrorBody("QUEUE_FULL", "not enough queue capacity for this case set; retry shortly"),
            "application/json", statusCode: StatusCodes.Status429TooManyRequests);
    }

    var timeout = compareTimeout;

    // Fan out concurrently — each case flows through the same semaphore + cache
    // as /solve (or build-solve for document cases), so results are identical
    // across endpoints (FR-008).
    var caseTasks = req.Cases.ToDictionary(
        kv => kv.Key,
        kv => hasDoc
            ? RunDocumentCaseAsync(req.Document!.Value, kv.Value ?? [], timeout, ct)
            : RunCaseAsync(req.TemplateId!, templateFile!, kv.Value ?? [], timeout, ct));
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
})
    .WithTags("Solving")
    .WithSummary("Fan out 1 to 25 named cases over one template or document.")
    .WithDescription("Per-case error isolation: the envelope is 200 and each value is either a solve result or an error object. A sweep is a compare whose cases you expanded from a range.")
    .Produces<CompareResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

// Single-variable optimization (US7, FR-OPT): golden-section over the normal
// solve pipeline — every evaluation is an ordinary cached /solve case, run
// sequentially (the search is inherently sequential).
app.MapPost("/optimize", async (OptimizeRequestDto req, HttpContext http, CancellationToken ct) =>
{
    // 120 US5 — templateId XOR document, same rule as /compare.
    var hasDoc = req.Document is { ValueKind: JsonValueKind.Object };
    if (hasDoc && !string.IsNullOrEmpty(req.TemplateId))
        return ErrorResult(StatusCodes.Status400BadRequest, "CONFLICTING_PARAMETERS",
            "provide templateId or document, not both");
    if (!hasDoc && string.IsNullOrEmpty(req.TemplateId))
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "provide templateId or document");

    string? templateFile = null;
    if (!hasDoc)
    {
        (templateFile, var error) = ResolveTemplate(req.TemplateId!);
        if (error is not null) return error;
    }

    if (req.Variable is not { } variable || string.IsNullOrEmpty(variable.Object) || string.IsNullOrEmpty(variable.Property))
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "variable { object, property, min, max } is required");
    if (!(variable.Min < variable.Max) || !double.IsFinite(variable.Min) || !double.IsFinite(variable.Max))
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "variable.min must be strictly less than variable.max");
    if (req.Objective is not { } objective || string.IsNullOrEmpty(objective.Object) || string.IsNullOrEmpty(objective.Property))
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "objective { object, property, direction } is required");
    if (objective.Direction is not ("minimize" or "maximize"))
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "objective.direction must be 'minimize' or 'maximize'");
    if (req.MaxEvaluations is < 2 or > 30)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "maxEvaluations must be between 2 and 30");
    var maxEvaluations = req.MaxEvaluations ?? 20;
    var tolerance = req.Tolerance is > 0 ? req.Tolerance.Value : (variable.Max - variable.Min) * 1e-3;
    var timeout = TimeSpan.FromSeconds(req.TimeoutSeconds is > 0 and <= 600 ? req.TimeoutSeconds.Value : defaultTimeout);

    // FND-0029 — refuse an over-budget search UP FRONT rather than accepting it and running.
    // Golden section is sequential, so this request holds a solve slot for up to
    // maxEvaluations x timeout; on the pre-fix defaults that is 30 x 600 s = 5 hours, and four
    // such requests starve every other caller at MAX_CONCURRENT_SOLVES=4.
    if (WorkBudgetRefusal(maxEvaluations, timeout) is { } optimizeRefusal)
        return optimizeRefusal;

    var outcome = await Optimizer.GoldenSectionAsync(
        variable.Min, variable.Max, tolerance, maxEvaluations,
        maximize: objective.Direction == "maximize",
        evaluate: async value =>
        {
            var overrides = new List<PropertyOverride>
                { new(variable.Object, variable.Property, value, variable.Unit) };
            var solve = hasDoc
                ? await RunDocumentCaseAsync(req.Document!.Value, overrides, timeout, ct)
                : await RunCaseAsync(req.TemplateId!, templateFile!, overrides, timeout, ct);
            if (solve.Status != StatusCodes.Status200OK)
                return new OptEvaluation(value, null, false, solve.Body);
            var converged = false;
            try
            {
                converged = JsonSerializer.Deserialize<JsonElement>(solve.Body)
                    .TryGetProperty("converged", out var cEl) && cEl.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { }
            var objectiveValue = converged
                ? Optimizer.ExtractObjective(solve.Body, objective.Object, objective.Property)
                : null;
            return new OptEvaluation(value, objectiveValue, converged && objectiveValue is not null, solve.Body);
        });

    if (outcome.Best is null)
        return ErrorResult(StatusCodes.Status422UnprocessableEntity, "OPTIMIZATION_INFEASIBLE",
            $"no evaluation converged with a readable objective '{objective.Object}.{objective.Property}' "
            + $"in [{variable.Min}, {variable.Max}] after {outcome.Evaluations.Count} evaluations");

    // best.result is the raw SolveResult body — splice it in as JSON.
    using var buffer = new MemoryStream();
    using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
    {
        w.WriteStartObject();
        w.WritePropertyName("best");
        w.WriteStartObject();
        w.WriteNumber("value", outcome.Best.Value);
        if (outcome.Best.ObjectiveValue is double bo) w.WriteNumber("objectiveValue", bo);
        w.WritePropertyName("result");
        using (var doc = JsonDocument.Parse(outcome.Best.Body)) doc.RootElement.WriteTo(w);
        w.WriteEndObject();
        w.WritePropertyName("evaluations");
        w.WriteStartArray();
        foreach (var e in outcome.Evaluations)
        {
            w.WriteStartObject();
            w.WriteNumber("value", e.Value);
            if (e.ObjectiveValue is double ov) w.WriteNumber("objectiveValue", ov);
            else w.WriteNull("objectiveValue");
            w.WriteBoolean("converged", e.Converged);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("converged", outcome.Converged);
        w.WriteString("stoppedReason", outcome.StoppedReason);
        w.WriteEndObject();
    }
    return Results.Content(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), "application/json");
})
    .WithTags("Solving")
    .WithSummary("Golden-section search over one variable.")
    .WithDescription("Every evaluation is an ordinary cached solve, run sequentially. Worst-case wall time is maxEvaluations times timeoutSeconds.")
    .Produces<OptimizeResponse>(StatusCodes.Status200OK)
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
    .Produces<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)
    .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);

app.Run("http://0.0.0.0:8080");

// ── helpers ────────────────────────────────────────────────────────────────

// Curated templates win on id collision (saves reject curated names, so a
// collision can't be created through the API). User templates join every
// spec-001 pipeline — /solve, /compare, /templates/{id}/objects (US3).
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
    {
        var userFile = userTemplates.UserTemplateFile(id);
        if (File.Exists(userFile)) return (userFile, null);
        return (null, ErrorResult(StatusCodes.Status404NotFound, "TEMPLATE_NOT_FOUND",
            $"unknown template '{id}'"));
    }

    return (file, null);
}

// FND-0029 — the one aggregate work bound, shared by /compare and /optimize because they are
// the only two routes that expand one request into many solves. Returns null when the request
// fits its budget. Deliberately an UP-FRONT refusal rather than a mid-flight stopwatch: every
// case is already bounded by `perCase`, so cases x perCase is the whole worker-slot exposure,
// and refusing before the first spawn is the difference between a 400 and four hours of held
// capacity. Queue-wait time is not counted (it holds no worker), only the work commissioned.
IResult? WorkBudgetRefusal(int cases, TimeSpan perCase)
{
    var requested = cases * perCase.TotalSeconds;
    if (requested <= maxWorkSeconds) return null;
    return ErrorResult(StatusCodes.Status400BadRequest, "WORK_BUDGET_EXCEEDED",
        $"{cases} cases x {perCase.TotalSeconds:0}s = {requested:0}s of solve time exceeds this runner's "
        + $"per-request budget of {maxWorkSeconds}s; lower timeoutSeconds or the case count");
}

static IResult ErrorResult(int status, string error, string message) =>
    Results.Json(new { error, message }, statusCode: status);

// An absent `document` binds to default(JsonElement), whose ValueKind is Undefined — so the
// "is it there" and "is it an object" checks are one test. Returns null when the document is
// usable, which is why call sites read `if (RequireDocument(x) is { } bad) return bad;`.
static IResult? RequireDocument(JsonElement document) =>
    document.ValueKind == JsonValueKind.Object
        ? null
        : ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
            "document is required and must be a JSON object");

static string ErrorBody(string error, string message) =>
    JsonSerializer.Serialize(new { error, message });

// One solve case end-to-end: cache → admission control → worker process →
// 120 US5 — one DOCUMENT case: build-solve with per-case overrides, cached like every
// other solve (KeyForDocument + canonicalized overrides), so document compares and
// optimizations hit the same cache a repeated build-solve would.
async Task<CaseOutcome> RunDocumentCaseAsync(JsonElement document,
    List<PropertyOverride> overrides, TimeSpan timeout, CancellationToken ct)
{
    var overrideKey = string.Join("|", overrides.Select(o => $"{o.Object} {o.Property} {o.Value} {o.Unit}"));
    var cacheKey = ResultCache.KeyForDocument(document, $"case|{overrideKey}|{catalogVersionKey ?? "unknown"}");
    if (cache.TryGet(cacheKey, out var cached))
        return new(StatusCodes.Status200OK, cached);

    var outcome = await RunDocumentModeAsync(document, "build-solve", timeout, null, ct, overrides: overrides);
    if (outcome.Status == StatusCodes.Status200OK)
        cache.Set(cacheKey, outcome.Body);
    return outcome;
}

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
                    if (converged || mode is "inspect" or "pfd")   // inventories/renders are pure functions of the template file
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

                case 5:   // PFD render failed (pfd mode only)
                    LogOutcome("RENDER_FAILED");
                    return new(StatusCodes.Status422UnprocessableEntity,
                        WorkerErrorOrDefault(stdout, "RENDER_FAILED", "PFD rendering failed"));

                case 6:   // FND-0103/0104 — the worker's own deadline fired (see the docmode twin).
                    LogOutcome("SOLVE_TIMEOUT");
                    return new(StatusCodes.Status504GatewayTimeout,
                        WorkerErrorOrDefault(stdout, "SOLVE_TIMEOUT", "worker exceeded its own deadline"));

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

// One worker process, one job, one JSON document back. ExitCode null = hard
// timeout (process killed). `gated` runs the spawn through the concurrency
// semaphore. Shared by catalog/validate/build-solve/flash/pfd; /solve keeps
// its own path in RunCaseAsync (identical mechanics plus cache/admission).
async Task<WorkerRun> SpawnWorkerAsync(object jobPayload, TimeSpan timeout, CancellationToken ct, bool gated)
{
    var jobFile = Path.Combine(Path.GetTempPath(), $"dwsim-job-{Guid.NewGuid():N}.json");
    try
    {
        await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(jobPayload, jobPayload.GetType(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), ct);

        if (gated) await gate.WaitAsync(ct);
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
                proc.Kill(entireProcessTree: true);
                return new WorkerRun(null, "", "hard timeout");
            }
            return new WorkerRun(proc.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new WorkerRun(127, "", $"failed to start worker: {ex.Message}");
        }
        finally
        {
            if (gated) gate.Release();
        }
    }
    finally
    {
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

// ISK-104 — the flowsheet-construction probe's answer, as /health reports it.
// `checkedAt`/`error` are null while `state` is "pending"; `error` carries the worker's own
// response body (or the exception message) when it is "failed", so a red probe names its cause
// without a trip to the deploy logs.
record WorkerRun(int? ExitCode, string Stdout, string Stderr);
/// <summary>A single property override applied to a named object before solving.</summary>
/// <param name="Object">An object tag from GET /templates/{id}/objects, e.g. "R-101".</param>
/// <param name="Property">
/// Must appear in that object's settableProperties, e.g. "OutletTemperature". Anything else is
/// refused with INVALID_PROPERTY.
/// </param>
/// <param name="Value">The numeric value, interpreted in <c>unit</c>.</param>
/// <param name="Unit">Omit for SI. Must be a spelling from GET /catalog/units.</param>
public record PropertyOverride(string Object, string Property, double Value, string? Unit);
record CaseOutcome(int Status, string Body);

public partial class Program
{
    // Shared camelCase serializer options for the new routes' inline payloads.
    public static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
} // WebApplicationFactory hook for tests
