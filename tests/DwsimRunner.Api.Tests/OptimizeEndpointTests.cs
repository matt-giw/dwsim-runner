// T058 — US7 POST /optimize: golden-section over one variable through the
// normal solve pipeline. The FakeWorker scripts the objective — a parabola
// (v-42)² + 7 on the W_comp duty, evaluated at the "__objective" override —
// so the optimizer must land on 42 (minimize) within tolerance.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class OptimizeEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Request(
        string variableObject = "__objective",
        double min = 0, double max = 100,
        double? tolerance = 0.5,
        int? maxEvaluations = 30,
        string direction = "minimize") => new
        {
            templateId = "t",
            variable = new { @object = variableObject, property = "pressure", min, max, unit = "bar" },
            objective = new { @object = "W_comp", property = "duty", direction },
            tolerance,
            maxEvaluations,
        };

    [Fact]
    public async Task Minimize_converges_to_the_scripted_optimum_within_tolerance()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/optimize", Request());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.True(body.GetProperty("converged").GetBoolean());
        Assert.Equal("tolerance", body.GetProperty("stoppedReason").GetString());

        var best = body.GetProperty("best");
        Assert.InRange(best.GetProperty("value").GetDouble(), 41.0, 43.0);
        Assert.True(best.GetProperty("result").GetProperty("converged").GetBoolean());

        // Evaluation history: every entry pairs input with objective + converged.
        var evals = body.GetProperty("evaluations").EnumerateArray().ToList();
        Assert.InRange(evals.Count, 2, 30);
        Assert.All(evals, e =>
        {
            Assert.True(e.TryGetProperty("value", out _));
            Assert.True(e.TryGetProperty("objectiveValue", out _));
            Assert.True(e.TryGetProperty("converged", out _));
        });
        // The best objective must be near the parabola's floor of 7.
        var bestObjective = evals.Min(e => e.GetProperty("objectiveValue").GetDouble());
        Assert.InRange(bestObjective, 7.0, 8.0);
    }

    [Fact]
    public async Task Maximize_direction_flips_the_search_to_a_boundary()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        // The parabola grows away from 42 — maximizing drives toward min/max.
        var resp = await host.Client.PostAsJsonAsync("/optimize",
            Request(direction: "maximize", tolerance: 1.0));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var bestValue = body.GetProperty("best").GetProperty("value").GetDouble();
        Assert.True(Math.Abs(bestValue - 42.0) > 20.0,
            $"maximize should move far from the minimum at 42, got {bestValue}");
    }

    [Fact]
    public async Task Exhausted_budget_stops_with_reason_budget_and_partial_history()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/optimize",
            Request(tolerance: 1e-9, maxEvaluations: 5));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(body.GetProperty("converged").GetBoolean());
        Assert.Equal("budget", body.GetProperty("stoppedReason").GetString());
        Assert.Equal(5, body.GetProperty("evaluations").GetArrayLength());
        Assert.True(body.TryGetProperty("best", out var best)
                    && best.ValueKind == JsonValueKind.Object);   // best-so-far still reported
    }

    [Fact]
    public async Task No_feasible_point_is_422_optimization_infeasible()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        // "__no-converge" makes every FakeWorker solve return converged:false.
        var resp = await host.Client.PostAsJsonAsync("/optimize",
            Request(variableObject: "__no-converge", maxEvaluations: 6));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("OPTIMIZATION_INFEASIBLE", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(50.0, 10.0)]    // min >= max
    [InlineData(10.0, 10.0)]
    public async Task Inverted_variable_bounds_are_400(double min, double max)
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/optimize", Request(min: min, max: max));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Evaluation_budget_over_30_is_400()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/optimize", Request(maxEvaluations: 31));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Bad_direction_is_400()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/optimize", Request(direction: "sideways"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_template_is_404()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/optimize", Request());

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Health_advertises_the_optimize_caps()
    {
        using var host = new RunnerHost();

        var body = await host.Client.GetFromJsonAsync<JsonElement>("/health", Json);

        Assert.Equal(30, body.GetProperty("maxEvaluations").GetInt32());
        Assert.Equal(600, body.GetProperty("maxTimeoutSeconds").GetInt32());
    }
}
