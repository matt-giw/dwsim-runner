#!/usr/bin/env bash
# verify-no-conveyance.sh — mechanical proof that the on-prem image conveys
# ZERO DWSIM binaries (Constitution IV). The on-prem image is what a customer
# receives, so this is the load-bearing no-conveyance guarantee.
#
# Approach: build Dockerfile.onprem, docker save the image to a tarball, and
# scan every layer's files for anything matching DWSIM.* (case-insensitive).
# A clean image exits 0; any DWSIM file is a hard failure.
#
# Requires: docker, build context with ./dwsim/ present for the SDK build stage
# (those DLLs are used for compile metadata only and are NOT copied into the
# onprem runtime image — the Dockerfile asserts this at build time too; this
# script re-verifies the final image independently).
#
# Usage:
#   scripts/verify-no-conveyance.sh
#   IMAGE_TAG=iskra-runner-onprem:check scripts/verify-no-conveyance.sh

set -euo pipefail

IMAGE_TAG="${IMAGE_TAG:-iskra-runner-onprem:check}"
TARBALL="$(mktemp -t dwsim-onprem-XXXXXX.tar)"
WORKDIR="$(mktemp -d -t dwsim-onprem-XXXXXX)"
trap 'rm -rf "$TARBALL" "$WORKDIR"' EXIT

echo "→ Building on-prem image ($IMAGE_TAG)…"
docker build -f Dockerfile.onprem -t "$IMAGE_TAG" .

echo "→ Saving image to scan layers…"
docker save "$IMAGE_TAG" -o "$TARBALL"

echo "→ Extracting image layers…"
tar -xf "$TARBALL" -C "$WORKDIR"

# Modern docker (OCI format) lays layers out as compressed tars under
# blobs/sha256/<sha>, indexed by index.json → image index → per-platform
# manifest. Walk that chain and scan every layer's file list for DWSIM.*.
# A layer blob is gzip-compressed; list its entries with `gzip -dc | tar -t`.
leaks=""
blob_dir="$WORKDIR/blobs/sha256"
if [ ! -d "$blob_dir" ]; then
    echo "✗ unrecognized image format (no blobs/sha256 dir)" >&2
    exit 1
fi

scan_layer() {
    local blob="$1"
    # Only tar+gzip layers have file entries; attestation/json blobs are skipped.
    if ! gzip -t "$blob" 2>/dev/null; then return; fi
    local found
    found=$(gzip -dc "$blob" | tar -t 2>/dev/null | grep -iE '(^|/)DWSIM\.[^/]+$' || true)
    if [ -n "$found" ]; then
        leaks="${leaks}${leaks:+$'\n'}${found}"
    fi
}

layer_count=0
for blob in "$blob_dir"/*; do
    [ -f "$blob" ] || continue
    gzip -t "$blob" 2>/dev/null || continue   # only layers are gzip
    layer_count=$((layer_count + 1))
    scan_layer "$blob"
done

echo "→ Scanned ${layer_count} layer(s)."

if [ -n "$leaks" ]; then
    echo "✗ NO-CONVEYANCE VIOLATION — DWSIM files found in the on-prem image:" >&2
    echo "$leaks" >&2
    exit 1
fi

echo "✓ on-prem image is clean of DWSIM.* — no conveyance (Constitution IV)"