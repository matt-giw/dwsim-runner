#!/usr/bin/env bash
# fetch-dwsim.sh — download the pinned DWSIM Linux release and extract its
# assemblies into ./dwsim/ (the build input for the Worker and SaaS image).
#
# We never commit or redistribute these binaries (see .gitignore and the
# repo constitution): SaaS bundles them into our own internal containers
# only; on-prem customers install DWSIM themselves.
#
# Usage:
#   scripts/fetch-dwsim.sh              # fetch pinned version into ./dwsim/
#   DWSIM_VERSION=9.0.4 scripts/fetch-dwsim.sh
#   scripts/fetch-dwsim.sh /custom/out/dir

set -euo pipefail

DWSIM_VERSION="${DWSIM_VERSION:-9.0.5}"   # supported version — bump deliberately
REPO="DanWBR/dwsim"
ASSET="dwsim_${DWSIM_VERSION}-amd64.deb"
URL="https://github.com/${REPO}/releases/download/v${DWSIM_VERSION}/${ASSET}"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out_dir="${1:-${script_dir}/../dwsim}"
out_dir="$(mkdir -p "$out_dir" && cd "$out_dir" && pwd)"

if [[ -f "$out_dir/DWSIM.Automation.dll" ]]; then
  echo "DWSIM already present at $out_dir — delete it to re-fetch." >&2
  exit 0
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

echo "Downloading DWSIM v${DWSIM_VERSION} (${ASSET})..."
curl -fL --retry 3 -o "$work/$ASSET" "$URL"

echo "Extracting .deb..."
cd "$work"
if command -v dpkg-deb >/dev/null 2>&1; then          # debian/ubuntu (docker build stage)
  dpkg-deb -x "$ASSET" extracted
elif command -v ar >/dev/null 2>&1; then              # macOS / generic
  mkdir extracted && ar x "$ASSET"
  tar -xf data.tar.* -C extracted                      # tar auto-detects xz/zst if installed
else
  echo "error: need dpkg-deb or ar to extract a .deb" >&2
  exit 1
fi

# Locate the managed assemblies inside the package (path varies by release).
automation_dll="$(find extracted -name DWSIM.Automation.dll -print -quit)"
if [[ -z "$automation_dll" ]]; then
  echo "error: DWSIM.Automation.dll not found in $ASSET — package layout changed?" >&2
  exit 1
fi

src_dir="$(dirname "$automation_dll")"
echo "Assemblies found in ${src_dir#extracted/}"
cp -R "$src_dir/." "$out_dir/"

count="$(find "$out_dir" -name '*.dll' | wc -l | tr -d ' ')"
echo "Done: $count DLLs in $out_dir"
echo "Build with: export DWSIM_PATH=$out_dir && dotnet publish src/DwsimRunner.Api ..."
