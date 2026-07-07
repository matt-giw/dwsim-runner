// dwsim-runner Worker — GPL-3.0
// DistillationColumn parameter binding (T032/T023): the rigorous column has
// configuration that doesn't map cleanly to single-property reflection. The
// builder dispatches feedStage / refluxRatio / distillateMolarFlow here so
// the catalog's advertised parameters land on the right subobjects.
//
// DWSIM's DistillationColumn exposes a SetupData/Solve/etc. surface; the
// fields we touch here are stable across the 9.x line and probed defensively
// (missing members are an explicit BUILD_FAILED, never a silent skip).

using System.Reflection;
using System.Text.Json;
using DWSIM.Interfaces;

namespace DwsimRunner.Worker;

internal static class ColumnConfigurator
{
    public static void Apply(ISimulationObject column, string paramName, JsonElement raw)
    {
        var t = column.GetType();

        switch (paramName)
        {
            case "feedStage":
                SetFeedStage(t, column, AsInt(raw));
                return;
            case "refluxRatio":
                SetRefluxRatio(t, column, AsDouble(raw));
                return;
            case "distillateMolarFlow":
                SetDistillateFlow(t, column, AsDouble(raw));
                return;
            default:
                throw new InvalidOperationException($"ColumnConfigurator has no handler for '{paramName}'");
        }
    }

    private static int AsInt(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.GetProperty("value").GetInt32() : e.GetInt32();
    private static double AsDouble(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.GetProperty("value").GetDouble() : e.GetDouble();

    // The feed stage is the index of the stage that receives the feed; DWSIM
    // stores feed stages under the column's InputDeptCollection or via
    // SetFeedStage on the DistillationColumn class. We try a small set of
    // known member shapes and fail loudly if none match.
    private static void SetFeedStage(Type t, ISimulationObject column, int stage)
    {
        // Try SetFeedStage(int) method first.
        var m = t.GetMethod("SetFeedStage", BindingFlags.Public | BindingFlags.Instance, null, [typeof(int)], null);
        if (m is not null) { m.Invoke(column, [stage]); return; }

        // Fall back to a writable FeedStage property.
        if (TrySetProperty(t, column, "FeedStage", stage)) return;

        // Last resort: the column's GraphicObject exposes an edit collection;
        // without engine-specific support we surface a named BUILD_FAILED.
        throw new InvalidOperationException(
            $"DistillationColumn '{column.GraphicObject.Tag}' exposes neither SetFeedStage(int) nor a FeedStage property — column feed-stage binding needs engine support");
    }

    private static void SetRefluxRatio(Type t, ISimulationObject column, double rr)
    {
        if (TrySetProperty(t, column, "RefluxRatio", rr)) return;
        if (TrySetProperty(t, column, "m_refluxratio", rr)) return;
        // CondenserSpec enum + R = value pattern: many DWSIM columns use a
        // `CondenserSpec` enum and a paired numeric field. We set the numeric
        // field; the builder chooses RR by advertising it in the catalog.
        if (TrySetProperty(t, column, "RR", rr)) return;
        throw new InvalidOperationException(
            $"DistillationColumn '{column.GraphicObject.Tag}' has no RefluxRatio/m_refluxratio/RR property");
    }

    private static void SetDistillateFlow(Type t, ISimulationObject column, double flow)
    {
        if (TrySetProperty(t, column, "DistillateFlow", flow)) return;
        if (TrySetProperty(t, column, "m_distillate_flow", flow)) return;
        if (TrySetProperty(t, column, "DistillateMolarFlow", flow)) return;
        throw new InvalidOperationException(
            $"DistillationColumn '{column.GraphicObject.Tag}' has no DistillateFlow/m_distillate_flow/DistillateMolarFlow property");
    }

    private static bool TrySetProperty(Type t, ISimulationObject obj, string name, double value)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (p is null || !p.CanWrite) return false;
        var target = p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, value.ToString("R"), true)
            : p.PropertyType == typeof(int) ? (object)(int)value
            : p.PropertyType == typeof(double) ? value
            : p.PropertyType == typeof(double?) ? (double?)value
            : Convert.ChangeType(value, p.PropertyType);
        p.SetValue(obj, target);
        return true;
    }
}