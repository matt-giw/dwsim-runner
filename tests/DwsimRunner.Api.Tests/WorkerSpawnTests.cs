// ISK-442 — every route that runs the worker goes through ONE spawn and ONE exit-code
// classification. There used to be three spawn copies (the template path, the document path, the
// catalog fetch) and they had drifted: exit 3 was mapped only on the template path, exit 4 only on
// the document path, and the unsupported-DWSIM-version warning was appended only to /solve. Each
// test below pins a behaviour one path used to lack.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class WorkerSpawnTests
{
    private const string ValidDoc = """
    {
      "schemaVersion": 1,
      "name": "spawn parity",
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 25, "unit": "C" },
                    "pressure": { "value": 50, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar", "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
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

    private static JsonElement Doc() => JsonSerializer.Deserialize<JsonElement>(ValidDoc);

    private static object Exit(int code) => new { @object = $"__exit:{code}", property = "x", value = 0.0 };

    private static string MakeFixtureDwsimDir()
    {
        // Any readable PE with a 1.0.x assembly version reads as "DWSIM found, unsupported".
        var dir = Directory.CreateTempSubdirectory("dwsim-fixture-").FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "FakeWorker.dll"),
                  Path.Combine(dir, "DWSIM.Automation.dll"));
        return dir;
    }

    // The document path used to map exit 3 to a 500 WORKER_CRASH: only the template path knew it.
    [Fact]
    public async Task Document_case_exit_3_is_template_load_failed_not_a_crash()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            document = Doc(),
            cases = new Dictionary<string, object> { ["bad"] = new[] { Exit(3) } },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var bad = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").GetProperty("bad");
        Assert.Equal("TEMPLATE_LOAD_FAILED", bad.GetProperty("error").GetString());
    }

    // The template path used to map exit 4 to a 500 WORKER_CRASH: only the document path knew it.
    [Fact]
    public async Task Template_solve_exit_4_is_422_not_a_crash()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/solve", new { templateId = "t", overrides = new[] { Exit(4) } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // The FakeWorker's exit-4 document is a WORKER_CRASH envelope; what matters is the status:
        // the taxonomy row for exit 4 is a 422 on every route now, and the worker's own error
        // document is passed through.
        Assert.True(body.TryGetProperty("error", out _));
    }

    // Only /solve used to append the out-of-range engine warning; a build-solve against the same
    // engine said nothing.
    [Fact]
    public async Task Build_solve_appends_warning_when_engine_version_unsupported()
    {
        using var host = new RunnerHost(new() { ["DWSIM_PATH"] = MakeFixtureDwsimDir() });

        var resp = await host.Client.PostAsJsonAsync("/flowsheets/build-solve", new { document = Doc() });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.Contains("outside supported range"));
    }

    // The document-case cache key joined overrides in REQUEST order, so the same case listed two
    // ways was two cache entries and two solves. It is one now, exactly as on the template path.
    [Fact]
    public async Task Document_case_cache_ignores_override_order()
    {
        using var host = new RunnerHost();
        var t = new { @object = "FEED", property = "temperature", value = 30.0 };
        var p = new { @object = "FEED", property = "pressure", value = 40.0 };

        async Task Compare(object[] overrides)
        {
            var resp = await host.Client.PostAsJsonAsync("/compare", new
            {
                document = Doc(),
                cases = new Dictionary<string, object> { ["a"] = overrides },
            });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        await Compare([t, p]);
        var afterFirst = host.StartMarkers().Length;
        await Compare([p, t]);

        Assert.Equal(afterFirst, host.StartMarkers().Length);
    }
}
