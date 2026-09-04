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

All bodies are JSON, camelCase; the process listens on `:8080`.

| | Path |
|---|---|
| **OpenAPI 3.0 spec** — generated from the endpoints | `/openapi.json` |
| **Swagger UI** — browsable, with try-it | `/docs` |
| **Narrative reference** — lifecycle, caching, engine caveats | [`docs/api.md`](docs/api.md) |

The spec and UI are generated from the endpoint definitions, so they cannot drift from the
routes. `docs/api.md` covers what a schema cannot state: the caching and queueing rules, and the
places where the engine accepts something and is quietly wrong about it.

Both doc paths stay open even when `RUNNER_API_KEY` is set (like `/health`) — Swagger UI fetches
the spec before its Authorize button exists to carry a key, so gating them breaks the UI rather
than securing anything. `DOCS_ENABLED=false` removes both.

Docs live in this repo so a route change and its documentation are the same commit. The
`specs/*/contracts/*.md` files in the iskra monorepo are spec-time snapshots kept for history;
where they and `docs/api.md` disagree, `docs/api.md` is the live one.

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

## Environment variables

| Var | Default | Purpose |
|---|---|---|
| `DWSIM_PATH` | `/opt/dwsim` | DWSIM install dir (runtime + compile-time) |
| `TEMPLATES_PATH` | `/templates` | directory of curated `.dwxmz` reference plants (read-only) |
| `USER_TEMPLATES_PATH` | `<TEMPLATES_PATH>/user` | writable directory for user-saved templates (`.dwxmz` + `.doc.json` provenance sidecars). On-prem only (steering §10 Q4); in SaaS the app's Postgres `flow_templates` table is the system of record and this path is unused for user state. An unwritable dir does not fail the solve — see [`docs/api.md`](docs/api.md#post-flowsheetsbuild-solve). |
| `WORKER_PATH` | `/app/worker/DwsimRunner.Worker.dll` | worker assembly |
| `SOLVE_TIMEOUT_SECONDS` | `60` | default per-solve timeout (caller cap 600) |
| `MAX_CONCURRENT_SOLVES` | `4` (images set `6`) | worker process pool size (SC-006 target). The code falls back to 4; `Dockerfile` and both compose files set 6, so bare metal gets 4 unless you set it. `/health`'s `maxConcurrent` reports the effective value |
| `CACHE_SIZE` | `256` | bounded LRU result cache entries |
| `RUNNER_API_KEY` | _(unset)_ | optional shared API key; when set, `X-Api-Key` required on all routes except `GET /health` (FR-016). Clients read it from `SIM_RUNNER_API_KEY` |
| `DOCS_ENABLED` | `true` | serve `/openapi.json` + `/docs`. Set `false` to remove both entirely |
| `BUILD_REF` | `unknown` | which runner build this is (Docker ARG), reported by `/health`. `dwsimVersion` is the DWSIM *library* version and cannot tell two runner builds apart |

## Testing

Two tiers (Constitution IX, test-first):

- **Tier A** — `tests/DwsimRunner.Api.Tests/`: API tests against a `FakeWorker`
  stub (no DWSIM required, CI-safe). Covers routing, validation, the error
  taxonomy, cache, queue-cap, /compare, introspection, unitOps, auth, and the
  generated OpenAPI document (`OpenApiContractTests`).
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