#!/usr/bin/env python3
"""Enumerate every DWSIM calculation mode, headlessly, from the vendored assembly.

Spec 199 FR-007. This is the ground truth for "which unit ops have a calculation mode and what
are its members", and it is the DENOMINATOR for FR-006/SC-1 — a coverage requirement measured
against a hand-built table can be neither satisfied nor falsified.

Why not probe.py/probe2.py: those read a LIVE engine, but only through DWSIM's desktop Script
Manager (macOS IronPython has a broken filesystem layer — see README-probe.md). They cannot run
in CI, in the container, or on a machine with no GUI, which makes "reproducible" a claim rather
than a property. This reads the same facts out of the DLL we already ship.

Cross-checked against research.md R1, which was measured the other way (live engine reflection).
Both methods agree on every ordinal for pump/compressor/expander/valve/heater/cooler — two
independent derivations of the same table.

    python3 dwsim-runner/scripts/modes.py > specs/199-calculation-mode-input/modes.json

Requires `ikdasm` (homebrew: `brew install mono`).
"""
import json, re, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / "dwsim-runner" / "dwsim" / "DWSIM.UnitOperations.dll"

# Wire type -> the DWSIM class whose mode property we want, taken from UnitOpCatalog's ObjectType.
# Only CATALOGUED types appear: a mode on a unit op iskra cannot build is not a mode iskra can miss.
CATALOG = {
    "splitter": "Splitter", "separator": "Vessel", "heater": "Heater", "cooler": "Cooler",
    "heatExchanger": "HeatExchanger", "pump": "Pump", "compressor": "Compressor",
    "expander": "Expander", "valve": "Valve", "orificePlate": "OrificePlate",
    "reactorConversion": "Reactor", "reactorEquilibrium": "Reactor", "reactorGibbs": "Reactor",
    "reactorCSTR": "Reactor", "reactorPFR": "Reactor",
}
# The five reactors share ONE enum (DWSIM.UnitOperations.Reactors.OperationMode), so their mode
# lists are identical by construction — not by coincidence, and not to be copied five times.
SHARED = {"Reactor": "DWSIM.UnitOperations.Reactors.OperationMode"}

# Non-converging on the 2026-08-27 capture. Their modes are counted and NAMED rather than left
# implicit: FR-006 scopes to converging unit ops, and a silent omission is how a coverage claim
# rots into a number nobody can check.
NON_CONVERGING = {"pipe", "reactorCSTR"}


def normalize(member: str) -> str:
    """Engine enum member -> wire name. `Delta_P` and `DeltaP` both become `deltaP`.

    Uniqueness is claimed WITHIN a unit op and nowhere else: `outletPressure` is ordinal 1 on a
    pump and 0 on a compressor (research.md R1 hazard 1). Any encoding shared across unit ops —
    integer OR string — is unsafe, which is why this is only ever applied per type.
    """
    parts = [p for p in member.split("_") if p]
    head, *tail = parts
    return head[0].lower() + head[1:] + "".join(p[0].upper() + p[1:] for p in tail)


def main() -> int:
    if not DLL.exists():
        print(f"missing {DLL} — run dwsim-runner/scripts/fetch-dwsim.sh", file=sys.stderr)
        return 1
    il = subprocess.run(["ikdasm", str(DLL)], capture_output=True, text=True, check=True).stdout

    enums: dict[str, list[tuple[str, int]]] = {}
    for fqn, name, val in re.findall(
        r"\.field public static literal valuetype ([\w.`/]+) (\w+) = int32\(0x([0-9a-fA-F]+)\)", il
    ):
        enums.setdefault(fqn, []).append((name, int(val, 16)))

    # An enum-typed property whose name says "mode" or "method" IS the calculation-mode selector.
    props = {
        (t, n)
        for t, n in re.findall(r"\.property instance valuetype ([\w.`/]+)\s+(\w+)\(\)", il)
        if t in enums and re.search(r"mode|method", n, re.I)
    }

    out, unresolved = {}, []
    for wire, cls in sorted(CATALOG.items()):
        want = SHARED.get(cls)
        if want:
            hit = [(t, n) for t, n in props if t == want]
        else:
            # The enum is nested on the class (`...Pump/CalculationMode`) or named after it
            # (`...HeatExchangerCalcMode`). Both forms appear; neither is negotiable.
            hit = [(t, n) for t, n in props
                   if t.split("/")[0].split(".")[-1] == cls or t.split(".")[-1].startswith(cls)]
        if len(hit) != 1:
            unresolved.append((wire, cls, [t for t, _ in hit]))
            continue
        fqn, prop = hit[0]
        members = sorted(enums[fqn], key=lambda x: x[1])
        out[wire] = {
            "dwsimClass": cls,
            "clrProperty": prop,
            "enumType": fqn,
            "converges": wire not in NON_CONVERGING,
            # NOT called "default". research.md R1 says "ordinal 0 is the constructor default in
            # each case" and Vessel DISPROVES it: three IL sites store ordinal 1 (`Legacy`) through
            # set_CalculationMode, which is exactly what spec 166 measured and worked around. So the
            # effective default is a MEASUREMENT, not a property of the enum's numbering, and this
            # field says only what it knows.
            "ordinalZero": normalize(members[0][0]),
            "effectiveDefault": None,   # filled by the live probe; see spec 199 task T008a
            "modes": [{"name": normalize(m), "engineMember": m, "ordinal": v} for m, v in members],
        }

    if unresolved:
        for wire, cls, hits in unresolved:
            print(f"UNRESOLVED {wire} ({cls}): {hits}", file=sys.stderr)
        return 2

    counted = sum(len(v["modes"]) for v in out.values() if v["converges"])
    excluded = sum(len(v["modes"]) for v in out.values() if not v["converges"])
    print(json.dumps({
        "_source": "DWSIM.UnitOperations.dll 9.0.5.0, disassembled with ikdasm",
        "_method": "enum-typed property named *mode*/*method* on each catalogued unit op's class",
        "_note": "Cross-checked against research.md R1 (live engine reflection). Both agree on every ordinal.",
        "modeBearingUnitOps": len(out),
        "modesInScope": counted,
        "modesExcludedNonConverging": excluded,
        "unitOps": out,
    }, indent=2))
    print(f"{len(out)} mode-bearing catalogued unit ops, {counted} modes in scope "
          f"({excluded} excluded as non-converging)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
