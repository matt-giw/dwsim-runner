// T018 — POST /flowsheets/build-solve (FR-BUILD-001..005). Builds a flowsheet
// from a document, solves, and returns the BuildReport (SolveResult + build +
// optional template). Non-convergence is 200 with converged:false; build-stage
// failures use the extended error taxonomy. Reserved tags in the FakeWorker
// provoke each failure class.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class BuildSolveEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string ValidDoc = """
    {
      "schemaVersion": 1,
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 0, "unit": "C" },
                    "pressure": { "value": 50, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
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

    private static StringContent DocBody(string doc, int timeoutSeconds = 120, object? saveAs = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["document"] = JsonSerializer.Deserialize<JsonElement>(doc),
            ["timeoutSeconds"] = timeoutSeconds,
        };
        if (saveAs is not null) payload["saveAsTemplate"] = saveAs;
        return new(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Valid_document_builds_and_solves_returns_200_build_report()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", DocBody(ValidDoc));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("converged").GetBoolean());
        Assert.NotNull(body.GetProperty("build"));
        Assert.True(body.GetProperty("build").GetProperty("objectsCreated").GetInt32() >= 1);
        Assert.True(body.GetProperty("build").GetProperty("connectionsMade").GetInt32() >= 1);
        Assert.NotEmpty(body.GetProperty("streams").EnumerateArray());
    }

    [Fact]
    public async Task Non_convergence_is_200_with_converged_false()
    {
        var notConvergedDoc = ValidDoc
            .Replace("\"tag\": \"V-1\"", "\"tag\": \"__not-converged\"")
            .Replace("\"to\": \"V-1\"", "\"to\": \"__not-converged\"")
            .Replace("\"from\": \"V-1\"", "\"from\": \"__not-converged\"");
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", DocBody(notConvergedDoc));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(body.GetProperty("converged").GetBoolean());
    }

    [Fact]
    public async Task Structural_failure_returns_400_document_invalid_with_issues()
    {
        var structuralFail = ValidDoc.Replace("\"type\": \"separator\"", "\"type\": \"warpDrive\"");
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", DocBody(structuralFail));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("DOCUMENT_INVALID", body.GetProperty("error").GetString());
        var issues = body.GetProperty("issues").EnumerateArray().ToList();
        Assert.Contains(issues, i => i.GetProperty("code").GetString() == "UNKNOWN_UNIT_OP_TYPE");
    }

    [Fact]
    public async Task Build_failure_returns_422_with_named_tag()
    {
        var buildFailDoc = ValidDoc
            .Replace("\"tag\": \"V-1\"", "\"tag\": \"__build-fail\"")
            .Replace("\"to\": \"V-1\"", "\"to\": \"__build-fail\"")
            .Replace("\"from\": \"V-1\"", "\"from\": \"__build-fail\"");
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", DocBody(buildFailDoc));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("BUILD_FAILED", body.GetProperty("error").GetString());
        var issues = body.GetProperty("issues").EnumerateArray().ToList();
        Assert.Contains(issues, i => i.GetProperty("tag").GetString() == "__build-fail");
    }

    [Fact]
    public async Task Unknown_compound_returns_422_with_tag()
    {
        var unknownCompoundDoc = ValidDoc
            .Replace("\"tag\": \"V-1\"", "\"tag\": \"__unknown-compound\"")
            .Replace("\"to\": \"V-1\"", "\"to\": \"__unknown-compound\"")
            .Replace("\"from\": \"V-1\"", "\"from\": \"__unknown-compound\"");
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", DocBody(unknownCompoundDoc));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("UNKNOWN_COMPOUND", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Timeout_returns_504()
    {
        // __sleep:5 in the document blocks the fake worker past the timeout.
        var sleepDoc = ValidDoc
            .Replace("\"tag\": \"V-1\"", "\"tag\": \"__sleep:5\"")
            .Replace("\"to\": \"V-1\"", "\"to\": \"__sleep:5\"")
            .Replace("\"from\": \"V-1\"", "\"from\": \"__sleep:5\"");
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", DocBody(sleepDoc, timeoutSeconds: 1));

        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("SOLVE_TIMEOUT", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Identical_documents_served_from_cache_single_spawn()
    {
        using var host = new RunnerHost();

        var first = await host.Client.PostAsync("/flowsheets/build-solve", DocBody(ValidDoc));
        var second = await host.Client.PostAsync("/flowsheets/build-solve", DocBody(ValidDoc));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, host.StartMarkers().Length);   // pre-warmed catalog + first build-solve (second from cache)
    }

    [Fact]
    public async Task Saturated_queue_returns_429()
    {
        using var host = new RunnerHost(new() { ["MAX_CONCURRENT_SOLVES"] = "1" });

        var slowDoc = ValidDoc
            .Replace("\"tag\": \"V-1\"", "\"tag\": \"__sleep:8\"")
            .Replace("\"to\": \"V-1\"", "\"to\": \"__sleep:8\"")
            .Replace("\"from\": \"V-1\"", "\"from\": \"__sleep:8\"");
        var slow = host.Client.PostAsync("/flowsheets/build-solve", DocBody(slowDoc));

        // Fill the queue (maxAdmitted = maxConcurrent * 5 = 5) so the next request gets 429.
        await Task.Delay(2000);
        for (var i = 0; i < 5; i++)
            _ = host.Client.PostAsync("/flowsheets/build-solve", DocBody(ValidDoc));
        var fastTask = host.Client.PostAsync("/flowsheets/build-solve", DocBody(ValidDoc));

        var fastResp = await fastTask;
        Assert.Equal(HttpStatusCode.TooManyRequests, fastResp.StatusCode);
        var body = await fastResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("QUEUE_FULL", body.GetProperty("error").GetString());

        await slow;
    }

    [Fact]
    public async Task Missing_document_is_400_invalid_request()
    {
        using var host = new RunnerHost();
        var resp = await host.Client.PostAsJsonAsync("/flowsheets/build-solve", new { timeoutSeconds = 60 });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("error").GetString());
    }
}
