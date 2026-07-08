// T052 — US6 PFD endpoints: GET /templates/{id}/pfd.png and
// POST /flowsheets/pfd return binary image/png decoded from the worker's
// {pngBase64} payload; render failures surface as 422 RENDER_FAILED JSON.
// Served by the FakeWorker's pfd mode (1×1 PNG; tag "__render-fail" → exit 5).

using System.Net;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class PfdEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];

    private const string Doc = """
    {
      "schemaVersion": 1,
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream" },
        { "tag": "V-1", "kind": "unitOp", "type": "separator" },
        { "tag": "VAP", "kind": "materialStream" },
        { "tag": "LIQ", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "V-1", "port": "Inlet" },
        { "from": "V-1", "to": "VAP", "port": "Vapor Outlet" },
        { "from": "V-1", "to": "LIQ", "port": "Liquid Outlet" }
      ]
    }
    """;

    private static StringContent DocBody(string doc) =>
        new(JsonSerializer.Serialize(new { document = JsonSerializer.Deserialize<JsonElement>(doc) }),
            System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Post_document_pfd_returns_binary_png()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/pfd", DocBody(Doc));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 4);
        Assert.Equal(PngMagic, bytes[..4]);
    }

    [Fact]
    public async Task Get_template_pfd_returns_binary_png()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.GetAsync("/templates/t/pfd.png");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(PngMagic, bytes[..4]);
    }

    [Fact]
    public async Task Get_template_pfd_unknown_template_is_404()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.GetAsync("/templates/nope/pfd.png");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(), Json);
        Assert.Equal("TEMPLATE_NOT_FOUND", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Render_failure_is_422_render_failed_json()
    {
        using var host = new RunnerHost();

        // "__render-fail" makes the FakeWorker exit 5.
        var failDoc = Doc.Replace("\"tag\": \"V-1\"", "\"tag\": \"__render-fail\"")
                         .Replace("\"to\": \"V-1\"", "\"to\": \"__render-fail\"")
                         .Replace("\"from\": \"V-1\"", "\"from\": \"__render-fail\"");
        var resp = await host.Client.PostAsync("/flowsheets/pfd", DocBody(failDoc));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(), Json);
        Assert.Equal("RENDER_FAILED", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Missing_document_is_400_invalid_request()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/pfd",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(), Json);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Template_pfd_is_cached_by_template_file()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var first = await host.Client.GetAsync("/templates/t/pfd.png");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var spawns = host.StartMarkers().Length;

        var second = await host.Client.GetAsync("/templates/t/pfd.png");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(spawns, host.StartMarkers().Length);   // cache hit, no new worker
    }
}
