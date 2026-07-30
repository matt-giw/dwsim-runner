// dwsim-runner Worker — GPL-3.0
// Spec 099 US1 — the water electrolyzer's one bespoke need: its power.
//
// The engine takes power on INPUT CONNECTOR 1 as an energy stream, and dereferences it
// unconditionally in both calculation modes — so a missing one is a null reference inside
// `Calculate`, not a degraded solve. But the document's standing invariant is that an energy port is
// a PARAMETER, never a nozzle (spec 024 FR-009): the canvas has no energy lines, and the co-pilot
// authors a duty as a number.
//
// So the document says `powerInput: { value, unit }` and this class synthesizes the energy stream
// the engine demands. The document never contains it, the app never sees it, and the result harvest
// skips it — otherwise the app folds back a stream that does not exist on its side.
//
// Modelled on `ColumnConfigurator`, including its "not my type → return false" guard, so the generic
// path still serves every other unit op.

using System.Text.Json;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.UnitOperations.UnitOperations;

namespace DwsimRunner.Worker;

internal static class ElectrolyzerConfigurator
{
    /// <summary>Tag suffix for a synthesized power stream. Namespaced deliberately: document tag
    /// uniqueness is validated over DOCUMENT tags only, so an un-namespaced tag could collide with
    /// one the engineer authored and the collision would surface as a mangled flowsheet.</summary>
    public const string PowerSuffix = "-POWER";

    /// <summary>True when this synthesized stream must be hidden from the result harvest.</summary>
    public static bool IsSynthesizedPower(string? tag) =>
        tag is not null && tag.EndsWith(PowerSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Give every electrolyzer in the document its power stream.
    /// </summary>
    /// <remarks>
    /// ONE EXTRA PASS IN `Build`, after connections and before the parameter loop — not a branch
    /// inside `ApplyParameter`, which has no flowsheet handle. Threading one through five call sites
    /// to serve a single parameter would make a property setter into something else.
    /// </remarks>
    public static void Apply(IFlowsheet fs, FlowDoc doc, Dictionary<string, ISimulationObject> byTag,
        Action<string, string?, string, string?> error)   // (code, tag, message, path)
    {
        foreach (var o in doc.Objects ?? [])
        {
            if (o.Kind != "unitOp" || o.Type != "waterElectrolyzer") continue;
            if (!byTag.TryGetValue(o.Tag, out var so) || so is not WaterElectrolyzer) continue;

            // `powerInput` is `required: true` in the catalog, so the structural validator has
            // already refused a document without it. This is the second door: a worker invoked
            // directly, or a catalog edited without the validator, must still not reach the engine's
            // null dereference — the engineer would read a stack trace instead of a sentence.
            if (o.Parameters is null ||
                !o.Parameters.Keys.Any(k => string.Equals(k, "powerInput", StringComparison.OrdinalIgnoreCase)))
            {
                error("MISSING_PARAMETER", o.Tag,
                    $"'{o.Tag}' is a waterElectrolyzer with no 'powerInput'. The engine needs the " +
                    "electrical power as a number in kW (or W/MW); without it it cannot calculate.",
                    "objects[].parameters.powerInput");
                continue;
            }

            var raw = o.Parameters.First(k =>
                string.Equals(k.Key, "powerInput", StringComparison.OrdinalIgnoreCase)).Value;
            double kw;
            try
            {
                kw = PowerKw(raw);
            }
            catch (Exception ex)
            {
                error("INVALID_PARAMETER_VALUE", o.Tag,
                    $"cannot read 'powerInput' on '{o.Tag}': {ex.Message}",
                    "objects[].parameters.powerInput");
                continue;
            }
            if (!double.IsFinite(kw) || kw <= 0)
            {
                error("INVALID_PARAMETER_VALUE", o.Tag,
                    $"'{o.Tag}' has powerInput {kw} kW. An electrolyzer needs positive electrical power.",
                    "objects[].parameters.powerInput");
                continue;
            }

            try
            {
                var tag = o.Tag + PowerSuffix;
                var es = fs.AddObject(ObjectType.EnergyStream, 50, 50, tag);
                if (es is null)
                {
                    error("ENGINE_ERROR", o.Tag, $"could not create the power stream for '{o.Tag}'", null);
                    continue;
                }
                // Input index 1 — connector 0 is the water feed. The engine leaves its GRAPHIC energy
                // connector inactive and reads this one instead, which is why there is no energy
                // nozzle in the catalog for callers to connect.
                fs.ConnectObjects(es.GraphicObject, so.GraphicObject, 0, 1);
                // kW is the unit convention the energy harvest already uses, so a synthesized stream
                // and an authored one cannot mean different things by the same number.
                SetEnergyFlow(es, kw);
            }
            catch (Exception ex)
            {
                error("ENGINE_ERROR", o.Tag,
                    $"could not attach power to '{o.Tag}': {ex.Message}", null);
            }
        }
    }

    /// <summary>Power in kW, through the same converter every other parameter uses.</summary>
    private static double PowerKw(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Number) return raw.GetDouble();   // bare number = SI = kW
        var value = raw.GetProperty("value").GetDouble();
        var unit = raw.TryGetProperty("unit", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(unit)) return value;
        // DWSIM's SI power is kW, and `ConvertToSI` honours that — the same call the generic
        // parameter path makes, so "1 MW" cannot mean two things depending on which path read it.
        return DWSIM.SharedClasses.SystemsOfUnits.Converter.ConvertToSI(unit, value);
    }

    private static void SetEnergyFlow(ISimulationObject es, double kw)
    {
        var prop = es.GetType().GetProperty("EnergyFlow");
        if (prop is not null && prop.CanWrite) prop.SetValue(es, kw);
        else es.SetPropertyValue("EnergyFlow", kw);
    }
}
