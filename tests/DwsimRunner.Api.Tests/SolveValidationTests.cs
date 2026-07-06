// T008 — US1 validation, error taxonomy, cache, and queue-cap behavior
// (FR-004, FR-013, FR-012). Written before the implementation; each test maps
// to a row of the error-taxonomy table in data-model.md.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class SolveValidationTests
{
    private static object ExitPayload(string templateId, int code) => new
    {
        templateId,
        overrides = new[] { new { @object = $"__exit:{code}", property = "x", value = 0.0 } },
    };

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task Bad_template_id_syntax_is_400_invalid_request(string templateId)
    {
        using var host = new RunnerHost();
        var resp = await host.Client.PostAsJsonAsync("/solve", new { templateId });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_REQUEST", body.GetProperty("error").GetString());
        Assert.Empty(host.StartMarkers()); // rejected before any worker spawn
    }

    [Fact]
    public async Task Unknown_template_is_404_with_taxonomy_body()
    {
        using var host = new RunnerHost();
        var resp = await host.Client.PostAsJsonAsync("/solve", new { templateId = "nope" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TEMPLATE_NOT_FOUND", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Worker_exit_2_maps_to_400_with_worker_error_passed_through()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");
        var resp = await host.Client.PostAsJsonAsync("/solve", ExitPayload("t", 2));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_OBJECT", body.GetProperty("error").GetString());
        Assert.Contains("Syngas", body.GetProperty("detail").GetString()); // lists available tags
    }

    [Fact]
    public async Task Worker_exit_3_maps_to_422_template_load_failed()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");
        var resp = await host.Client.PostAsJsonAsync("/solve", ExitPayload("t", 3));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TEMPLATE_LOAD_FAILED", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Worker_crash_maps_to_500_without_internals_in_body()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");
        var resp = await host.Client.PostAsJsonAsync("/solve", ExitPayload("t", 1));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal("WORKER_CRASH", body.GetProperty("error").GetString());
        Assert.DoesNotContain("   at ", text); // no stack frames leak to clients
    }

    [Fact]
    public async Task Identical_requests_are_served_from_cache()
    {
        using var host = new RunnerHost();
        host.AddTemplate("cached");
        var payload = new
        {
            templateId = "cached",
            overrides = new[] { new { @object = "Syngas", property = "massflow", value = 230.7, unit = "kg/h" } },
        };

        var body1 = await (await host.Client.PostAsJsonAsync("/solve", payload)).Content.ReadAsStringAsync();
        var body2 = await (await host.Client.PostAsJsonAsync("/solve", payload)).Content.ReadAsStringAsync();

        Assert.Equal(body1, body2);              // byte-identical (FR-008/FR-013)
        Assert.Single(host.StartMarkers());      // second request never spawned a worker
    }

    [Fact]
    public async Task Queue_overflow_returns_429_with_retry_after()
    {
        using var host = new RunnerHost(new() { ["MAX_CONCURRENT_SOLVES"] = "1" });
        host.AddTemplate("busy");

        // capacity = 1 running + 4 queued; fire 8 distinct slow requests at once
        var tasks = Enumerable.Range(0, 8).Select(i =>
            host.Client.PostAsJsonAsync("/solve", new
            {
                templateId = "busy",
                overrides = new[] { new { @object = "__sleep:2", property = "x", value = (double)i } },
            })).ToArray();
        var responses = await Task.WhenAll(tasks);

        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToArray();
        Assert.True(rejected.Length >= 3, $"expected >=3 rejections, got {rejected.Length}");
        Assert.All(rejected, r => Assert.True(r.Headers.Contains("Retry-After")));
        Assert.Equal(8 - rejected.Length, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
    }
}
