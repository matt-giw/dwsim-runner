# dwsim-runner HTTP API

The reference for every route this service serves. Written for the two clients that
call it — `iskra-app` (`lib/simRunner.ts`) and the MCP tool surface it hosts.

**This file lives in the runner repo on purpose.** `dwsim-runner` is a submodule with its
own repo and versions independently ([SDLC §2](https://github.com/matt-giw/iskra-tech-sdlc)),
so a route change and its documentation are the same commit here and cannot be the same
commit anywhere else. The `specs/*/contracts/*.md` files in the iskra monorepo are
**spec-time snapshots** — the record of what a feature intended, kept for history. When
they and this file disagree, this file is the live one, and the source is the authority
over both.

- Base URL: `http://localhost:8080` (the process binds `0.0.0.0:8080`, hardcoded).
- All request and response bodies are JSON, **camelCase**, unless a route says binary.
- There is no API versioning and no `/v1` prefix.

## The machine-readable spec

| | Path |
|---|---|
| **OpenAPI 3.0 document** | `/openapi.json` |
| **Swagger UI** — browsable, with try-it | `/docs` |

Both are **generated from the endpoint definitions**, so they cannot drift from the routes: request
schemas are the types the handlers bind, and every path, status code and field description comes
from the code that serves it. Use the spec for client generation and the UI for exploring; use this
file for what a schema cannot state — the caching rules, the queueing behaviour, and the few places
where the engine accepts something and is quietly wrong about it.

Both paths stay **open even when `RUNNER_API_KEY` is set**, exactly like `/health`. Swagger UI's
first act is an unauthenticated fetch of the spec, before its Authorize button exists to carry a
key, so gating them would make the UI unusable rather than secure — an API description is not API
data. `DOCS_ENABLED=false` removes both entirely.

The UI's **Authorize** button attaches `X-Api-Key` to try-it calls. Whether the key is *enforced*
depends on the server setting `RUNNER_API_KEY`; the document declares the scheme either way,
because the document is static and that setting is not.

One limit worth knowing: response bodies are produced by the worker process and passed through as
bytes, so response schemas are **declared** rather than bound — see
[Keeping this file honest](#keeping-this-file-honest) for what stops a declaration drifting.

## Contents

- [The machine-readable spec](#the-machine-readable-spec) — `/openapi.json` and `/docs`
- [Auth](#auth) · [Conventions](#conventions) · [Error taxonomy](#error-taxonomy)
- [Solve lifecycle](#solve-lifecycle-timeouts-queueing-caching) — timeouts, queueing, **caching**
- Routes: [health & templates](#health--templates) · [catalog](#catalog) ·
  [documents](#documents-validate-build-render) · [solving](#solving) · [flash](#flash)
- [The document schema](#the-document-schema) · [Validation issue codes](#validation-issue-codes)
- [Configuration](#configuration)

---

## Auth

One optional shared secret, `RUNNER_API_KEY`.

| `RUNNER_API_KEY` | Behaviour |
|---|---|
| unset | every route is open (local dev) |
| set | `X-Api-Key` required on **every route except `GET /health`**; compared in constant time |

A missing or wrong key is `401 UNAUTHORIZED`. `GET /health` is deliberately unauthenticated so a
load balancer can probe it. Clients read the value from `SIM_RUNNER_API_KEY`.

There is no per-user authn/authz here — this service has no notion of a user. That stays in the
calling platform.

```bash
curl -s localhost:8080/solve -H 'X-Api-Key: '"$SIM_RUNNER_API_KEY" \
  -H 'content-type: application/json' -d '{"templateId":"methanol_synthesis"}'
```

## Conventions

**Errors are `{ error, message }`** with an uppercase `error` code, at every status except 200.
Two shapes carry more:

- **`issues[]`** — the same objects `/flowsheets/validate` returns. On `400 DOCUMENT_INVALID`
  (structural rejection, from the API) and on `422 BUILD_FAILED` / `422 UNKNOWN_COMPOUND`
  (the engine rejected the document, from the worker). Always check for `issues` on both.
- **`detail`** — an optional third string on worker-originated `400`s
  (`INVALID_REQUEST` / `INVALID_OBJECT` / `INVALID_PROPERTY`), carrying engine context.

So a client switching on `error` should treat `issues` as available at 400 **and** 422, not as a
marker of one status.

**Template ids must match `^[A-Za-z0-9._-]+$`.** Anything else is `400 INVALID_REQUEST` before
any filesystem access. Ids are also re-checked for directory escape after path resolution.

**Units are optional and SI is assumed when absent.** A unit that *is* given must be a spelling
this runner knows or the document is refused with `INVALID_UNIT`. Do not transcribe the table
into a client — read it from `GET /catalog/units`, which serves the same dictionary that does the
accepting. (The reason is recorded in `DocumentValidator.cs`: DWSIM's `ConvertToSI` returns an
unknown unit's value **unchanged**, so a unit the runner admits and the converter does not produces
a number under the wrong dimension with no error at all.)

**Non-convergence is not an error.** A flowsheet that fails to converge is `200 OK` with
`converged: false` and a populated `warnings` array. Reserve HTTP failure for "we could not run
your request"; a run that completed and diverged is an answer.

## Error taxonomy

| Status | `error` | When |
|---|---|---|
| 400 | `INVALID_REQUEST` | malformed body, bad template id syntax, missing or invalid required field |
| 400 | `INVALID_OBJECT` | an override names an object the template does not contain |
| 400 | `INVALID_PROPERTY` | an override names a property that object does not expose, or cannot be set |
| 400 | `CONFLICTING_PARAMETERS` | `templateId` **and** `document` both supplied to `/compare` or `/optimize` |
| 400 | `DOCUMENT_INVALID` | document failed structural validation (carries `issues[]`) |
| 400 | `FLASH_INVALID` | flash type unknown, or its required specs missing |
| 401 | `UNAUTHORIZED` | `RUNNER_API_KEY` set, `X-Api-Key` missing or wrong |
| 403 | `TEMPLATE_READONLY` | `DELETE` on a curated template |
| 404 | `TEMPLATE_NOT_FOUND` | no curated or user template with that id |
| 409 | `TEMPLATE_NAME_CONFLICT` | `saveAsTemplate.id` collides with a curated name, or with an existing user template and `overwrite` was not `true` |
| 422 | `TEMPLATE_LOAD_FAILED` | the file exists; the engine could not open it |
| 422 | `BUILD_FAILED` | the engine refused to construct the flowsheet (carries `issues[]`) |
| 422 | `UNKNOWN_COMPOUND` | a compound name the engine does not have (carries `issues[]`; the message suggests up to 5 near matches) |
| 422 | `RENDER_FAILED` | PFD rendering failed, or the worker returned no image data |
| 422 | `OPTIMIZATION_INFEASIBLE` | no evaluation converged with a readable objective |
| 429 | `QUEUE_FULL` | admission cap reached; `Retry-After: 5` is set |
| 500 | `WORKER_CRASH` | worker died unexpectedly, or returned a non-JSON body. Detail stays in server logs |
| 503 | `ENGINE_UNAVAILABLE` | the catalog worker failed or returned invalid JSON — check `/health` |
| 504 | `SOLVE_TIMEOUT` | hard timeout; the worker process tree was killed |

Worker-originated 400/422 bodies are **passed through verbatim** when the worker emitted valid JSON
containing an `error` field, so the message is usually more specific than the table's fallback.

## Solve lifecycle, timeouts, queueing, caching

There are no jobs, no polling and no callbacks. **Every route is synchronous** and holds the
connection for the whole solve. Long-running behaviour is bounded by a timeout, not by a job id.

**One worker process per job.** The API never loads DWSIM into its own address space; it writes a
job file to temp, spawns `dotnet DwsimRunner.Worker.dll <jobfile>`, reads stdout, and maps the exit
code. Worker exit codes: `0` ok · `2` invalid input (400) · `3` template load failed (422) ·
`4` build failed (422) · `5` render failed (422) · anything else → `WORKER_CRASH` (500).

**Concurrency and admission.** `MAX_CONCURRENT_SOLVES` worker processes run at once (a semaphore).
Beyond that, requests queue up to an admission cap of `MAX_CONCURRENT_SOLVES × 5` **admitted**
requests — running plus queued. The next request over the cap is rejected `429 QUEUE_FULL` with
`Retry-After: 5` rather than queued indefinitely.

`POST /compare` checks capacity for the whole case set up front and rejects the set as one; a
per-case race can still degrade to a per-case `QUEUE_FULL` entry inside a `200` result map.

**Timeouts.** On expiry the worker's whole process tree is killed and the route returns `504`.

| Route | Default | Caller override |
|---|---|---|
| `/solve`, `/compare`, `/optimize` (per evaluation) | `SOLVE_TIMEOUT_SECONDS` (60) | `timeoutSeconds`, honoured in `1..600` |
| `/flowsheets/build-solve` | **120** — *not* `SOLVE_TIMEOUT_SECONDS` | `timeoutSeconds`, **clamped** to `5..600` |
| `/flowsheets/validate`, `/flash`, `/flowsheets/pfd`, `/templates/{id}/objects`, `/templates/{id}/pfd.png`, catalog | `SOLVE_TIMEOUT_SECONDS` (60) | none |

The two clamping rules differ and both are intentional: `/solve` **ignores** an out-of-range
`timeoutSeconds` and falls back to the default, `build-solve` **clamps** it into range.

### Caching — read this before you time anything

Results are held in a bounded LRU (`CACHE_SIZE`, default 256 entries).

| Route | Cache key | Cached when |
|---|---|---|
| `/solve`, `/compare` (template cases) | template id + **file mtime** + canonicalized overrides | `converged: true` only |
| `/templates/{id}/objects`, `/templates/{id}/pfd.png` | same, plus the mode | always (pure functions of the file) |
| `/flowsheets/build-solve`, `/compare`/`/optimize` document cases | hash of the document + catalog engine version | **any 200**, including `converged: false` |
| `/flash` | hash of the flash request + catalog engine version | any 200 |
| `/catalog/*` | in-memory, keyed by probed engine version | always, until the version changes |

Two consequences worth stating plainly:

1. **A repeated `build-solve` replays a non-convergence.** Document-scoped results are cached
   regardless of `converged`, so a second identical request returns the same failure in ~0 ms.
2. **A cache hit reports the original `elapsedMs`, and the HTTP round trip will be near-zero.**
   Timing a re-run measures the cache, not the engine. Restart the container before benchmarking.

`saveAsTemplate` requests **bypass the cache lookup and are never cached** — the persistence side
effect has to actually run.

There is no cache invalidation endpoint. The template half needs none (the mtime is in the key, and
a deleted template 404s before the lookup); the document half is keyed by content.

---

## Health & templates

### `GET /health`

Unauthenticated. Readiness, engine identity and the curated template list in one call.

```bash
curl -s localhost:8080/health | jq .
```

```jsonc
{
  "ok": true,                     // == dwsimFound
  "dwsimPath": "/opt/dwsim",
  "dwsimFound": true,
  "dwsimVersion": "9.0.5.0",      // DWSIM LIBRARY version, read from PE metadata; null if unreadable
  "buildRef": "a41cf15",          // WHICH RUNNER BUILD, baked from a Docker ARG. "unknown" if unset
  "supportedRange": ">=9.0 <10",
  "versionSupported": true,
  "templatesPath": "/templates",
  "templates": ["methanol_synthesis"],   // bare curated ids, ordinal-sorted
  "maxConcurrent": 6,
  "maxEvaluations": 30,           // /optimize budget cap
  "maxTimeoutSeconds": 600,
  "hint": null                    // install instructions when dwsimFound is false
}
```

`dwsimVersion` **cannot tell two runner builds apart** — it is the library's version, and two
runner images pinning the same DWSIM report it identically while disagreeing about what they
accept. `buildRef` is the field that answers "which build is this". Unset is an explicit
`"unknown"`, never an absent field, because an absent field and a stale one read the same.

An out-of-range engine still solves; the result gains a best-effort warning in `warnings[]`.

### `GET /templates`

Object-shaped listing of curated **and** user templates.

```jsonc
[ { "id": "methanol_synthesis", "source": "curated", "createdUtc": null, "solvedAtSave": null },
  { "id": "my-plant",           "source": "user", "createdUtc": "2026-09-04T10:00:00Z", "solvedAtSave": true } ]
```

`/health`'s `templates` array is curated ids only. This route is the complete list.

### `DELETE /templates/{id}`

`204 No Content`. User templates only — `403 TEMPLATE_READONLY` on a curated id, `404` on unknown.

### `GET /templates/{id}/file`

The raw `.dwxmz` bytes as `application/octet-stream`. Works for curated and user templates.
`iskra-app` uses this to pull a freshly saved template into its own Postgres store (the SaaS system
of record) and then `DELETE`s the runner-side copy.

### `GET /templates/{id}/objects`

Object inventory without solving. Cached by template mtime.

```jsonc
{ "objects": [ { "tag": "R-101", "type": "Reactor_Conversion",
                 "settableProperties": ["OutletTemperature", "Pressure"] } ] }
```

Discover legal `/solve` override targets here — `settableProperties` is the `property` vocabulary
for that object.

### `GET /templates/{id}/pfd.png`

Rendered flowsheet diagram, `image/png`, cached by template mtime. Failures stay JSON (`422`).

## Catalog

Four sections of one payload the worker produces in `catalog` mode, fetched once per engine
version and served from memory. Each returns `{ engineVersion, <section> }`.

| Route | Section shape |
|---|---|
| `GET /catalog/compounds` | `[{ name, formula, casNumber }]` |
| `GET /catalog/property-packages` | `[{ id, name, description }]` |
| `GET /catalog/unit-op-types` | port and parameter schema per wire type — the source for legal `type`, `port` and `parameters` values in a document |
| `GET /catalog/engine-inventory` | `[{ name, displayName, source, instantiable, exposedAs }]` |

`engine-inventory` is what the **engine** declares versus what this runner **exposes**:
`exposedAs: null` means DWSIM has the unit op and this runner has no wire type for it. An absent
capability that says so.

If the catalog worker fails, these return `503 ENGINE_UNAVAILABLE`.

### `GET /catalog/units`

Served from the validator's own dictionary rather than through the worker, so it is the table that
actually accepts or rejects a unit and not a second opinion about it. Served even when the engine
is unavailable.

```jsonc
{ "engineVersion": "9.0.5.0",
  "units": { "temperature": ["C","K","F"],
             "pressure": ["bar","Pa","kPa","MPa","atm","psi","mbar"],
             "dimensionless": [], "integer": [], "string": [] } }
```

It exists so a client's transcribed copy can be **checked**. If the two disagree, a value is either
dropped before it ships (client stricter) or refused with `INVALID_UNIT` after it does (runner
stricter) — and both surface a long way from the edit that caused them.

## Documents: validate, build, render

### `POST /flowsheets/validate`

```jsonc
{ "document": { /* see The document schema */ },
  "semantic": true }    // default true; false = structural only, no worker spawn, no queue slot
```

Always `200` when the request itself is well-formed — validity is in the body, not the status.

```jsonc
{ "valid": false,
  "issues": [ { "severity": "error", "code": "MISSING_REQUIRED_PORT",
                "tag": "HX", "path": "objects[2]", "message": "..." } ] }
```

Structural checks run in-process against the cached catalog and **short-circuit semantic** ones — a
structurally invalid document is never sent to the engine. If the catalog is unavailable the
catalog-independent checks (schema version, duplicate tags, units) still run; the fetch failure is
silent.

Structural checks are collect-all, so you get every issue in one pass rather than one per round trip.

### `POST /flowsheets/build-solve`

Build a document into a flowsheet, solve it, optionally save it as a template. One shot.

```jsonc
{ "document": { /* ... */ },
  "timeoutSeconds": 120,                             // optional, clamped 5..600, default 120
  "saveAsTemplate": { "id": "my-plant", "overwrite": false } }   // optional
}
```

Response is a solve result plus a `build` block:

```jsonc
{ "converged": true, "elapsedMs": 3412,
  "streams": [ /* StreamRow */ ], "energy": [ /* EnergyRow */ ], "unitOps": [ /* UnitOpRow */ ],
  "warnings": [],
  "build": { "objectsCreated": 5, "connectionsMade": 4, "elapsedMs": 88 },
  "template": { "id": "my-plant", "source": "user", "saved": true } }   // only when saveAsTemplate was sent
```

This route can reject a document at two different depths, and both carry `issues[]`:
`400 DOCUMENT_INVALID` when the API's structural pass refuses it, and `422 BUILD_FAILED` (or
`422 UNKNOWN_COMPOUND`) when the structural pass let it through and the **engine** refused to
build it. Handle both.

**A failed save is not a failed solve.** `saveAsTemplate` conflicts are pre-checked and 409 before
the solve, but a store that turns out to be unwritable is reported *after* a successful solve as a
soft block — the solve is never lost to a persistence side effect:

```jsonc
"template": { "id": "my-plant", "source": "user", "saved": false, "reason": "STORE_UNAVAILABLE" }
```

`reason` is `STORE_UNAVAILABLE` (the directory is not writable) or `WRITE_FAILED` (it is writable
and the `.dwxmz` write still failed).

```bash
curl -s localhost:8080/flowsheets/build-solve -H 'content-type: application/json' -d '{
  "document": {
    "schemaVersion": 1, "name": "heater", "compounds": ["Water"], "propertyPackage": "STEAM",
    "objects": [
      { "tag": "S1", "kind": "materialStream",
        "spec": { "temperature": {"value": 25, "unit": "C"},
                  "pressure": {"value": 1, "unit": "bar"},
                  "massFlow": {"value": 1000, "unit": "kg/h"},
                  "composition": { "basis": "mass", "fractions": { "Water": 1.0 } } } },
      { "tag": "H-101", "kind": "unitOp", "type": "heater",
        "parameters": { "outletTemperature": { "value": 80, "unit": "C" } } },
      { "tag": "S2", "kind": "materialStream" }
    ],
    "connections": [ { "from": "S1", "to": "H-101", "port": "Inlet" },
                     { "from": "H-101", "to": "S2", "port": "Outlet" } ]
  }
}' | jq '{converged, elapsedMs, build}'
```

### `POST /flowsheets/pfd`

`{ "document": {...} }` → `image/png`. Auto-layout when object positions are absent. Errors stay
JSON.

## Solving

### `POST /solve`

Solve a stored template with optional property overrides.

```jsonc
{ "templateId": "methanol_synthesis",
  "overrides": [ { "object": "R-101", "property": "OutletTemperature", "value": 250, "unit": "C" } ],
  "timeoutSeconds": 60 }   // optional; out-of-range values fall back to the default
```

`object` and `property` must name something real — see `GET /templates/{id}/objects`. An unknown
target is `400 INVALID_REQUEST` from the worker, with its own message.

```jsonc
{ "converged": true, "elapsedMs": 4210,
  "streams": [ {
    "name": "S-1", "phase": "liquid",              // DERIVED from phases[], not an engine slot index
    "temperatureC": 80.0, "pressureBar": 1.01325,
    "massFlowKgH": 1000.0, "molarFlowKmolH": 55.5,
    "compositionMol": { "Water": 1.0 },
    "densityKgM3": 971.8,
    "compositionMass": { "Water": 1.0 },           // null = the engine did not report it, never an implied zero
    "vaporFraction": 0.0,
    "phases": [ { "name": "liquid", "moleFraction": 1.0, "composition": { "Water": 1.0 },
                  "densityKgM3": 971.8, "molecularWeight": 18.015,
                  "heatCapacityKJKgK": 4.19, "viscosityPaS": 0.00035 } ]
  } ],
  "energy":  [ { "name": "E-1", "dutyKw": 63.9 } ],
  "unitOps": [ { "name": "H-101", "type": "Heater", "powerKw": null, "dutyKw": 63.9,
                 "outletTemperatureC": 80.0, "outletPressureBar": 1.01325,
                 "solvingMethod": null, "maxIterations": null } ],  // columns only
  "warnings": [] }
```

Phase blocks are named in physics terms — `vapor` / `liquid` / `liquid2` / `solid` — never engine
slot indexes. A nullable field that is absent means the engine did not report it.

### `POST /compare`

Fan out one template or document across 1–25 named cases, each with its own overrides. Per-case
error isolation: one case failing does not fail the set.

```jsonc
{ "templateId": "methanol_synthesis",       // templateId XOR document — both is 400 CONFLICTING_PARAMETERS
  "cases": { "base": [], "hot": [ { "object": "R-101", "property": "OutletTemperature", "value": 300, "unit": "C" } ] },
  "timeoutSeconds": 60 }
```

```jsonc
{ "results": { "base": { "converged": true, "elapsedMs": 4210, "streams": [ /* ... */ ] },
               "hot":  { "error": "SOLVE_TIMEOUT", "message": "solve timed out after 60s" } } }
```

The envelope is `200` whenever the request was accepted; a per-case value is **either** a solve
result **or** an `{ error, message }` object. Discriminate on the presence of `error`.

Cases run concurrently through the same semaphore and cache as `/solve`, so a compare case and a
direct solve of the same inputs return identical bytes.

**A sweep is a compare whose cases you expanded from a range.** There is deliberately no `/sweep`.

### `POST /optimize`

Golden-section search over one variable. Every evaluation is an ordinary cached solve, run
sequentially — the search is inherently sequential.

```jsonc
{ "templateId": "methanol_synthesis",       // templateId XOR document
  "variable":  { "object": "R-101", "property": "OutletTemperature", "unit": "C", "min": 200, "max": 300 },
  "objective": { "object": "S-5", "property": "massFlowKgH", "direction": "maximize" },  // or "minimize"
  "tolerance": 0.5,          // optional; defaults to (max - min) * 1e-3
  "maxEvaluations": 20,      // optional; 2..30, default 20
  "timeoutSeconds": 60 }     // per evaluation, not for the whole search
```

```jsonc
{ "best": { "value": 264.1, "objectiveValue": 1180.4, "result": { /* the full SolveResult */ } },
  "evaluations": [ { "value": 238.2, "objectiveValue": 1102.7, "converged": true },
                   { "value": 261.8, "objectiveValue": null,   "converged": false } ],
  "converged": true,
  "stoppedReason": "tolerance" }
```

`variable.min` must be strictly less than `variable.max` and both must be finite. A non-converging
evaluation contributes `objectiveValue: null` and is skipped by the search rather than failing it.
If **no** evaluation converges with a readable objective the whole call is
`422 OPTIMIZATION_INFEASIBLE`.

Worst-case wall time is `maxEvaluations × timeoutSeconds`. At the caps (30 × 600) that is five
hours on one HTTP connection — bound `timeoutSeconds` yourself for anything interactive.

## Flash

### `POST /flash`

Single-point thermodynamics, no flowsheet.

```jsonc
{ "compounds": ["Water", "Ethanol"],
  "composition": { "basis": "mole", "fractions": { "Water": 0.6, "Ethanol": 0.4 } },
  "propertyPackage": "NRTL",
  "flashType": "TP",
  "temperature": { "value": 80, "unit": "C" },
  "pressure":    { "value": 1,  "unit": "bar" } }
```

| `flashType` | Required specs |
|---|---|
| `TP` | `temperature` + `pressure` |
| `PH` | `pressure` + `enthalpy` |
| `PS` | `pressure` + `entropy` |
| `PVF` | `pressure` + `vaporFraction` |
| `TVF` | `temperature` + `vaporFraction` |

Anything else is `400 FLASH_INVALID`. `TH` and `TS` are **not supported** — they kill the worker
process outright (measured under both STEAM and PR). `PSF`/`TSF` need solids handling this runner
does not select.

> ⚠️ **`TVF` is accepted and, on a pure compound, silently insensitive.** For a single compound,
> saturation pressure does not depend on vapour fraction, so `TVF` at three different
> `vaporFraction` values returns three identical results. Prefer `PVF`, which is responsive.
> Recorded in the iskra monorepo as spec 147.

```jsonc
{ "vaporFraction": 0.0,
  "temperatureC": 80.0, "pressureBar": 1.0,
  "phases": [ { "phase": "liquid", "molarFraction": 1.0,
                "composition": { "Water": 0.6, "Ethanol": 0.4 } } ],
  "enthalpyKJKg": -15234.1, "entropyKJKgK": -4.22,
  "densityKgM3": 856.3 }
```

The API pre-checks the flash type and its specs in ~50 ms without spawning a worker. **That
validator duplicates the worker's switch by design** — extend both, or the API vetoes a flash the
worker would have run.

---

## The document schema

The body of `document` for `/flowsheets/validate`, `/flowsheets/build-solve`, `/flowsheets/pfd`,
and the document form of `/compare` and `/optimize`.

```jsonc
{
  "schemaVersion": 1,                    // required, number, must be 1
  "name": "heat exchanger",
  "compounds": ["Water"],                // engine compound names — GET /catalog/compounds
  "propertyPackage": "STEAM",            // GET /catalog/property-packages
  "reactionSets": [ /* required by reactor types that declare requiresReactionSet */ ],
  "objects": [
    { "tag": "HOTIN", "kind": "materialStream",
      "spec": { "temperature": { "value": 400.0, "unit": "K" },
                "pressure":    { "value": 304000, "unit": "Pa" },
                "massFlow":    { "value": 1.0, "unit": "kg/s" },
                "composition": { "basis": "mass", "fractions": { "Water": 1.0 } } } },
    { "tag": "HX", "kind": "unitOp", "type": "heatExchanger",
      "parameters": { "overallUA": { "value": 500, "unit": "W/[m2.K]" } } },
    { "tag": "HOTOUT", "kind": "materialStream" }   // outlet: no spec, the engine computes it
  ],
  "connections": [
    { "from": "HOTIN", "to": "HX",     "port": "Inlet 1" },
    { "from": "HX",    "to": "HOTOUT", "port": "Outlet 1" }
  ]
}
```

- **Size cap: 200 KB.** Over it is `DOCUMENT_TOO_LARGE`.
- `kind` is `materialStream`, `energyStream` or `unitOp`.
- `tag` is the join key and must be unique across `objects` (`DUPLICATE_TAG`).
- Legal `type`, `port` and `parameters` names per unit-op type come from
  `GET /catalog/unit-op-types`. Do not hardcode them.
- Every quantity is `{ value, unit? }`. Omit `unit` for SI.
- An outlet stream is declared with **no `spec`** — it is what the solve produces.

## Validation issue codes

The `code` on an entry in `issues[]`. Distinct from the top-level `error` taxonomy: these describe
a place in your document, and carry `tag` and `path` to point at it.

| Code | Meaning |
|---|---|
| `SCHEMA_INVALID` | not an object, or a required field missing/wrong type |
| `UNSUPPORTED_SCHEMA` | `schemaVersion` is not 1 |
| `DOCUMENT_TOO_LARGE` | over 200 KB |
| `DUPLICATE_TAG` | two objects share a `tag` |
| `UNKNOWN_UNIT_OP_TYPE` | `type` is not in the catalog |
| `UNKNOWN_PORT` | `port` is not declared by that unit-op type |
| `MISSING_REQUIRED_PORT` | a port the type declares `required` has no connection |
| `PORT_CONFLICT` | two connections claim one port |
| `UNRESOLVED_REFERENCE` | a `from`/`to` names no declared `tag` |
| `MISSING_REQUIRED_PARAMETER` | a parameter the type requires is absent |
| `INVALID_PARAMETER_VALUE` | a parameter value is out of range or the wrong type |
| `INVALID_UNIT` | a unit spelling this runner does not accept — see `GET /catalog/units` |
| `COMPOSITION_NOT_NORMALIZED` | `fractions` do not sum to 1 |
| `MISSING_REACTION_SET` | a reactor type declaring `requiresReactionSet` has none |
| `CONFLICTING_PARAMETERS` | mutually exclusive parameters both set |

`severity` is `error` or `warning`. Only an `error` makes the document invalid; warnings ride
along on a `valid: true` response.

## Configuration

Deployment settings live in one place — the environment-variable table in
[`../README.md`](../README.md#environment-variables). Four of them change what a caller observes:

| Var | Effect on the API |
|---|---|
| `RUNNER_API_KEY` | unset = every route open; set = `X-Api-Key` required except on `GET /health` |
| `SOLVE_TIMEOUT_SECONDS` | the default timeout for every route except `build-solve` (which is 120) |
| `MAX_CONCURRENT_SOLVES` | worker pool size; the `429 QUEUE_FULL` admission cap is **5×** this |
| `CACHE_SIZE` | LRU result-cache entries |

**Do not assume `MAX_CONCURRENT_SOLVES`.** The code falls back to 4 and the shipped images set 6.
Read the effective value from `/health`'s `maxConcurrent`.

## Keeping this file honest

The offline API tier (`tests/DwsimRunner.Api.Tests/`) runs against a `FakeWorker` stub and needs no
DWSIM, so it pins routing, the error taxonomy, auth, the cache and the queue cap on every commit:

```bash
dotnet test tests/DwsimRunner.Api.Tests
```

`OpenApiContractTests.cs` is what keeps the generated document from lying. A spec generated from
code is only worth more than prose if it cannot quietly disagree with the service, and there are
three ways it could:

1. **A new route lands with no metadata.** Swashbuckle would list it with no summary and no
   response schema — present in the document, describing nothing. Every operation is asserted to
   carry a summary and a 2xx body, and the path list is checked in *both* directions, so an
   undocumented new route fails the build instead of appearing blank.
2. **A field description silently vanishes.** `///` comments written *inside* a record's parameter
   list compile with a CS1587 warning and are **discarded** — the source reads as documented and
   the document comes out bare. That happened while this was being written, which is why a known
   description is pinned rather than trusted.
3. **A response record drifts from what the worker sends.** Response bodies are worker
   pass-through, so nothing at runtime would ever notice a field the schema fails to name. Real
   worker output is round-tripped through each declared record and any dropped field fails.

Response schemas are declared rather than bound **on purpose**: if the API deserialized results
into its own records and re-serialized them, any field the worker later learned to emit would be
silently stripped on the way out, and the two processes would have to be upgraded in lockstep.
`solvingMethod` (spec 143) and the per-phase blocks (spec 120) both arrived exactly that way.

Where a claim here is behavioural rather than structural — the `TVF` insensitivity, the cache
replaying a non-convergence — it was measured against a live engine and is cited to the spec that
measured it. Those are the claims the generated document cannot make, and the reason this file
exists alongside it.
