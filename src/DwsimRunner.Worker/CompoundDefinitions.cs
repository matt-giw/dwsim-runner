// dwsim-runner Worker — GPL-3.0
// iskra spec 281 (ISK-534) — compounds the engine does not ship, defined on the request.
//
// A definition is built in memory for the one job that carries it and registered on that job's
// Automation3. Nothing is written to disk and nothing survives the process: measured 2026-10-04
// (DWSIM 9.0.5.0) — AvailableCompounds is a plain dictionary, and a second Automation3 does not
// see an addition to the first. The library these come from lives in the caller, not here.
//
// The wire carries PHYSICS in one stated unit per value. Everything the engine needs beyond that —
// the per-kilogram formation properties, the per-kmol heat capacity, the placeholders an equation
// of state demands of every compound — is added in `Build`, here and nowhere else.

using System.Text.Json;
using DWSIM.Interfaces.Enums;
using DWSIM.Thermodynamics.BaseClasses;

namespace DwsimRunner.Worker;

/// <summary>One parsed definition. `Values` holds every required key, in the unit `SolidValues` names.</summary>
public sealed record CompoundDefinition(string Name, string? Formula, string? CasNumber, string Kind,
    IReadOnlyDictionary<string, double> Values);

public static class CompoundDefinitions
{
    public const int Max = 50;

    /// <summary>
    /// What a `solid` must carry, and the ONE unit each value is accepted in. One unit, not a list:
    /// DWSIM's converter returns an unknown unit's value unchanged, so every extra spelling is a
    /// chance to read 1538 °C as 1538 K. A caller converts; this runner does not guess.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Unit, bool Positive)> SolidValues =
    [
        ("molarWeight", "g/mol", true),
        ("meltingPoint", "K", true),
        ("enthalpyOfFusion", "kJ/mol", false),
        ("solidDensity", "kg/m3", true),
        ("solidHeatCapacity", "J/[mol.K]", true),
        ("enthalpyOfFormation", "kJ/mol", false),
        ("gibbsEnergyOfFormation", "kJ/mol", false),
    ];

    /// <summary>
    /// Package id → the flash setting under which the engine places a defined solid in a SOLID
    /// phase. Every entry is pinned by a live test (SolidsTests) that measures it; an entry without
    /// one does not ship.
    ///
    /// Measured 2026-10-04: Raoult with this setting is right at 25 °C (iron + water) and at 700 °C
    /// (Fe + H2O + Fe3O4 + H2). Peng-Robinson is NOT here: at 25 °C with water it is wrong under
    /// every setting — iron liquid by default, water labelled solid under this one.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (FlashSetting Setting, string Value)> SolidsAllowlist =
        new Dictionary<string, (FlashSetting, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["RAOULT"] = (FlashSetting.HandleSolidsInDefaultEqCalcMode, "True"),
        };

    /// <summary>
    /// Parse and check `compoundDefinitions`. Pure — no engine type is touched — and collect-all:
    /// every problem found is added to <paramref name="issues"/>, and only clean definitions are returned.
    /// </summary>
    public static List<CompoundDefinition> Parse(JsonElement? element, IEnumerable<string> compounds, List<BuildIssue> issues)
    {
        var defs = new List<CompoundDefinition>();
        if (element is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return defs;

        void Error(string code, string message, string? path = "compoundDefinitions") =>
            issues.Add(new BuildIssue("error", code, null, path, message));

        if (el.ValueKind != JsonValueKind.Array)
        {
            Error("COMPOUND_DEFINITION_INVALID", "compoundDefinitions must be an array");
            return defs;
        }
        if (el.GetArrayLength() > Max)
        {
            Error("DOCUMENT_TOO_LARGE", $"compoundDefinitions has {el.GetArrayLength()} entries (max {Max})");
            return defs;
        }

        var used = new HashSet<string>(compounds, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = -1;
        foreach (var d in el.EnumerateArray())
        {
            index++;
            var path = $"compoundDefinitions[{index}]";
            var before = issues.Count;

            var name = Str(d, "name");
            if (name is not { Length: > 0 })
            {
                Error("COMPOUND_DEFINITION_INVALID", "a compound definition needs a name", path);
                continue;
            }
            if (!seen.Add(name))
                Error("COMPOUND_DEFINITION_CONFLICT", $"compound '{name}' is defined more than once", path);
            if (!used.Contains(name))
                Error("COMPOUND_DEFINITION_INVALID",
                    $"compound '{name}' is defined but is not in the request's compounds list", path);

            var kind = Str(d, "kind");
            if (kind == "fluid")
            {
                // Not measured: what the engine needs to SOLVE with a user-defined liquid or gas.
                Error("COMPOUND_KIND_UNSUPPORTED",
                    $"compound '{name}' is a fluid; this runner has only been measured solving with defined solids", path);
                continue;
            }
            if (kind != "solid")
            {
                Error("COMPOUND_DEFINITION_INVALID", $"compound '{name}' has kind '{kind}'; expected 'solid' or 'fluid'", path);
                continue;
            }

            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            var given = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("values", out var v)
                        && v.ValueKind == JsonValueKind.Object ? v : (JsonElement?)null;
            if (given is { } obj)
                foreach (var p in obj.EnumerateObject())
                    if (!SolidValues.Any(s => s.Key == p.Name))
                        Error("COMPOUND_DEFINITION_INVALID",
                            $"compound '{name}' carries '{p.Name}', which this runner does not read for a solid; " +
                            $"accepted: {string.Join(", ", SolidValues.Select(s => s.Key))}", $"{path}.values.{p.Name}");

            foreach (var (key, unit, positive) in SolidValues)
            {
                var vpath = $"{path}.values.{key}";
                if (given is not { } g || !g.TryGetProperty(key, out var q) || q.ValueKind != JsonValueKind.Object)
                {
                    Error("COMPOUND_DEFINITION_INVALID", $"compound '{name}' is missing '{key}' ({unit})", vpath);
                    continue;
                }
                if (Str(q, "unit") != unit)
                {
                    Error("INVALID_UNIT",
                        $"compound '{name}' gives '{key}' in '{Str(q, "unit")}'; accepted: {unit}", $"{vpath}.unit");
                    continue;
                }
                if (!q.TryGetProperty("value", out var num) || num.ValueKind != JsonValueKind.Number
                    || !num.TryGetDouble(out var value) || !double.IsFinite(value) || (positive && value <= 0))
                {
                    Error("COMPOUND_DEFINITION_INVALID",
                        $"compound '{name}' needs '{key}' to be a {(positive ? "positive " : "")}number", $"{vpath}.value");
                    continue;
                }
                values[key] = value;
            }

            if (issues.Count == before)
                defs.Add(new CompoundDefinition(name, Str(d, "formula"), Str(d, "casNumber"), kind, values));
        }
        return defs;

        static string? Str(JsonElement e, string property) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
    }

    /// <summary>
    /// Register definitions on the job's engine instance. MUST run before `CreateFlowsheet()`: that
    /// is the measured path — the flowsheet inherits the instance's compound table as it is created.
    /// </summary>
    public static void Register(DWSIM.Automation.Automation3 auto, IReadOnlyList<CompoundDefinition> defs, List<BuildIssue> issues)
    {
        for (var i = 0; i < defs.Count; i++)
        {
            var d = defs[i];
            var existing = auto.AvailableCompounds.Keys
                .FirstOrDefault(k => string.Equals(k, d.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                issues.Add(new BuildIssue("error", "COMPOUND_DEFINITION_CONFLICT", null, $"compoundDefinitions[{i}]",
                    $"compound '{d.Name}' is defined on the request but '{existing}' is already an engine compound"));
                continue;
            }
            auto.AvailableCompounds.Add(d.Name, Build(d, i));
        }
    }

    /// <summary>Every refusal code this file can raise — a build abort leads with one of these when present.</summary>
    public static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        "COMPOUND_DEFINITION_INVALID", "COMPOUND_DEFINITION_CONFLICT", "COMPOUND_KIND_UNSUPPORTED",
        "SOLIDS_UNSUPPORTED_PACKAGE", "INVALID_UNIT", "DOCUMENT_TOO_LARGE",
    };

    /// <summary>Null when the request may proceed; otherwise the sentence that refuses it.</summary>
    public static string? SolidsRefusal(IReadOnlySet<string> solids, string? enginePackageName)
    {
        if (solids.Count == 0 || enginePackageName is null) return null;
        var id = PackageCatalog.Classify(enginePackageName).Id;
        return SolidsAllowlist.ContainsKey(id) ? null
            : $"property package '{id}' has not been measured to place a solid in a solid phase, and this request defines " +
              $"{string.Join(", ", solids.Order().Select(s => $"'{s}'"))} as solid; measured packages: " +
              string.Join(", ", SolidsAllowlist.Keys.Order());
    }

    /// <summary>Apply the allowlisted flash setting. A request with no defined solid is not touched.</summary>
    public static void ApplySolidsSetting(DWSIM.Interfaces.IFlowsheet fs, IReadOnlySet<string> solids, string enginePackageName)
    {
        if (solids.Count == 0) return;
        var (setting, value) = SolidsAllowlist[PackageCatalog.Classify(enginePackageName).Id];
        foreach (var package in fs.PropertyPackages.Values)
            ((DWSIM.Thermodynamics.PropertyPackages.PropertyPackage)package).FlashSettings[setting] = value;
    }

    /// <summary>The names of the defined solids, for the package gate and the post-solve guard.</summary>
    public static HashSet<string> SolidNames(IEnumerable<CompoundDefinition> defs) =>
        new(defs.Where(d => d.Kind == "solid").Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

    internal static ConstantProperties Build(CompoundDefinition d, int index)
    {
        var mw = d.Values["molarWeight"];
        return new ConstantProperties
        {
            Name = d.Name,
            Formula = d.Formula ?? "",
            CAS_Number = d.CasNumber ?? "",
            // Above every id the engine's own databases use; unique within the request.
            ID = 900_000 + index,
            OriginalDB = "User",
            CurrentDB = "User",
            Molar_Weight = mw,

            IsSolid = true,
            TemperatureOfFusion = d.Values["meltingPoint"],
            EnthalpyOfFusionAtTf = d.Values["enthalpyOfFusion"],
            SolidTs = 298.15,
            SolidDensityAtTs = d.Values["solidDensity"],

            // PER KILOGRAM in the engine; kJ/mol on the wire. The engine's own naphthalene is
            // 1174.84 against the handbook's 150.6 kJ/mol at 128.171 g/mol — the test pins this line.
            IG_Enthalpy_of_Formation_25C = d.Values["enthalpyOfFormation"] / mw * 1000,
            IG_Gibbs_Energy_of_Formation_25C = d.Values["gibbsEnergyOfFormation"] / mw * 1000,

            // Equation "100" is the polynomial A + B·T + …, per KMOL; one constant term.
            SolidHeatCapacityEquation = "100",
            Solid_Heat_Capacity_Const_A = d.Values["solidHeatCapacity"] * 1000,
            Solid_Heat_Capacity_Tmin = 0,
            Solid_Heat_Capacity_Tmax = 1e4,

            // ── placeholders ─────────────────────────────────────────────────────────────────
            // Not physics, and never on the wire. Measured 2026-10-04: with none of these, PR
            // throws "Unable to calculate compressility factor" and Raoult reports the solid as a
            // liquid under every setting. They are what an equation of state asks of ANY compound:
            // a critical point far above anything a flowsheet reaches, and a vapour pressure that
            // evaluates to ~4e-44 Pa (equation "101", ln P = A) so the compound never enters the
            // vapour. SolidsTests varies them and asserts the reported phases do not move.
            Critical_Temperature = 10_000,
            Critical_Pressure = 1e9,
            Critical_Volume = 0.05,
            Critical_Compressibility = 0.27,
            Z_Rackett = 0.27,
            Acentric_Factor = 0.1,
            Normal_Boiling_Point = 5_000,
            NBP = 5_000,
            VaporPressureEquation = "101",
            Vapor_Pressure_Constant_A = -100,
            Vapor_Pressure_TMIN = 1,
            Vapor_Pressure_TMAX = 20_000,
            IdealgasCpEquation = "100",
            Ideal_Gas_Heat_Capacity_Const_A = d.Values["solidHeatCapacity"] * 1000,
        };
    }
}

/// <summary>
/// What a result may say about a solid. Pure: phases in, findings out, so both harvests — solve and
/// flash — share one rule and it is testable without an engine.
/// </summary>
public static class SolidsCheck
{
    /// <summary>Below this a compound's presence in a phase is the solver's arithmetic, not a finding.</summary>
    public const double Trace = 1e-6;

    /// <summary>
    /// Measured 2026-10-05 (T009): the PHASES a defined solid lands in do not move when its
    /// placeholders are varied, but the stream's ENTHALPY does — by up to 4x across Tc, Pc and the
    /// boiling point — because the engine reaches a solid's enthalpy through an ideal-gas reference
    /// and a vaporisation step the placeholders control. A duty computed from it is a number, and
    /// it is wrong. So every energy result on a request that defines a solid is withheld, by name.
    /// </summary>
    public const string EnergyWarning =
        "[SOLID_ENTHALPY_UNMEASURED] energy results withheld: the enthalpy of a defined solid depends on engine " +
        "placeholders, not on its physical values (measured 2026-10-05), so duties, powers, enthalpies and " +
        "entropies on this request are not reported";

    private static bool IsEnergyKey(string key) =>
        key.Contains("enthalpy", StringComparison.OrdinalIgnoreCase)
        || key.Contains("entropy", StringComparison.OrdinalIgnoreCase)
        || key.Contains("energy", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bag without its energy keys; null stays null, and an emptied bag becomes null.</summary>
    public static Dictionary<string, double>? WithoutEnergy(Dictionary<string, double>? properties)
    {
        if (properties is null) return null;
        var kept = properties.Where(kv => !IsEnergyKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return kept.Count > 0 ? kept : null;
    }

    private static bool IsSolidPhase(string phase) => phase.StartsWith("solid", StringComparison.OrdinalIgnoreCase);
    private static bool IsLiquidPhase(string phase) => phase.StartsWith("liquid", StringComparison.OrdinalIgnoreCase);

    /// <summary>A defined solid in a vapour or liquid phase. The caller turns any finding into a refusal.</summary>
    public static List<BuildIssue> SolidInFluid(string? tag,
        IEnumerable<(string Phase, IReadOnlyDictionary<string, double>? Composition)> phases, IReadOnlySet<string> solids)
    {
        var issues = new List<BuildIssue>();
        if (solids.Count == 0) return issues;
        foreach (var (phase, composition) in phases)
        {
            if (IsSolidPhase(phase) || composition is null) continue;
            foreach (var (compound, fraction) in composition)
                if (fraction > Trace && solids.Contains(compound))
                    issues.Add(new BuildIssue("error", "SOLID_REPORTED_AS_FLUID", tag, null,
                        $"{(tag is null ? "" : $"{tag}: ")}'{compound}' is defined as a solid but the engine placed it in the " +
                        $"{phase} phase (mole fraction {fraction:G4}); the result is refused rather than reported"));
        }
        return issues;
    }

    /// <summary>
    /// An ordinary compound that makes up most of a LIQUID phase below its own melting point.
    /// A warning, never a refusal: it cannot tell a precipitate from a solution. Naphthalene in
    /// water at 25 °C (a 94 % naphthalene "liquid") is the case it is for; naphthalene at 5 mol% in
    /// toluene is a true solution and stays silent.
    /// </summary>
    // ponytail: "more than half the phase" is a heuristic. It is wrong under strong freezing-point
    // depression (60 % water in glycol at -20 °C is a real liquid and will warn). The upgrade is a
    // solid-liquid equilibrium; until then this is a warning precisely because it can be wrong.
    public static List<string> BelowMeltingPoint(string? tag, double? temperatureC,
        IEnumerable<(string Phase, IReadOnlyDictionary<string, double>? Composition)> phases,
        IReadOnlyDictionary<string, double> meltingPointK, IReadOnlySet<string> solids)
    {
        var warnings = new List<string>();
        if (temperatureC is not { } tC) return warnings;
        foreach (var (phase, composition) in phases)
        {
            if (!IsLiquidPhase(phase) || composition is null) continue;
            foreach (var (compound, fraction) in composition)
            {
                if (fraction <= 0.5 || solids.Contains(compound)) continue;
                if (!meltingPointK.TryGetValue(compound, out var tfK) || !(tfK > 0)) continue;
                var tfC = tfK - 273.15;
                if (tC < tfC)
                    warnings.Add(
                        $"[BELOW_MELTING_POINT_AS_LIQUID] {(tag is null ? "" : $"{tag}: ")}{compound} is {fraction * 100:0}% of the {phase} phase " +
                        $"at {tC:0.##} C, below its melting point of {tfC:0.##} C; this solve does not model solid formation, " +
                        "so the liquid shown may be a solid in reality");
            }
        }
        return warnings;
    }
}
