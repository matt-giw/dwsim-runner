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
        buildRef,
        supportedRange = SupportedRange,
        versionSupported = supported,
        templatesPath,
        templates = ListTemplateIds(),
        maxConcurrent,
        maxEvaluations = 30,     // /optimize budget cap (runner-api-v2.md)
        maxTimeoutSeconds = 600, // per-evaluation timeoutSeconds cap
        hint = found ? null :
            $"DWSIM not found at '{dwsimPath}'. Install DWSIM (https://dwsim.org) and set DWSIM_PATH " +
            "to its install directory (on-prem: mount the install at /opt/dwsim).",
    });
});

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
})));

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
});

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
});

// ── engine catalog (002: FR-CAT-001..004) ──────────────────────────────────

app.MapGet("/catalog/compounds", (CancellationToken ct) => CatalogSection("compounds", ct));
app.MapGet("/catalog/property-packages", (CancellationToken ct) => CatalogSection("propertyPackages", ct));
app.MapGet("/catalog/unit-op-types", (CancellationToken ct) => CatalogSection("unitOpTypes", ct));
// 099 FR-001 / 034 FR-020 — what the ENGINE declares, versus what this runner exposes. A section of
// the already version-keyed catalog payload, so it inherits its cache: no new worker mode, no second
// cache to go stale against the first.
app.MapGet("/catalog/engine-inventory", (CancellationToken ct) => CatalogSection("engineInventory", ct));
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
});

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

// 213 — the unit half of `ValidateStructural`, for the three endpoints that take a document and
// have never validated one: /compare, /optimize and /flowsheets/pfd. It RUNS the existing
// validator and keeps only INVALID_UNIT, so these endpoints gain the unit refusal and not the
// twenty other structural verdicts — closing the reported hole without changing what any of them
// already accepts. Catalog failure degrades exactly as build-solve's does: an empty model still
// checks stream specs, whose vocabulary needs no catalog.
async Task<string?> DocumentUnitRefusalAsync(JsonElement document, CancellationToken ct)
{
    CatalogModel model;
    try { model = await GetCatalogModelAsync(ct); }
    catch { model = new CatalogModel(); }
    return DocumentValidator.ValidateStructural(document, model)
        .FirstOrDefault(i => i.Code == "INVALID_UNIT") is { } issue
        ? $"{issue.Path}: {issue.Message}"
        : null;
}

app.MapPost("/flowsheets/validate", async (HttpContext http, CancellationToken ct) =>
{
    // Body shape: { document, semantic: true }
    JsonElement requestBody;
    try
    {
        using var j = await JsonDocument.ParseAsync(http.Request.Body);
        requestBody = j.RootElement.Clone();
    }
    catch (JsonException)
    {
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "request body must be JSON");
    }
    if (!requestBody.TryGetProperty("document", out var documentEl) || documentEl.ValueKind != JsonValueKind.Object)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "document is required and must be a JSON object");

    var semantic = requestBody.TryGetProperty("semantic", out var semEl) && semEl.ValueKind == JsonValueKind.False ? false : true;

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
});

app.MapPost("/flowsheets/build-solve", async (HttpContext http, CancellationToken ct) =>
{
    JsonElement requestBody;
    try
    {
        using var j = await JsonDocument.ParseAsync(http.Request.Body);
        requestBody = j.RootElement.Clone();
    }
    catch (JsonException)
    {
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "request body must be JSON");
    }
    if (!requestBody.TryGetProperty("document", out var documentEl) || documentEl.ValueKind != JsonValueKind.Object)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "document is required and must be a JSON object");

    var timeoutSeconds = requestBody.TryGetProperty("timeoutSeconds", out var toEl) && toEl.ValueKind == JsonValueKind.Number
        ? Math.Clamp((int)toEl.GetInt32(), 5, 600)
        : 120;

    string? savePath = null;
    string? saveTemplateId = null;
    if (requestBody.TryGetProperty("saveAsTemplate", out var saveEl) && saveEl.ValueKind == JsonValueKind.Object)
    {
        if (!saveEl.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } saveId
            || !templateIdPattern.IsMatch(saveId))
            return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST",
                "saveAsTemplate.id is required and must match ^[A-Za-z0-9._-]+$");
        // Spec 011 Cut 2: do NOT reject the request up-front when the store
        // isn't writable — the solve must run regardless. A missing/writable
        // store becomes a soft `template.saved:false` block after the solve.
        var overwrite = saveEl.TryGetProperty("overwrite", out var ovEl) && ovEl.ValueKind == JsonValueKind.True;
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
});

// Flash calculation without a flowsheet (US4, FR-FLASH): thermodynamics run
// in the worker's `flash` mode; the route only rejects structurally hopeless
// requests (bad flashType/spec pairing) before paying for a process spawn.
app.MapPost("/flash", async (HttpContext http, CancellationToken ct) =>
{
    JsonElement flashEl;
    try
    {
        using var j = await JsonDocument.ParseAsync(http.Request.Body);
        flashEl = j.RootElement.Clone();
    }
    catch (JsonException)
    {
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "request body must be JSON");
    }
    if (flashEl.ValueKind != JsonValueKind.Object)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "flash request must be a JSON object");

    if (FlashPrecheck(flashEl) is { } issue)
        return ErrorResult(StatusCodes.Status400BadRequest, "FLASH_INVALID", issue);

    // 213 — the pairing check above proves the spec is PRESENT; this proves its unit is one the
    // engine's converter actually knows. Before the cache lookup as well as before the worker
    // spawn: a refused request must never be answerable from a cached converged result.
    if (FlashUnitRefusal(flashEl) is { } unitIssue)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_UNIT", unitIssue);

    var cacheKey = ResultCache.KeyForDocument(flashEl, "flash|" + (catalogVersionKey ?? "unknown"));
    if (cache.TryGet(cacheKey, out var cached))
        return Results.Content(cached, "application/json");

    var outcome = await RunDocumentModeAsync(flashEl, "flash", TimeSpan.FromSeconds(defaultTimeout), null, ct, payloadKey: "flash");
    if (outcome.Status == StatusCodes.Status429TooManyRequests)
        http.Response.Headers.RetryAfter = "5";
    if (outcome.Status == StatusCodes.Status200OK)
        cache.Set(cacheKey, outcome.Body);
    return Results.Content(outcome.Body, "application/json", statusCode: outcome.Status);
});

// 213 — every unit-bearing field on /flash, in one place, keyed to the quantity each one IS.
// `vaporFraction` is dimensionless and `entropy` has no measured vocabulary, so both refuse ANY
// unit rather than converting one: `RequireSi` in the worker hands whatever it is to
// `ConvertToSI`, which silently returns it unchanged.
static string? FlashUnitRefusal(JsonElement flash)
{
    foreach (var (field, kind) in new (string Field, string Kind)[]
             {
                 ("temperature", "temperature"), ("pressure", "pressure"), ("enthalpy", "enthalpy"),
                 ("entropy", "entropy"), ("vaporFraction", "dimensionless"),
             })
        if (flash.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String
            && DocumentValidator.UnitRefusal($"{field}.unit", u.GetString(), kind) is { } refusal)
            return refusal;
    return null;
}

// 213 — an override names an object and a property, never a quantity, so the union vocabulary is
// the strongest check available before the template is loaded. Shared by /solve and /compare.
static string? OverrideUnitRefusal(IEnumerable<PropertyOverride>? overrides)
{
    foreach (var o in overrides ?? [])
        if (DocumentValidator.UnitRefusal($"override {o.Object}.{o.Property}", o.Unit, null) is { } refusal)
            return refusal;
    return null;
}

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
app.MapPost("/flowsheets/pfd", async (HttpContext http, CancellationToken ct) =>
{
    JsonElement requestBody;
    try
    {
        using var j = await JsonDocument.ParseAsync(http.Request.Body);
        requestBody = j.RootElement.Clone();
    }
    catch (JsonException)
    {
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "request body must be JSON");
    }
    if (!requestBody.TryGetProperty("document", out var documentEl) || documentEl.ValueKind != JsonValueKind.Object)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_REQUEST", "document is required and must be a JSON object");

    // 213 — a PFD builds the flowsheet (no solve), so `FlowsheetBuilder.ToSi` runs and a bad
    // spelling still lands a wrongly-dimensioned value on the objects. It is drawn rather than
    // reported, which makes it the quietest of the five holes, not the least real.
    if (await DocumentUnitRefusalAsync(documentEl, ct) is { } pfdUnitIssue)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_UNIT", pfdUnitIssue);

    var outcome = await RunDocumentModeAsync(documentEl, "pfd", TimeSpan.FromSeconds(defaultTimeout), null, ct);
    return PngOrError(http, outcome);
});

app.MapGet("/templates/{id}/pfd.png", async (string id, HttpContext http, CancellationToken ct) =>
{
    var (templateFile, error) = ResolveTemplate(id);
    if (error is not null) return error;

    var outcome = await RunCaseAsync(id, templateFile!, [], TimeSpan.FromSeconds(defaultTimeout), ct, mode: "pfd");
    return PngOrError(http, outcome);
});

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
});

app.MapPost("/solve", async (SolveRequest req, HttpContext http, CancellationToken ct) =>
{
    var (templateFile, error) = ResolveTemplate(req.TemplateId);
    if (error is not null) return error;

    if (OverrideUnitRefusal(req.Overrides) is { } unitIssue)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_UNIT", unitIssue);

    var timeout = TimeSpan.FromSeconds(req.TimeoutSeconds is > 0 and <= 600 ? req.TimeoutSeconds.Value : defaultTimeout);
    var outcome = await RunCaseAsync(req.TemplateId, templateFile!, req.Overrides ?? [], timeout, ct);

    if (outcome.Status == StatusCodes.Status429TooManyRequests)
        http.Response.Headers.RetryAfter = "5";
    return Results.Content(outcome.Body, "application/json", statusCode: outcome.Status);
});

app.MapPost("/compare", async (CompareRequest req, HttpContext http, CancellationToken ct) =>
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

    // 213 — both halves of a compare carry units: the overrides that define each case, and the
    // document form, which reaches FlowsheetBuilder without ever passing ValidateStructural.
    foreach (var (name, overrides) in req.Cases)
        if (OverrideUnitRefusal(overrides) is { } caseUnitIssue)
            return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_UNIT",
                $"case '{name}': {caseUnitIssue}");
    if (hasDoc && await DocumentUnitRefusalAsync(req.Document!.Value, ct) is { } docUnitIssue)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_UNIT", docUnitIssue);

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
});

// Single-variable optimization (US7, FR-OPT): golden-section over the normal
// solve pipeline — every evaluation is an ordinary cached /solve case, run
// sequentially (the search is inherently sequential).
app.MapPost("/optimize", async (OptimizeRequest req, HttpContext http, CancellationToken ct) =>
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
    // 213 — `variable.unit` rides every evaluation of the search, so an unrecognised spelling here
    // is not one wrong answer but a whole sweep of them, each internally consistent.
    if (DocumentValidator.UnitRefusal("variable.unit", variable.Unit, null) is { } varUnitIssue)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_UNIT", varUnitIssue);
    if (hasDoc && await DocumentUnitRefusalAsync(req.Document!.Value, ct) is { } optDocUnitIssue)
        return ErrorResult(StatusCodes.Status400BadRequest, "INVALID_UNIT", optDocUnitIssue);
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
});

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

static IResult ErrorResult(int status, string error, string message) =>
    Results.Json(new { error, message }, statusCode: status);

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

record WorkerRun(int? ExitCode, string Stdout, string Stderr);
record SolveRequest(string TemplateId, List<PropertyOverride>? Overrides, int? TimeoutSeconds);
record CompareRequest(string? TemplateId, JsonElement? Document, Dictionary<string, List<PropertyOverride>?>? Cases, int? TimeoutSeconds);
record OptimizeVariable(string Object, string Property, string? Unit, double Min, double Max);
record OptimizeObjective(string Object, string Property, string Direction);
record OptimizeRequest(string? TemplateId, JsonElement? Document, OptimizeVariable? Variable, OptimizeObjective? Objective,
    double? Tolerance, int? MaxEvaluations, int? TimeoutSeconds);
public record PropertyOverride(string Object, string Property, double Value, string? Unit);
record CaseOutcome(int Status, string Body);

public partial class Program
{
    // Shared camelCase serializer options for the new routes' inline payloads.
    public static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
} // WebApplicationFactory hook for tests
