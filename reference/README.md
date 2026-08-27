# Engine reference dumps

Ground truth extracted from DWSIM 9.0.5.0, for answering "what does the engine actually declare"
without a GUI, a live engine, or a guess.

## Why these are HERE and not in `iskra-platform`

Every row names a DWSIM class (`DWSIM.UnitOperations.UnitOperations.Compressor`). The platform repo
is the **proprietary zone** and Constitution II forbids copying DWSIM internals into it — a rule
already enforced in code: `iskra-app/scripts/capture-catalog.ts` refuses to write any captured payload
matching `/dwsim/i`, with the comment *"the proprietary zone must not carry DWSIM references."*

Spec 199 FR-007 put the extraction SCRIPTS in this repo for the same reason. Their output belongs
beside them. (These files spent a short time at the platform repo root; spec 200 US6 moved them.)

## The files

| File | Rows | Contents |
|---|---|---|
| `dwsim_unitop_enums.csv` | 152 | every enum-typed property per object type, with its members — `object_type, clr_property, enum_type, members` |
| `dwsim_unitop_reference.csv` | 674 | every parameter id with **its SI unit** and default — `object_type, status, clr_class, param_id, id_style, unit, default` |
| `enum_values.tsv` | 738 | enum members with their integer values |

## What they are good for

- **The unit.** `dwsim_unitop_reference.csv` is the only source here that gives a parameter's SI unit.
  `Compressor.PolytropicHead` is in **metres** — head as a length, not a pressure. Guessing that wrong
  is spec 036's `overallUA` mistake, a 1000× error that converges with no warning; `UnitOpCatalog`
  records that incident in full.
- **Corroboration.** They independently confirm what `scripts/modes.py` derives from the assembly:
  `Heater.CalcMode` has 6 members, `Cooler.CalcMode` has 5. Spec 199's live GUI reflection agrees,
  making three derivations from three methods.
- **`id_style`.** 391 of the parameter ids are opaque (`PROP_XX_n`) and cannot be name-matched; the
  column says which, so a name-based analysis knows what it is blind to instead of silently missing it.

## What they are NOT

**A substitute for probing.** These say what the engine *declares* — that a property exists, its type
and its unit. They cannot say whether the engine READS it. That gap is the entire subject of spec 199:
five parameters were declared, accepted, converged and ignored. Settable is necessary and not
sufficient; `iskra-app/packages/engine-contract/probes.ts` is where sufficiency is measured.

**Nor are they self-refreshing.** They are a snapshot of 9.0.5.0. On a DWSIM version bump, re-run and
diff — a changed enum ordinal silently changes which parameter the engine reads, and nothing else in
the stack notices.

## Regenerating

`scripts/modes.py` covers the enum half headlessly (`ikdasm` over the vendored DLL, no GUI). The
parameter reference with units comes from `scripts/probe.py`, which needs DWSIM's desktop Script
Manager — see `scripts/README-probe.md` for why, and treat extending `modes.py` to cover it as the
better fix.
