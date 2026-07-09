# dwsim-runner (GPL-3.0)

Headless DWSIM solve service. Two processes:

- **DwsimRunner.Api** — ASP.NET Core minimal API. Owns HTTP, queueing, timeouts,
  caching, auth. Never loads DWSIM into its own address space.
- **DwsimRunner.Worker** — short-lived process, one solve per invocation.
  Loads DWSIM assemblies (from `DWSIM_PATH` at runtime), loads a `.dwxmz`
  template, applies overrides, solves, prints a JSON stream table to stdout,
  exits. Killed hard on timeout.

Why a worker process: flowsheet solvers can diverge or leak; .NET cannot safely
kill a runaway thread. Process-per-solve gives crash isolation, a real timeout,
and (conveniently) a hard GPL process boundary.

## Building

DWSIM assemblies are compile-time references resolved via `DWSIM_PATH`:

```bash
scripts/fetch-dwsim.sh                 # fetch the pinned DWSIM 9.0.x into ./dwsim/
export DWSIM_PATH=$PWD/dwsim
dotnet publish src/DwsimRunner.Api    -c Release -o publish/api
dotnet publish src/DwsimRunner.Worker -c Release -o publish/worker
```

The published output does **not** copy DWSIM DLLs (`Private=false` on the
references) — at runtime the worker resolves them from `DWSIM_PATH` via an
`AssemblyResolve` hook. This is what makes the on-prem "customer installs
DWSIM separately" model work with a single build.

Supported DWSIM version: **9.0.x** (validated against 9.0.5). `/health`
reports the detected version and supported range; solves against an
out-of-range version still run but append a best-effort warning.

## Running

Docker (recommended):

```bash
docker compose up -d --build          # SaaS image, DWSIM bundled
curl -s localhost:8080/health | jq .  # ok:true once ready
```

Bare metal:

```bash
export DWSIM_PATH=/opt/dwsim
export TEMPLATES_PATH=./templates
export WORKER_PATH=./publish/worker/DwsimRunner.Worker.dll
export SOLVE_TIMEOUT_SECONDS=60
export MAX_CONCURRENT_SOLVES=6
dotnet publish/api/DwsimRunner.Api.dll   # listens on :8080
```

## API

All bodies are JSON, camelCase. Full contracts:
[`../specs/001-dwsim-headless-runner/contracts/runner-api.md`](../specs/001-dwsim-headless-runner/contracts/runner-api.md) (v1, still in force) and
[`../specs/002-dwsim-mcp-tools/contracts/runner-api-v2.md`](../specs/002-dwsim-mcp-tools/contracts/runner-api-v2.md) (authoring extension).

| Route | Body / result |
|---|---|
| `GET /health` | `{ ok, dwsimPath, dwsimFound, dwsimVersion, supportedRange, versionSupported, templatesPath, templates:[...], maxConcurrent, maxEvaluations, maxTimeoutSeconds, hint? }` — one status call answers readiness + version + templates (bare curated ids) |
| `GET /templates` | `[{ id, source:"curated"\|"user", createdUtc?, solvedAtSave? }, …]` — object-shaped listing, curated + user templates |
| `DELETE /templates/{id}` | 204 — user templates only (403 `TEMPLATE_READONLY` for curated) |
| `GET /templates/{id}/file` | binary `application/octet-stream` `.dwxmz` — raw flowsheet file (spec 011 Cut 3; the iskra-app pulls a saved template into its Postgres `flow_templates` table via this, then DELETEs the runner-side copy) |
| `GET /templates/{id}/objects` | `{ objects:[{ tag, type, settableProperties }] }` — object inventory (FR-014); no solve, cached by template mtime. Discover override targets here |
| `GET /templates/{id}/pfd.png` | binary `image/png` — rendered flowsheet diagram, cached by template mtime |
| `POST /solve` | `{ templateId, overrides:[{object, property, value, unit?}], timeoutSeconds? }` → `{ converged, elapsedMs, streams:[…], energy:[…], unitOps:[…], warnings:[…] }` |
| `POST /compare` | `{ templateId, cases:{ name → overrides[] }, timeoutSeconds? }` → `{ results:{ name → SolveResult \| CaseError } }` — fan-out with per-case error isolation (1–25 cases) |
| `GET /catalog/compounds` · `/catalog/property-packages` · `/catalog/unit-op-types` | engine catalogs (names, formulas; package ids; unit-op port/parameter schemas), cached per engine version |
| `POST /flowsheets/validate` | `{ document, semantic? }` → `{ valid, issues:[{severity, code, tag?, path?, message}] }` — structural checks in-process, semantic via a worker |
| `POST /flowsheets/build-solve` | `{ document, timeoutSeconds?, saveAsTemplate?:{id, overwrite?} }` → SolveResult + `build:{objectsCreated, connectionsMade, elapsedMs}` (+ `template:{id, source, saved}` when saved) |
| `POST /flowsheets/pfd` | `{ document }` → binary `image/png` (auto-layout when positions absent) |
| `POST /flash` | Flash Request (compounds, composition, propertyPackage, flashType TP\|PH\|PS + matching specs) → phase split, per-phase compositions, h/s |
| `POST /optimize` | `{ templateId, variable:{object, property, unit?, min, max}, objective:{object, property, direction}, tolerance?, maxEvaluations? (≤30), timeoutSeconds? }` → `{ best:{value, objectiveValue, result}, evaluations:[…], converged, stoppedReason }` — golden-section, sequential solves |

### Worker modes

One worker process per job; `mode` in the job file selects the handler
(exit codes: 0 ok, 2 invalid input, 3 template load, 4 build failed,
5 render failed, 1 crash):

| Mode | Purpose |
|---|---|
| `solve` (default) | load template, apply overrides, solve, harvest streams |
| `inspect` | object inventory without solving |
| `catalog` | compounds + property packages + unit-op schemas |
| `validate` | semantic document validation (build dry-run, no solve) |
| `build-solve` | build document → solve → optional `.dwxmz` save |
| `flash` | single-point TP/PH/PS flash, no flowsheet |
| `pfd` | build/load → auto-layout → SkiaSharp PNG render |

### Error taxonomy

| Status | `error` | When |
|---|---|---|
| 400 | `INVALID_REQUEST` \| `INVALID_OBJECT` \| `INVALID_PROPERTY` \| `DOCUMENT_INVALID` \| `FLASH_INVALID` | bad templateId syntax, unknown override target, unsupported stream property, structurally invalid document, bad flash spec |
| 404 | `TEMPLATE_NOT_FOUND` | unknown template id |
| 401 | `UNAUTHORIZED` | `RUNNER_API_KEY` set and `X-Api-Key` missing/wrong (all routes except `GET /health`) |
| 403 | `TEMPLATE_READONLY` | DELETE on a curated template |
| 409 | `TEMPLATE_NAME_CONFLICT` | saveAsTemplate id exists (pass `overwrite:true`) or collides with a curated name |
| 422 | `TEMPLATE_LOAD_FAILED` \| `BUILD_FAILED` \| `UNKNOWN_COMPOUND` \| `RENDER_FAILED` \| `OPTIMIZATION_INFEASIBLE` | engine cannot load/build/render, or no feasible optimization point |
| 429 | `QUEUE_FULL` (+ `Retry-After`) | queue at capacity |
| 504 | `SOLVE_TIMEOUT` | hard timeout; worker killed |
| 500 | `WORKER_CRASH` | unexpected engine failure |

Non-convergence is **not** an error: HTTP 200 with `converged:false`.

A `saveAsTemplate` request against an unwritable `USER_TEMPLATES_PATH` is **not** an error
(spec 011): the solve returns 200 and the `template` block carries `saved:false` with
`reason: STORE_UNAVAILABLE` (or `WRITE_FAILED` if the dir is writable but the `.dwxmz` write
itself failed). The solve is never blocked by a persistence side-effect.

## Environment variables

| Var | Default | Purpose |
|---|---|---|
| `DWSIM_PATH` | `/opt/dwsim` | DWSIM install dir (runtime + compile-time) |
| `TEMPLATES_PATH` | `/templates` | directory of curated `.dwxmz` reference plants (read-only) |
| `USER_TEMPLATES_PATH` | `<TEMPLATES_PATH>/user` | writable directory for user-saved templates (`.dwxmz` + `.doc.json` provenance sidecars). On-prem only (steering §10 Q4); in SaaS the app's Postgres `flow_templates` table is the system of record and this path is unused for user state. An unwritable dir no longer fails the solve — see the error-taxonomy note above. |
| `WORKER_PATH` | `/app/worker/DwsimRunner.Worker.dll` | worker assembly |
| `SOLVE_TIMEOUT_SECONDS` | `60` | hard per-solve timeout (cap 600) |
| `MAX_CONCURRENT_SOLVES` | `6` | worker process pool size (SC-006 target) |
| `CACHE_SIZE` | `256` | bounded LRU result cache entries |
| `RUNNER_API_KEY` | _(unset)_ | optional shared API key; when set, `X-Api-Key` required on all routes except `GET /health` (FR-016). Clients read it from `SIM_RUNNER_API_KEY` |

## Testing

Two tiers (Constitution IX, test-first):

- **Tier A** — `tests/DwsimRunner.Api.Tests/`: API tests against a `FakeWorker`
  stub (no DWSIM required, CI-safe). 38 tests cover routing, validation, error
  taxonomy, cache, queue-cap, /compare, introspection, unitOps, and auth.
  ```bash
  dotnet test tests/DwsimRunner.Api.Tests
  ```
- **Tier B** — `tests/DwsimRunner.Integration.Tests/`: real solves of
  `methanol_synthesis` against a running runner. Self-skips unless
  `SIM_RUNNER_URL` points at a healthy runner with DWSIM.
  ```bash
  docker compose up -d --build
  SIM_RUNNER_URL=http://localhost:8080 dotnet test tests/DwsimRunner.Integration.Tests
  ```

## No-conveyance verification

The on-prem image must contain zero DWSIM binaries (Constitution IV):

```bash
scripts/verify-no-conveyance.sh    # builds onprem image, scans every layer for DWSIM.*
```

## On-prem notes (deferred)

On-prem deployment is deferred per product steering (reopen at customer 50).
The architecture supports it by construction: `Dockerfile.onprem` builds an
image with no DWSIM, the customer mounts their install at `/opt/dwsim:ro`, and
`/health` reports whether the mount is present. `scripts/verify-no-conveyance.sh`
keeps the no-conveyance guarantee mechanically verified in the meantime.

## License

GPL-3.0. This service references DWSIM (GPL-3.0) assemblies and is licensed
accordingly. The iskra application communicates with this service exclusively
over HTTP and is a separate, independent work.