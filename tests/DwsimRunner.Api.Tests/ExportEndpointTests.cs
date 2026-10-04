// iskra spec 286 (ISK-493) — POST /flowsheets/export: build, solve, save with the engine's own
// SaveFlowsheet2 to a PRIVATE temp path, and return the .dwxmz bytes in the response.
//
// Why a route of its own: the build-solve + saveAsTemplate + GET /templates/{id}/file round trip
// needs a writable shared store (unwritable on the image's defaults) and lands its two requests on
// whichever replica the platform picks. This route keeps the file inside one request and deletes it
// on every path. A caller hanging up is handled by the shared spawn path (ISK-541) — not here.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class ExportEndpointTests
{
    private static string Doc(string unitTag = "V-1") => $$"""
    {
      "schemaVersion": 1,
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 0, "unit": "C" },
                    "pressure": { "value": 50, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar", "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
        { "tag": "{{unitTag}}", "kind": "unitOp", "type": "separator" },
        { "tag": "VAP", "kind": "materialStream" },
        { "tag": "LIQ", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "{{unitTag}}", "port": "Inlet" },
        { "from": "{{unitTag}}", "to": "VAP", "port": "Vapor Outlet" },
        { "from": "{{unitTag}}", "to": "LIQ", "port": "Liquid Outlet" }
      ]
    }
    """;

    private static object Body(string doc, int? timeoutSeconds = null) => timeoutSeconds is { } t
        ? new { document = JsonSerializer.Deserialize<JsonElement>(doc), timeoutSeconds = t }
        : new { document = JsonSerializer.Deserialize<JsonElement>(doc) };

    private static (RunnerHost host, string exportDir) Host(Dictionary<string, string?>? extra = null)
    {
        var dir = Directory.CreateTempSubdirectory("dwsim-export-tests-").FullName;
        var settings = new Dictionary<string, string?> { ["EXPORT_TEMP_PATH"] = dir };
        foreach (var kv in extra ?? []) settings[kv.Key] = kv.Value;
        return (new RunnerHost(settings), dir);
    }

    [Fact]
    public async Task A_solved_export_returns_the_file_bytes_and_its_metadata()
    {
        var (host, dir) = Host();
        using var _ = host;

        var resp = await host.Client.PostAsJsonAsync("/flowsheets/export", Body(Doc()));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/octet-stream", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("true", resp.Headers.GetValues("X-Export-Converged").Single());
        Assert.True(int.Parse(resp.Headers.GetValues("X-Export-Objects").Single()) >= 1);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal("fake dwxmz written by FakeWorker", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Empty(Directory.GetFiles(dir));   // the temp file is gone
    }

    [Fact]
    public async Task A_case_that_does_not_converge_is_still_a_file_and_says_so()
    {
        var (host, dir) = Host();
        using var _ = host;

        var resp = await host.Client.PostAsJsonAsync("/flowsheets/export", Body(Doc("__not-converged")));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("false", resp.Headers.GetValues("X-Export-Converged").Single());
        Assert.NotEmpty(await resp.Content.ReadAsByteArrayAsync());
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task A_save_that_wrote_nothing_is_a_500_SAVE_FAILED_never_a_200_without_bytes()
    {
        var (host, dir) = Host();
        using var _ = host;

        var resp = await host.Client.PostAsJsonAsync("/flowsheets/export", Body(Doc("__save-fail")));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal("SAVE_FAILED", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task An_engine_refusal_passes_through_as_422_and_leaves_no_file()
    {
        var (host, dir) = Host();
        using var _ = host;

        var resp = await host.Client.PostAsJsonAsync("/flowsheets/export", Body(Doc("__build-fail")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("BUILD_FAILED", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task A_timeout_is_a_distinct_504_SOLVE_TIMEOUT_and_leaves_no_file()
    {
        var (host, dir) = Host();
        using var _ = host;

        var resp = await host.Client.PostAsJsonAsync("/flowsheets/export", Body(Doc("__sleep:20"), timeoutSeconds: 5));

        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
        Assert.Equal("SOLVE_TIMEOUT", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task A_document_with_no_objects_is_a_400()
    {
        var (host, _) = Host();
        using var __ = host;

        var resp = await host.Client.PostAsJsonAsync("/flowsheets/export", new { document = new { schemaVersion = 1 } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_caller_hanging_up_leaves_no_worker_and_no_temp_file()
    {
        var (host, dir) = Host();
        using var _ = host;
        var before = Directory.GetFiles(host.TemplatesDir, "run-*.pid");
        using var caller = new CancellationTokenSource();

        var request = host.Client.PostAsJsonAsync("/flowsheets/export", Body(Doc("__sleep:30"), timeoutSeconds: 60), caller.Token);
        var pid = await NewWorkerPid(host, before);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.True(await GoneWithin(pid, TimeSpan.FromSeconds(10)), "the export's worker outlived its caller");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Directory.GetFiles(dir).Length > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task An_export_is_never_cached_and_never_listed_as_a_template()
    {
        var (host, _) = Host();
        using var __ = host;

        var first = Directory.GetFiles(host.TemplatesDir, "run-*.start").Length;
        await host.Client.PostAsJsonAsync("/flowsheets/export", Body(Doc()));
        await host.Client.PostAsJsonAsync("/flowsheets/export", Body(Doc()));
        Assert.Equal(first + 2, Directory.GetFiles(host.TemplatesDir, "run-*.start").Length);   // two real runs

        var templates = await host.Client.GetFromJsonAsync<JsonElement>("/templates");
        Assert.DoesNotContain("dwxmz", templates.ToString());
    }

    private static async Task<int> NewWorkerPid(RunnerHost host, string[] before)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var fresh = Directory.GetFiles(host.TemplatesDir, "run-*.pid").Except(before).FirstOrDefault();
            if (fresh is not null && int.TryParse(File.ReadAllText(fresh), out var pid)) return pid;
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("the request never reached the worker");
    }

    private static async Task<bool> GoneWithin(int pid, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            try { if (Process.GetProcessById(pid).HasExited) return true; }
            catch (ArgumentException) { return true; }
            await Task.Delay(25);
        }
        return false;
    }
}
