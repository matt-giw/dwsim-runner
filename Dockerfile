# dwsim-runner — SaaS image (GPL-3.0)
# Bundles DWSIM inside the container. Fine for SaaS: the container never
# leaves your infrastructure, so no conveyance to users occurs.
# Do NOT ship this image to on-prem customers — use Dockerfile.onprem.

# ── build ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# DWSIM's Linux release binaries are gitignored and never redistributed, so they
# cannot be COPY'd from the build context on a clean clone (a CI or Railway build
# has no ./dwsim/). Fetch them here instead — same pinned release the local
# script pulls, cached as one layer, extracted straight into the image.
COPY scripts/fetch-dwsim.sh ./scripts/
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && scripts/fetch-dwsim.sh /opt/dwsim
ENV DWSIM_PATH=/opt/dwsim

COPY src/ ./src/
RUN dotnet publish src/DwsimRunner.Api    -c Release -o /out/api && \
    dotnet publish src/DwsimRunner.Worker -c Release -o /out/worker

# ── runtime ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
# 147 US2 (FR-006) — the commit this image was built from, surfaced at /health so a consumer can
# tell two builds apart. `dwsimVersion` is the DWSIM library and cannot: the engine deployed on
# 2026-07-30 and the one pinned on 2026-08-08 report identical health while disagreeing about
# which flash types they accept. Railway passes this with `--build-arg BUILD_REF=$RAILWAY_GIT_COMMIT_SHA`;
# unset stays the literal "unknown", which is a REPORTABLE state, not an absent field.
ARG BUILD_REF=unknown
ENV BUILD_REF=${BUILD_REF}
# coinor-libipopt1v5: DWSIM's own release ships /opt/dwsim/libIpopt39.so as a
# SYMLINK to /usr/lib/libipopt.so.1 and expects the distro to provide the target.
# Without it the symlink dangles and any solve reaching the nonlinear solver kills
# the worker outright — `DllNotFoundException: Unable to load shared library
# 'Ipopt39'`, exit 134, no result. Measured 2026-08-08 on the eval corpus: 13 of
# 18 NRTL cases died on this; the same documents converge under PR and SRK, so it
# reads as a property-package limitation rather than a missing runtime dependency.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libfontconfig1 libgdiplus libc6-dev curl coinor-libipopt1v5 \
    && rm -rf /var/lib/apt/lists/*

# Non-root: the API + spawned worker processes never need root. DWSIM writes
# temp files to the OS temp dir, so chown that for the runner user.
RUN useradd --system --uid 10001 --create-home --home-dir /home/runner runner \
    && mkdir -p /tmp/dwsim \
    && chown -R runner:runner /tmp/dwsim
ENV TMPDIR=/tmp/dwsim

COPY --from=build /opt/dwsim /opt/dwsim
COPY --from=build /out/api    /app/api
COPY --from=build /out/worker /app/worker
COPY templates/ /templates/

ENV DWSIM_PATH=/opt/dwsim \
    TEMPLATES_PATH=/templates \
    WORKER_PATH=/app/worker/DwsimRunner.Worker.dll \
    LD_LIBRARY_PATH=/opt/dwsim \
    SOLVE_TIMEOUT_SECONDS=60 \
    MAX_CONCURRENT_SOLVES=6

EXPOSE 8080
WORKDIR /app/api
USER runner
# /health stays open even when RUNNER_API_KEY is set, so the orchestrator probe works.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -sf http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "DwsimRunner.Api.dll"]
