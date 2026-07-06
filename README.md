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

All bodies are JSON, camelCase. Full contract:
[`../specs/001-dwsim-headless-runner/contracts/runner-api.md`](../specs/001-dwsim-headless-runner/contracts/runner-api.md).

| Route | Body / result |
|---|---|
| `GET /health` | `{ ok, dwsimPath, dwsimFound, dwsimVersion, supportedRange, versionSupported, templatesPath, templates:[...], maxConcurrent, hint? }` — one status call answers readiness + version + templates |
| `GET /templates` | `["methanol_synthesis", …]` — template ids |
| `GET /templates/{id}/objects` | `{ objects:[{ tag, type, settableProperties }] }` — object inventory (FR-014); no solve, cached by template mtime. Discover override targets here |
| `POST /solve` | `{ templateId, overrides:[{object, property, value, unit?}], timeoutSeconds? }` → `{ converged, elapsedMs, streams:[…], energy:[…], unitOps:[…], warnings:[…] }` |
| `POST /compare` | `{ templateId, cases:{ name → overrides[] }, timeoutSeconds? }` → `{ results:{ name → SolveResult \| CaseError } }` — fan-out with per-case error isolation (1–10 cases) |

### Error taxonomy

| Status | `error` | When |
|---|---|---|
| 400 | `INVALID_REQUEST` \| `INVALID_OBJECT` \| `INVALID_PROPERTY` | bad templateId syntax, unknown override target, unsupported stream property |
| 404 | `TEMPLATE_NOT_FOUND` | unknown template id |
| 401 | `UNAUTHORIZED` | `RUNNER_API_KEY` set and `X-Api-Key` missing/wrong (all routes except `GET /health`) |
| 422 | `TEMPLATE_LOAD_FAILED` | engine cannot load the template file |
| 429 | `QUEUE_FULL` (+ `Retry-After`) | queue at capacity |
| 504 | `SOLVE_TIMEOUT` | hard timeout; worker killed |
| 500 | `WORKER_CRASH` | unexpected failure; detail in server logs only |

Non-convergence is **not** an error: HTTP 200 with `converged:false`.

## Environment variables

| Var | Default | Purpose |
|---|---|---|
| `DWSIM_PATH` | `/opt/dwsim` | DWSIM install dir (runtime + compile-time) |
| `TEMPLATES_PATH` | `/templates` | directory of `.dwxmz` reference plants |
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