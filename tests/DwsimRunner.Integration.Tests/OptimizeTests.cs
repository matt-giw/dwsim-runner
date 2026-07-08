// T061 — US7 Tier B: minimize compressor power on the curated methanol
// circuit by varying feed (suction) pressure. Physics: at fixed discharge
// pressure, higher suction pressure → lower compression ratio → less power,
// so the optimum must sit at the upper bound and the best objective must be
// the minimum of the evaluation history.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Optimize")]
public class OptimizeTests
{
    [SkippableFact]
    public async Task Minimizing_compressor_power_drives_suction_pressure_to_the_upper_bound()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsJsonAsync("/optimize", new
        {
            templateId = "methanol_synthesis",
            variable = new { @object = "Syngas", property = "pressure", min = 10.0, max = 25.0, unit = "bar" },
            objective = new { @object = "W_comp", property = "duty", direction = "minimize" },
            tolerance = 1.0,
            maxEvaluations = 12,
            timeoutSeconds = 120,
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

        var best = body.GetProperty("best");
        var bestValue = best.GetProperty("value").GetDouble();
        var bestObjective = best.GetProperty("objectiveValue").GetDouble();

        var evals = body.GetProperty("evaluations").EnumerateArray().ToList();
        Assert.InRange(evals.Count, 2, 12);

        // Best must actually be the best point evaluated.
        var feasible = evals.Where(e => e.GetProperty("converged").GetBoolean())
                            .Select(e => e.GetProperty("objectiveValue").GetDouble()).ToList();
        Assert.NotEmpty(feasible);
        Assert.Equal(feasible.Min(), bestObjective, 6);

        // Monotonic objective → the search must end up near the upper bound.
        Assert.True(bestValue > 20.0,
            $"minimum power should sit near the 25 bar bound, got {bestValue:F2} bar (P={bestObjective:F2} kW)");

        // And the study result carries the full solve at the optimum.
        Assert.True(best.GetProperty("result").GetProperty("converged").GetBoolean());
    }

    [SkippableFact]
    public async Task Impossible_bounds_are_infeasible()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // Negative absolute pressures can never converge.
        var resp = await RunnerConnection.Client.PostAsJsonAsync("/optimize", new
        {
            templateId = "methanol_synthesis",
            variable = new { @object = "Syngas", property = "pressure", min = -20.0, max = -5.0, unit = "bar" },
            objective = new { @object = "W_comp", property = "duty", direction = "minimize" },
            maxEvaluations = 4,
            timeoutSeconds = 60,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal("OPTIMIZATION_INFEASIBLE", body.GetProperty("error").GetString());
    }
}
