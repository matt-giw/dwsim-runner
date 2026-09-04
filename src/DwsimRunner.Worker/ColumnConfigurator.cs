// dwsim-runner Worker — GPL-3.0
// DistillationColumn binding (T032): the rigorous column does not use the
// generic port/property surface — it has dedicated connection methods
// (ConnectFeed/ConnectDistillate/…), a two-slot spec system
// (SetCondenserSpec/SetReboilerSpec), and per-stage pressures. This class owns
// that translation so FlowsheetBuilder stays generic.
//
// Parameter contract (catalog):
//   numberOfStages      → Column.SetNumberOfStages (applied before any feed connect)
//   feedStage (1-based) → ConnectFeed(stream, stage-1)
//   refluxRatio         → SetCondenserSpec("Reflux Ratio", …)
//   distillateMolarFlow → SetCondenserSpec("Product Molar Flow Rate", …) — alternative to refluxRatio
//   bottomsMolarFlow    → SetReboilerSpec("Product Molar Flow Rate", …)
//   condenserPressure   → Stages[0].P   (bar/kPa/… converted to Pa)
//   reboilerPressure    → Stages[^1].P; middle stages interpolated linearly in Finish()
//   solvingMethod       → Column.SolvingMethodName (spec 143)
//   maxIterations       → Column.MaxIterations     (spec 143)

using System.Text.Json;
using DWSIM.Interfaces;
using DWSIM.UnitOperations.UnitOperations;

namespace DwsimRunner.Worker;

internal static class ColumnConfigurator
{
    /// <summary>Column-owned connection handling. Returns false when the unit
    /// is not a rigorous column (caller falls back to the generic port path).</summary>
    public static bool TryConnect(ISimulationObject unitObj, string portName,
        ISimulationObject streamObj, FlowObject unitDoc)
    {
        if (unitObj is not DistillationColumn col) return false;

        switch (portName)
        {
            case "Feed":
                EnsureStages(col, unitDoc);
                col.ConnectFeed(streamObj, FeedStageIndex(col, unitDoc));
                return true;
            case "Distillate":
                col.ConnectDistillate(streamObj);
                return true;
            case "Bottoms":
                col.ConnectBottoms(streamObj);
                return true;
            case "Condenser Duty":
                col.ConnectCondenserDuty(streamObj);
                return true;
            case "Reboiler Duty":
                col.ConnectReboilerDuty(streamObj);
                return true;
            default:
                throw new InvalidOperationException(
                    $"distillationColumn has no port '{portName}'; valid: Feed, Distillate, Bottoms, Condenser Duty, Reboiler Duty");
        }
    }

    public static bool Handles(string paramName) => paramName is
        "numberOfStages" or "feedStage" or "refluxRatio" or "distillateMolarFlow"
        or "bottomsMolarFlow" or "condenserPressure" or "reboilerPressure"
        or "solvingMethod" or "maxIterations";

    // ── 143: the solver was never selected ────────────────────────────────
    // DWSIM 9.0.5.0 ships FOUR column solvers and `Column.Calculate` picks between them by
    // SUBSTRING of `SolvingMethodName` — "Modified" first, then "Bubble", "Napthali", "Rates";
    // anything else is looked up in an external-solver dictionary and, failing that, throws
    // `Unable to find column solver with name '{0}'` from inside Calculate. The constructor
    // default is "Wang-Henke (Bubble Point)", the bubble-point method — the classic wrong
    // choice at high reflux and strong non-ideality, and the one every observed failure stack
    // came from. The runner set none of this, so every column in the platform ran the default.
    //
    // The document vocabulary is OURS and closed, mapped here to the engine's strings, for
    // two reasons that are really one: DWSIM's own spelling is a typo ("Napthali"), and an
    // unrecognised name fails deep inside the solve rather than at the binder. A closed map
    // makes an unknown method a typed build refusal listing what IS available — 141 FR-001.
    private static readonly Dictionary<string, string> Methods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wangHenke"] = "Wang-Henke (Bubble Point)",
        ["modifiedWangHenke"] = "Modified Wang-Henke (Bubble Point)",
        ["naphtaliSandholm"] = "Napthali-Sandholm (Simultaneous Correction)",
        ["burninghamOtto"] = "Burningham-Otto (Sum Rates)",
    };

    /// <summary>The document-facing solver names, for error text and for the catalog.</summary>
    public static IReadOnlyCollection<string> MethodNames => Methods.Keys;

    public static void Apply(ISimulationObject column, string paramName, JsonElement raw)
    {
        if (column is not DistillationColumn col)
            throw new InvalidOperationException($"'{paramName}' is only supported on distillationColumn");

        switch (paramName)
        {
            case "numberOfStages":
                SetStageCount(col, AsInt(raw));
                return;
            case "feedStage":
                return;   // consumed by TryConnect(Feed) — validated structurally
            case "refluxRatio":
                col.SetCondenserSpec("Reflux Ratio", AsDouble(raw), "", "");
                return;
            case "distillateMolarFlow":
                col.SetCondenserSpec("Product Molar Flow Rate", AsDouble(raw), AsUnit(raw) ?? "mol/s", "");
                return;
            case "bottomsMolarFlow":
                col.SetReboilerSpec("Product Molar Flow Rate", AsDouble(raw), AsUnit(raw) ?? "mol/s", "");
                return;
            case "condenserPressure":
                col.Stages[0].P = ToPa(raw);
                return;
            case "reboilerPressure":
                col.Stages[^1].P = ToPa(raw);
                return;
            case "solvingMethod":
            {
                var name = AsString(raw);
                if (!Methods.TryGetValue(name, out var engineName))
                    throw new InvalidOperationException(
                        $"solvingMethod '{name}' is not a solver this engine has; available: {string.Join(", ", Methods.Keys)}");
                col.SolvingMethodName = engineName;
                return;
            }
            case "maxIterations":
            {
                var n = AsInt(raw);
                if (n is < 1 or > MaxIterationsCeiling)
                    throw new InvalidOperationException(
                        $"maxIterations is {n}; it must be between 1 and {MaxIterationsCeiling}");
                col.MaxIterations = n;
                return;
            }
            default:
                throw new InvalidOperationException($"ColumnConfigurator has no handler for '{paramName}'");
        }
    }

    // 143 FR-006 — the runner's iteration budget, replacing DWSIM's constructor default of 100.
    //
    // MEASURED, on 36 documents (3 systems × 4 stage counts × 3 reflux ratios,
    // `specs/143-column-solver-selection/results.md`): 100 solves 18 of them, 300 solves 25,
    // 1000 solves 28 — and every one of those 28 converges inside the deployed 60 s
    // `SOLVE_TIMEOUT_SECONDS`, the slowest at 18.9 s. No document that converged at 100 fails at
    // 1000; the converged set is a strict superset, which is what FR-006 requires.
    //
    // The cost is that a column which will NOT converge now spends up to 10× longer proving it,
    // and past 60 s that is a timeout rather than the engine's "maximum number of iterations"
    // message. Bounded by the timeout either way, and the app now reports a column timeout as a
    // non-convergence rather than throwing — so the diagnosis survives the trade.
    //
    // NOT a method change: Naphtali-Sandholm solves 26 and is better on nine documents, but it
    // loses methanol/water at 30 stages / reflux 3.0, which Wang-Henke converges in 5.6 s and NS
    // does not finish in 300. One regression is one too many (FR-006). It is one `solvingMethod`
    // away for anyone who wants it.
    private const int DefaultMaxIterations = 1000;

    /// <summary>Post-parameter pass: the linear stage-pressure profile between the condenser and
    /// reboiler pressures, and the runner's iteration budget when the document did not state one.</summary>
    public static void Finish(ISimulationObject column, FlowObject unitDoc)
    {
        if (column is not DistillationColumn col) return;

        // Applied here rather than at construction so an explicit `maxIterations` always wins —
        // the document is the authority, and a default that overwrote it would be the silent
        // no-op class this repo keeps finding (023/038).
        var stated = unitDoc.Parameters is { } prms
            && prms.Keys.Any(k => string.Equals(k, "maxIterations", StringComparison.OrdinalIgnoreCase));
        if (!stated) col.MaxIterations = DefaultMaxIterations;

        if (col.Stages.Count < 3) return;
        var top = col.Stages[0].P;
        var bottom = col.Stages[^1].P;
        if (top <= 0 || bottom <= 0) return;
        var n = col.Stages.Count;
        for (var i = 1; i < n - 1; i++)
            col.Stages[i].P = top + (bottom - top) * i / (n - 1);
    }

    // The two request-supplied integers that buy engine work per unit. `maxIterations` is the
    // ITERATION BOUND the wall-clock watchdog (Watchdog.cs) is the wall-clock half of; without it
    // the deadline is the only thing standing between a caller and an arbitrarily long solve, and
    // a bound that only ever fires as a timeout costs the full deadline every time. `numberOfStages`
    // is FND-0102's cap in a second array — a column allocates per stage at construction.
    //
    // Both are ~10x anything real: spec 143 measured 216 solves across a 16/20/30-stage probe grid
    // and found 1000 iterations sufficient for every case that converges at all (100 was the
    // binding constraint; 1000 is the shipped default, DefaultMaxIterations below).
    private const int MaxIterationsCeiling = 10_000;
    private const int MaxStages = 300;

    private static void SetStageCount(DistillationColumn col, int n)
    {
        if (n > MaxStages)
            throw new InvalidOperationException($"numberOfStages is {n}; this runner allows at most {MaxStages}");
        if (n < 3) throw new InvalidOperationException($"numberOfStages is {n}; a column needs at least 3 stages");
        if (col.NumberOfStages != n)
        {
            col.NumberOfStages = n;
            col.SetNumberOfStages(n);
        }
    }

    // numberOfStages must be applied before ConnectFeed places the feed on a
    // stage — connections run before the parameter pass in the builder.
    private static void EnsureStages(DistillationColumn col, FlowObject unitDoc)
    {
        if (unitDoc.Parameters is { } prms && prms.TryGetValue("numberOfStages", out var rawN))
            SetStageCount(col, AsInt(rawN));
    }

    private static int FeedStageIndex(DistillationColumn col, FlowObject unitDoc)
    {
        var stage = unitDoc.Parameters is { } prms && prms.TryGetValue("feedStage", out var rawS)
            ? AsInt(rawS) : (col.NumberOfStages + 1) / 2;
        return Math.Clamp(stage - 1, 0, Math.Max(col.NumberOfStages - 1, 0));
    }

    private static string AsString(JsonElement e) =>
        (e.ValueKind == JsonValueKind.Object ? e.GetProperty("value").GetString() : e.GetString())
        ?? throw new InvalidOperationException("expected a string value");
    private static int AsInt(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.GetProperty("value").GetInt32() : e.GetInt32();
    private static double AsDouble(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.GetProperty("value").GetDouble() : e.GetDouble();
    private static string? AsUnit(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("unit", out var u) ? u.GetString() : null;
    private static double ToPa(JsonElement e)
    {
        var v = AsDouble(e);
        var unit = AsUnit(e);
        return unit is { Length: > 0 }
            ? DWSIM.SharedClasses.SystemsOfUnits.Converter.ConvertToSI(unit, v)
            : v;
    }
}
