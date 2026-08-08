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
                if (n < 1) throw new InvalidOperationException($"maxIterations is {n}; it must be at least 1");
                col.MaxIterations = n;
                return;
            }
            default:
                throw new InvalidOperationException($"ColumnConfigurator has no handler for '{paramName}'");
        }
    }

    /// <summary>Post-parameter pass: linear stage-pressure profile between the
    /// condenser and reboiler pressures.</summary>
    public static void Finish(ISimulationObject column)
    {
        if (column is not DistillationColumn col || col.Stages.Count < 3) return;
        var top = col.Stages[0].P;
        var bottom = col.Stages[^1].P;
        if (top <= 0 || bottom <= 0) return;
        var n = col.Stages.Count;
        for (var i = 1; i < n - 1; i++)
            col.Stages[i].P = top + (bottom - top) * i / (n - 1);
    }

    private static void SetStageCount(DistillationColumn col, int n)
    {
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
