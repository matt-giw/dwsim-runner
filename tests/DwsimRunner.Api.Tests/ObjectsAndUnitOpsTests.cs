// T021 — US5 Tier A: template object inventory endpoint (FR-014) and unitOps
// passthrough in solve results (FR-015), via the FakeWorker's canned payloads.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class ObjectsAndUnitOpsTests
{
    [Fact]
    public async Task Objects_endpoint_returns_worker_inventory()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.GetAsync("/templates/t/objects");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var objects = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("objects");
        var syngas = objects.EnumerateArray().First(o => o.GetProperty("tag").GetString() == "Syngas");
        Assert.Equal("materialStream", syngas.GetProperty("type").GetString());
        Assert.Contains("massflow",
            syngas.GetProperty("settableProperties").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Objects_endpoint_unknown_template_is_404_taxonomy()
    {
        using var host = new RunnerHost();
        var resp = await host.Client.GetAsync("/templates/nope/objects");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TEMPLATE_NOT_FOUND", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Objects_endpoint_rejects_bad_template_id_syntax()
    {
        using var host = new RunnerHost();
        // %2F = encoded '/' — must not resolve outside the templates dir
        var resp = await host.Client.GetAsync("/templates/..%2Fescape/objects");
        Assert.True(resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"got {(int)resp.StatusCode}");
        Assert.Single(host.StartMarkers()); // only the pre-warmed catalog worker
    }

    [Fact]
    public async Task Inventory_is_cached_by_template_mtime()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var b1 = await host.Client.GetStringAsync("/templates/t/objects");
        var b2 = await host.Client.GetStringAsync("/templates/t/objects");

        Assert.Equal(b1, b2);
        Assert.Equal(2, host.StartMarkers().Length); // pre-warmed catalog + first inventory (second from cache)
    }

    [Fact]
    public async Task Solve_result_includes_unit_op_rows()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/solve", new { templateId = "t" });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var comp = body.GetProperty("unitOps").EnumerateArray()
                       .First(u => u.GetProperty("name").GetString() == "Comp-1");
        Assert.Equal("compressor", comp.GetProperty("type").GetString());
        Assert.Equal(11.5, comp.GetProperty("powerKw").GetDouble());
    }
}
