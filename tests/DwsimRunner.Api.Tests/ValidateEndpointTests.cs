// T017 — POST /flowsheets/validate (FR-VAL-001/002). Served by the FakeWorker's
// validate mode (T021 cans already drive the semantic path). The structural
// fast path runs in-process against DocumentValidator + the cached catalog;
// semantic validation sends the document to the worker and surfaces its issues.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class ValidateEndpointTests
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

    private static StringContent DocBody(string doc, bool semantic = true) =>
        new(JsonSerializer.Serialize(new { document = JsonSerializer.Deserialize<JsonElement>(doc), semantic }), System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Structural_only_valid_document_returns_200_with_no_issues()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/validate", DocBody(ValidDoc, semantic: false));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("valid").GetBoolean());
        Assert.Empty(body.GetProperty("issues").EnumerateArray());
        Assert.Single(host.StartMarkers());   // only the pre-warmed catalog worker
    }

    [Fact]
    public async Task Structural_short_circuits_semantic_when_document_has_structural_errors()
    {
        // duplicate tags → structural error; semantic must NOT run.
        var dupTagDoc = ValidDoc.Replace("\"tag\": \"VAP\"", "\"tag\": \"FEED\"");

        using var host = new RunnerHost();
        var resp = await host.Client.PostAsync("/flowsheets/validate", DocBody(dupTagDoc, semantic: true));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(body.GetProperty("valid").GetBoolean());
        var issues = body.GetProperty("issues").EnumerateArray().ToList();
        Assert.Contains(issues, i => i.GetProperty("code").GetString() == "DUPLICATE_TAG");
        Assert.Single(host.StartMarkers());   // only the pre-warmed catalog worker
    }

    [Fact]
    public async Task Semantic_valid_path_runs_the_worker_and_returns_valid_true()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/validate", DocBody(ValidDoc, semantic: true));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("valid").GetBoolean());
        Assert.Empty(body.GetProperty("issues").EnumerateArray());
        Assert.Equal(2, host.StartMarkers().Length);   // pre-warmed catalog + semantic validation
    }

    [Fact]
    public async Task Semantic_surfaces_engine_issues_with_their_tags_and_codes()
    {
        // The FakeWorker emits UNKNOWN_COMPOUND on any object tagged '__semantic-issue'.
        var semanticIssueDoc = ValidDoc
            .Replace("\"tag\": \"V-1\"", "\"tag\": \"__semantic-issue\"")
            .Replace("\"to\": \"V-1\"", "\"to\": \"__semantic-issue\"")
            .Replace("\"from\": \"V-1\"", "\"from\": \"__semantic-issue\"");

        using var host = new RunnerHost();
        var resp = await host.Client.PostAsync("/flowsheets/validate", DocBody(semanticIssueDoc, semantic: true));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(body.GetProperty("valid").GetBoolean());
        var issues = body.GetProperty("issues").EnumerateArray().ToList();
        var unknown = Assert.Single(issues, i => i.GetProperty("code").GetString() == "UNKNOWN_COMPOUND");
        Assert.Equal("__semantic-issue", unknown.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task Missing_document_is_400_invalid_request()
    {
        using var host = new RunnerHost();
        var resp = await host.Client.PostAsJsonAsync("/flowsheets/validate", new { semantic = true });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unparsable_document_body_is_400_invalid_request()
    {
        using var host = new RunnerHost();
        var resp = await host.Client.PostAsync("/flowsheets/validate",
            new StringContent("not json at all", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Semantic_saturated_queue_returns_429_with_retry_after()
    {
        using var host = new RunnerHost(new() { ["MAX_CONCURRENT_SOLVES"] = "1" });

        // Admit-and-block one semantic request via a sleep tag, then wait for
        // its worker start marker so the gate is provably held before probing.
        var slowDoc = ValidDoc
            .Replace("\"tag\": \"V-1\"", "\"tag\": \"__sleep:8\"")
            .Replace("\"to\": \"V-1\"", "\"to\": \"__sleep:8\"")
            .Replace("\"from\": \"V-1\"", "\"from\": \"__sleep:8\"");
        var before = host.StartMarkers().Length;
        var slow = host.Client.PostAsync("/flowsheets/validate", DocBody(slowDoc, semantic: true));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.StartMarkers().Length <= before && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(host.StartMarkers().Length > before, "slow request never reached the worker");

        // Occupy the remaining queue slots one at a time (maxAdmitted = 1 × 5).
        // Each probe either blocks on the held gate (slot occupied) or is the
        // 429 rejection under test — no racing fire-and-forget batch.
        HttpResponseMessage? rejected = null;
        var blocked = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < 8 && rejected is null; i++)
        {
            var probe = host.Client.PostAsync("/flowsheets/validate", DocBody(ValidDoc, semantic: true));
            var completed = await Task.WhenAny(probe, Task.Delay(500));
            if (completed == probe && probe.Result.StatusCode == HttpStatusCode.TooManyRequests)
                rejected = probe.Result;
            else if (completed != probe)
                blocked.Add(probe);
        }

        Assert.NotNull(rejected);
        Assert.True(rejected!.Headers.Contains("Retry-After"));
        var body = await rejected.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("QUEUE_FULL", body.GetProperty("error").GetString());

        await slow;
        await Task.WhenAll(blocked);
    }
}
