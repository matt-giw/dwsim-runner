// 120-runner-dwsim-parity US5 (T022) — Tier A: /compare and /optimize accept a DOCUMENT.
//
// Both endpoints were template-only, which is why they had zero consumers: iskra's whole model
// is the document as the single source of truth (014), and "save a template first" routes a
// sweep through a guarded outward-effect action. Written FAILING (both endpoints 400/404 a
// document request today). Rules pinned: templateId XOR document; per-case failures — including
// per-case document/override failures — land in their slot, the batch continues; a sweep is a
// compare whose cases the caller expanded (deliberately no /sweep endpoint — research R7).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class DocumentScopedTests
{
    private static readonly object NoOverrides = Array.Empty<object>();

    // Same shape as BuildSolveEndpointTests.ValidDoc (kept local: that one is private).
    private const string ValidDoc = """
    {
      "schemaVersion": 1,
      "name": "document-scoped cases",
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

    [Fact]
    public async Task Compare_accepts_a_document_and_returns_labeled_results()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            document = Doc(),
            cases = new Dictionary<string, object> { ["base"] = NoOverrides, ["hot"] = NoOverrides },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.True(results.GetProperty("base").GetProperty("converged").GetBoolean());
        Assert.True(results.GetProperty("hot").GetProperty("converged").GetBoolean());
    }

    [Fact]
    public async Task Compare_with_both_template_and_document_is_400_conflicting()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            templateId = "t",
            document = Doc(),
            cases = new Dictionary<string, object> { ["a"] = NoOverrides },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("CONFLICTING_PARAMETERS", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Compare_with_neither_template_nor_document_is_400()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            cases = new Dictionary<string, object> { ["a"] = NoOverrides },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_failing_document_case_lands_in_its_slot_and_the_batch_continues()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            document = Doc(),
            cases = new Dictionary<string, object>
            {
                ["good"] = NoOverrides,
                ["bad"] = new[] { new { @object = "__exit:2", property = "x", value = 0.0 } },
            },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.True(results.GetProperty("good").GetProperty("converged").GetBoolean());
        Assert.True(results.GetProperty("bad").TryGetProperty("error", out _),
            "the failing case should carry its error in its slot");
    }

    [Fact]
    public async Task Optimize_accepts_a_document()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/optimize", new
        {
            document = Doc(),
            variable = new { @object = "FEED", property = "massflow", min = 50.0, max = 150.0 },
            objective = new { @object = "VAP", property = "massflow", direction = "maximize" },
            maxEvaluations = 4,
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("best", out _) || body.TryGetProperty("evaluations", out _),
            "optimize should return its normal result shape for a document");
    }

    [Fact]
    public async Task Optimize_with_both_template_and_document_is_400_conflicting()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/optimize", new
        {
            templateId = "t",
            document = Doc(),
            variable = new { @object = "FEED", property = "massflow", min = 50.0, max = 150.0 },
            objective = new { @object = "VAP", property = "massflow", direction = "maximize" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("CONFLICTING_PARAMETERS", await resp.Content.ReadAsStringAsync());
    }
}
