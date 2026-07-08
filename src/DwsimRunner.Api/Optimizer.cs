// dwsim-runner API — GPL-3.0
// Golden-section search over one variable (US7, FR-OPT). Pure orchestration:
// the objective is an async callback that runs a solve through the normal
// pipeline; unconverged points count as infinitely bad so the search walks
// around infeasible pockets without failing the study.

namespace DwsimRunner.Api;

public sealed record OptEvaluation(double Value, double? ObjectiveValue, bool Converged, string Body);

public sealed record OptOutcome(
    OptEvaluation? Best,
    List<OptEvaluation> Evaluations,
    bool Converged,
    string StoppedReason);   // "tolerance" | "budget" | "infeasible"

public static class Optimizer
{
    private static readonly double InvPhi = (Math.Sqrt(5) - 1) / 2;   // 0.618…

    /// <summary>
    /// Minimize (or maximize) over [min, max] with ≤ maxEvaluations calls of
    /// <paramref name="evaluate"/>. Terminates when the bracket shrinks below
    /// <paramref name="tolerance"/> ("tolerance") or the budget runs out
    /// ("budget"); "infeasible" when no evaluation converged.
    /// </summary>
    public static async Task<OptOutcome> GoldenSectionAsync(
        double min, double max, double tolerance, int maxEvaluations, bool maximize,
        Func<double, Task<OptEvaluation>> evaluate)
    {
        var history = new List<OptEvaluation>();

        async Task<(OptEvaluation Eval, double Score)> Probe(double x)
        {
            var e = await evaluate(x);
            history.Add(e);
            // Unconverged/valueless points are "worst possible", never NaN.
            var score = e.Converged && e.ObjectiveValue is double ov && double.IsFinite(ov)
                ? (maximize ? -ov : ov)
                : double.PositiveInfinity;
            return (e, score);
        }

        double a = min, b = max;
        double x1 = b - InvPhi * (b - a);
        double x2 = a + InvPhi * (b - a);

        var p1 = await Probe(x1);
        var stopped = "budget";
        if (history.Count < maxEvaluations)
        {
            var p2 = await Probe(x2);
            while (true)
            {
                if (b - a <= tolerance) { stopped = "tolerance"; break; }
                if (history.Count >= maxEvaluations) break;   // "budget"

                if (p1.Score <= p2.Score)
                {
                    b = x2; x2 = x1; p2 = p1;
                    x1 = b - InvPhi * (b - a);
                    p1 = await Probe(x1);
                }
                else
                {
                    a = x1; x1 = x2; p1 = p2;
                    x2 = a + InvPhi * (b - a);
                    p2 = await Probe(x2);
                }
            }
        }

        var feasible = history.Where(e => e.Converged && e.ObjectiveValue is double v && double.IsFinite(v)).ToList();
        if (feasible.Count == 0)
            return new(null, history, false, "infeasible");

        var best = maximize
            ? feasible.MaxBy(e => e.ObjectiveValue!.Value)!
            : feasible.MinBy(e => e.ObjectiveValue!.Value)!;
        return new(best, history, stopped == "tolerance", stopped);
    }

    /// <summary>
    /// Pull the objective value for (objectName, property) out of a
    /// SolveResult JSON body. Property accepts override-style aliases
    /// (duty, power, temperature, pressure, massflow, molarflow) or the
    /// literal wire field name. Null when not found / non-numeric.
    /// </summary>
    public static double? ExtractObjective(string solveResultBody, string objectName, string property)
    {
        System.Text.Json.JsonElement root;
        try
        {
            root = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(solveResultBody);
        }
        catch (System.Text.Json.JsonException) { return null; }

        var field = property.ToLowerInvariant() switch
        {
            "duty" or "dutykw" => "dutyKw",
            "power" or "powerkw" => "powerKw",
            "temperature" => "temperatureC",
            "pressure" => "pressureBar",
            "massflow" => "massFlowKgH",
            "molarflow" => "molarFlowKmolH",
            _ => property,
        };

        foreach (var section in new[] { "streams", "energy", "unitOps" })
        {
            if (!root.TryGetProperty(section, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;
            foreach (var row in arr.EnumerateArray())
            {
                if (!row.TryGetProperty("name", out var nameEl)
                    || !string.Equals(nameEl.GetString(), objectName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (row.TryGetProperty(field, out var valEl)
                    && valEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    return valEl.GetDouble();
            }
        }
        return null;
    }
}
