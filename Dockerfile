# dwsim-runner — SaaS image (GPL-3.0)
# Bundles DWSIM inside the container. Fine for SaaS: the container never
# leaves your infrastructure, so no conveyance to users occurs.
# Do NOT ship this image to on-prem customers — use Dockerfile.onprem.

# ── build ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# DWSIM Linux release binaries, extracted into ./dwsim/ before building:
#   e.g. download from https://dwsim.org → extract → ./dwsim/DWSIM.Automation.dll ...
COPY dwsim/ /opt/dwsim/
ENV DWSIM_PATH=/opt/dwsim

COPY src/ ./src/
RUN dotnet publish src/DwsimRunner.Api    -c Release -o /out/api && \
    dotnet publish src/DwsimRunner.Worker -c Release -o /out/worker

# ── runtime ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends \
        libfontconfig1 libgdiplus libc6-dev \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /opt/dwsim /opt/dwsim
COPY --from=build /out/api    /app/api
COPY --from=build /out/worker /app/worker
COPY templates/ /templates/

ENV DWSIM_PATH=/opt/dwsim \
    TEMPLATES_PATH=/templates \
    WORKER_PATH=/app/worker/DwsimRunner.Worker.dll \
    LD_LIBRARY_PATH=/opt/dwsim \
    SOLVE_TIMEOUT_SECONDS=60 \
    MAX_CONCURRENT_SOLVES=4

EXPOSE 8080
WORKDIR /app/api
ENTRYPOINT ["dotnet", "DwsimRunner.Api.dll"]
