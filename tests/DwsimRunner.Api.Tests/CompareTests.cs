// T016 — US2 /compare: labeled per-case results, per-case error isolation,
// case-count limits, concurrent fan-out, cache shared with /solve (FR-003,
// FR-008, FR-013).

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class CompareTests
{
    private static readonly object Ok = Array.Empty<object>();

    [Fact]
    public async Task Returns_labeled_results_per_case()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            templateId = "t",
            cases = new Dictionary<string, object> { ["base"] = Ok, ["case b"] = Ok },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.True(results.GetProperty("base").GetProperty("converged").GetBoolean());
        Assert.True(results.GetProperty("case b").GetProperty("converged").GetBoolean());
    }

    [Fact]
    public async Task Invalid_case_yields_case_error_without_failing_the_set()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            templateId = "t",
            cases = new Dictionary<string, object>
            {
                ["good"] = Ok,
                ["bad"] = new[] { new { @object = "__exit:2", property = "x", value = 0.0 } },
            },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.True(results.GetProperty("good").GetProperty("converged").GetBoolean());
        Assert.Equal("INVALID_OBJECT", results.GetProperty("bad").GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task Case_count_outside_1_to_10_is_400(int count)
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var cases = Enumerable.Range(0, count).ToDictionary(i => $"case{i}", _ => (object)Ok);
        var resp = await host.Client.PostAsJsonAsync("/compare", new { templateId = "t", cases });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_REQUEST", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cases_fan_out_concurrently_through_the_worker_pool()
    {
        using var host = new RunnerHost(new() { ["MAX_CONCURRENT_SOLVES"] = "2" });
        host.AddTemplate("t");

        object Sleepy(double marker) =>
            new[] { new { @object = "__sleep:2", property = "x", value = marker } };

        var sw = Stopwatch.StartNew();
        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            templateId = "t",
            cases = new Dictionary<string, object> { ["a"] = Sleepy(1), ["b"] = Sleepy(2) },
        });
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5),
            $"cases appear to have run sequentially: {sw.Elapsed}"); // sequential ≈ 4s+
    }

    [Fact]
    public async Task Compare_case_shares_the_solve_cache()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");
        var overrides = new[] { new { @object = "Syngas", property = "massflow", value = 173.0, unit = "kg/h" } };

        var solveBody = await (await host.Client.PostAsJsonAsync("/solve",
            new { templateId = "t", overrides })).Content.ReadAsStringAsync();

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            templateId = "t",
            cases = new Dictionary<string, object> { ["same"] = overrides },
        });

        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.Equal(solveBody, results.GetProperty("same").GetRawText());
        Assert.Equal(2, host.StartMarkers().Length); // pre-warmed catalog + solve (compare served from cache)
    }
}
