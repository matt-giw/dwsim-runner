# dwsim-runner (GPL-3.0)

Headless DWSIM solve service. Two processes:

- **DwsimRunner.Api** — ASP.NET Core minimal API. Owns HTTP, queueing, timeouts.
  Never loads DWSIM into its own address space.
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
export DWSIM_PATH=/opt/dwsim          # a DWSIM install or extracted Linux release
dotnet publish src/DwsimRunner.Api    -c Release -o publish/api
dotnet publish src/DwsimRunner.Worker -c Release -o publish/worker
```

The published output does **not** copy DWSIM DLLs (`Private=false` on the
references) — at runtime the worker resolves them from `DWSIM_PATH` via an
`AssemblyResolve` hook. This is what makes the on-prem "customer installs
DWSIM separately" model work with a single build.

## Running

```bash
export DWSIM_PATH=/opt/dwsim
export TEMPLATES_PATH=./templates
export WORKER_PATH=./publish/worker/DwsimRunner.Worker.dll
export SOLVE_TIMEOUT_SECONDS=60
dotnet publish/api/DwsimRunner.Api.dll   # listens on :8080
```

## API

| Route            | Body / result |
|------------------|---------------|
| `GET /health`    | `{ ok, dwsimPath, dwsimFound }` |
| `GET /templates` | list of `.dwxmz` template ids |
| `POST /solve`    | `{ templateId, overrides:[{object, property, value, unit?}], timeoutSeconds? }` → `{ converged, elapsedMs, streams:[...], energy:[...] }` |

## On-prem notes

- Customer installs DWSIM (any 8.x+ desktop or the extracted release) and
  points `DWSIM_PATH` at it. macOS: `/Applications/DWSIM.app/Contents/MonoBundle`.
- You ship: this repo's source + published binaries (GPL obligations: trivially
  satisfied, it's your own GPL code), plus the proprietary iskra app.
- You do NOT ship DWSIM. Version-check at startup (`/health` reports what it
  found) and document a supported DWSIM version range.

## License

GPL-3.0. This service references DWSIM (GPL-3.0) assemblies and is licensed
accordingly. The iskra application communicates with this service exclusively
over HTTP and is a separate, independent work.
