// T006 — characterization tests: pin the API behavior that already exists
// (health shape, template listing, solve passthrough, hard timeout kill,
// concurrency gate) before any new feature work lands.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class CoreApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Health_reports_paths_and_concurrency()
    {
        using var host = new RunnerHost();
        var health = await host.Client.GetFromJsonAsync<JsonElement>("/health");

        // ok tracks readiness (FR-007): no DWSIM in the test dir → not ready
        Assert.False(health.GetProperty("ok").GetBoolean());
        Assert.False(health.GetProperty("dwsimFound").GetBoolean());
        Assert.Equal(host.TemplatesDir, health.GetProperty("templatesPath").GetString());
        Assert.Equal(4, health.GetProperty("maxConcurrent").GetInt32());
    }

    [Fact]
    public async Task Templates_lists_dwxmz_ids()
    {
        using var host = new RunnerHost();
        host.AddTemplate("methanol_synthesis");
        host.AddTemplate("pem-ref-plant");

        var ids = await host.Client.GetFromJsonAsync<string[]>("/templates");

        Assert.Equal(new[] { "methanol_synthesis", "pem-ref-plant" }, ids!.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Solve_returns_worker_result_as_json()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t1");

        var resp = await host.Client.PostAsJsonAsync("/solve", new { templateId = "t1" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("converged").GetBoolean());
        Assert.Equal("Syngas", body.GetProperty("streams")[0].GetProperty("name").GetString());
        Assert.Equal(11.5, body.GetProperty("energy")[0].GetProperty("dutyKw").GetDouble());
        Assert.Equal(2, host.StartMarkers().Length); // pre-warmed catalog + solve worker
    }

    [Fact]
    public async Task Solve_unknown_template_is_404()
    {
        using var host = new RunnerHost();
        var resp = await host.Client.PostAsJsonAsync("/solve", new { templateId = "nope" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Solve_timeout_kills_worker_and_returns_504()
    {
        using var host = new RunnerHost();
        host.AddTemplate("slow");

        var sw = Stopwatch.StartNew();
        var resp = await host.Client.PostAsJsonAsync("/solve", new
        {
            templateId = "slow",
            overrides = new[] { new { @object = "__sleep:20", property = "x", value = 0.0 } },
            timeoutSeconds = 1,
        });
        sw.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");

        await Task.Delay(1500); // give a survivor time to finish — it must not
        Assert.Equal(2, host.StartMarkers().Length); // pre-warmed catalog + solve worker
        Assert.Single(host.EndMarkers()); // killed hard: no end marker for killed worker
    }

    [Fact]
    public async Task Solves_serialize_when_gate_is_one()
    {
        using var host = new RunnerHost(new() { ["MAX_CONCURRENT_SOLVES"] = "1" });
        host.AddTemplate("gated");

        var payload = new
        {
            templateId = "gated",
            overrides = new[] { new { @object = "__sleep:1", property = "x", value = 0.0 } },
        };
        var (r1, r2) = (host.Client.PostAsJsonAsync("/solve", payload),
                        host.Client.PostAsJsonAsync("/solve", payload));
        Assert.Equal(HttpStatusCode.OK, (await r1).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await r2).StatusCode);

        var runs = host.RunIntervals();
        Assert.Equal(3, runs.Count); // pre-warmed catalog + 2 solve workers
        var (first, second) = runs[0].Start <= runs[1].Start ? (runs[0], runs[1]) : (runs[1], runs[0]);
        Assert.True(first.End <= second.Start,
            $"runs overlapped: [{first.Start}-{first.End}] vs [{second.Start}-{second.End}]");
    }
}
