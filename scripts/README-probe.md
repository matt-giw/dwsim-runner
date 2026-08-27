# DWSIM engine probes

Ground-truth extraction from a **running** DWSIM desktop instance. Written for spec
`199-unitop-mode-derivation`; the output is the reference side of any unit-op parameter work and the
regression detector for DWSIM upgrades.

Where 005 disassembled the DLL with `monodis` (one unit op at a time), these read the live engine and
cover every unit op in one pass.

## What they produce

| Script | Output | Contents |
|--------|--------|----------|
| `probe.py` | `unitop_params.tsv` | Per `ObjectType`: CLR class, `GetProperties(ALL)` ids with SI unit and default, every public CLR property, every enum with its members |
| `probe2.py` | `enum_values.tsv` | Every enum in the UnitOperations/Interfaces namespaces with **integer values** — needed because member ordinals differ per unit op |

## Running them

They cannot be run from a shell. DWSIM's macOS IronPython has a broken filesystem layer
(`Mono.Unix.Native.Syscall` throws on any `open()` or `import`), so the scripts reach .NET reflection
through an in-scope object and are loaded via a one-line bootstrap.

1. Open DWSIM. Any flowsheet will do — the probe adds objects and deletes them again, leaving the
   flowsheet as it found it. A property package is NOT required.
2. Edit `OUT` at the top of the script to an absolute path you can read.
3. Open the **Script Manager** tab, create a script, and paste **one line**:

```python
C=Flowsheet.GetType().GetType().Assembly; B=C.GetType("System.Reflection.BindingFlags"); F=B.GetField("InvokeMethod").GetValue(None)|B.GetField("Static").GetValue(None)|B.GetField("Public").GetValue(None); exec(C.GetType("System.IO.File").InvokeMember("ReadAllText",F,None,None,("/absolute/path/to/probe.py",)))
```

4. Press Run. The Log Panel reports `PROBE DONE: <n> lines -> <path>`.

Do not paste the script bodies directly into the editor — they are indentation-sensitive and the
editor auto-indents.

## Gotchas

- `BindingFlags` must be real enum values. Passing the integer `280` fails with
  *"Cannot convert numeric value 280 to BindingFlags."*
- 25 of 73 `ObjectType` values are not instantiable (drawing objects, `CapeOpenUO`, `AirCooler2`,
  `RefluxedAbsorber`, …). They are recorded as `ADD_ERR`, which is expected, not a failure.
- Six unit ops return ZERO properties from `GetProperties(ALL)` (`Mixer`, `Switch`, `Input`,
  `CustomUO`, `FlowsheetUO`, `RCT_GibbsReaktoro`). Their inputs exist only as CLR properties — read
  the `CLRPROP` rows for those.
- `NodeIn`/`Mixer` and `NodeOut`/`Splitter` are aliased pairs: two enum values, one implementation.

## When to re-run

On every DWSIM version bump. Diff the new `enum_values.tsv` against the committed one — a changed
ordinal silently changes which parameter the engine reads, and nothing else in the stack will notice.
