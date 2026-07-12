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
RUN apt-get update && apt-get install -y --no-install-recommends \
        libfontconfig1 libgdiplus libc6-dev curl \
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
