// T030 — US1 Tier B: semantic validation against real DWSIM catches an
// unknown compound (with suggestions) and a bad port, without ever running a
// solve. Collect-all per FR-VAL-003: both defects come back in one response.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Validate")]
public class ValidateTests
{
    private static StringContent ValidateBody(string doc, bool semantic = true)
    {
        var payload = new { document = JsonSerializer.Deserialize<JsonElement>(doc), semantic };
        return new(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
    }

    [SkippableFact]
    public async Task Unknown_compound_is_flagged_semantically_with_suggestions_and_no_solve()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // "Methan" (typo) — structurally fine, semantically unknown.
        var doc = BuildSolveTests.FlashDrumDoc
            .Replace("\"Methane\", \"Ethane\"", "\"Methan\", \"Ethane\"")
            .Replace("\"Methane\": 0.5", "\"Methan\": 0.5");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/validate", ValidateBody(doc));
        clock.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.False(r.GetProperty("valid").GetBoolean());
        var issue = r.GetProperty("issues").EnumerateArray()
            .First(i => i.GetProperty("code").GetString() == "UNKNOWN_COMPOUND");
        Assert.Contains("Methane", issue.GetProperty("message").GetString());   // suggestion present
        // Validation never solves — a solve of even this trivial sheet plus a
        // worker spawn stays well under the solve timeout; the real guarantee
        // is behavioral (build without CalculateFlowsheet), pinned Tier A.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90));
    }

    [SkippableFact]
    public async Task Bad_port_is_rejected_structurally_naming_the_valid_ports()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var doc = BuildSolveTests.FlashDrumDoc.Replace("\"port\": \"Vapor Outlet\"", "\"port\": \"Vapour Out\"");
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/validate", ValidateBody(doc));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.False(r.GetProperty("valid").GetBoolean());
        var issue = r.GetProperty("issues").EnumerateArray()
            .First(i => i.GetProperty("code").GetString() == "UNKNOWN_PORT");
        Assert.Contains("Vapor Outlet", issue.GetProperty("message").GetString());   // valid ports named
    }

    // FlashDrumDoc's shape with LIQ (and its connection) omitted: one dangling required port.
    internal const string DanglingPortDoc = """
    {
      "schemaVersion": 1,
      "name": "dangling required port",
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": -40, "unit": "C" },
                    "pressure": { "value": 10, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
        { "tag": "V-1", "kind": "unitOp", "type": "separator" },
        { "tag": "VAP", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "V-1", "port": "Inlet" },
        { "from": "V-1", "to": "VAP", "port": "Vapor Outlet" }
      ]
    }
    """;

    // 121 T001 — pins for the required-port enforcement (`DocumentValidator.cs`, 19e4d60).
    // The check shipped 2026-07-30 with ZERO tests referencing MISSING_REQUIRED_PORT: it
    // could be deleted and every tier stayed green. These pin the refusal, the naming, and
    // the collect-all property. (Red demonstrated against pre-19e4d60 behavior, where this
    // exact document returned valid:true — specs/121 research.md R1.)
    [SkippableFact]
    public async Task Dangling_required_port_is_refused_naming_unit_and_port()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // The flash drum with the separator's required 'Liquid Outlet' unpiped. DWSIM
        // dereferences that outlet unconditionally at Calculate. (Self-contained rather
        // than a Replace over FlashDrumDoc — raw-string dedent makes textual surgery
        // whitespace-coupled.)
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/validate", ValidateBody(DanglingPortDoc));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.False(r.GetProperty("valid").GetBoolean());
        var issue = r.GetProperty("issues").EnumerateArray()
            .First(i => i.GetProperty("code").GetString() == "MISSING_REQUIRED_PORT");
        Assert.Equal("V-1", issue.GetProperty("tag").GetString());
        Assert.Contains("Liquid Outlet", issue.GetProperty("message").GetString());
    }

    [SkippableFact]
    public async Task Two_units_with_dangling_ports_are_both_reported_in_one_response()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // Collect-all (FR-VAL-003): two separators, each missing its liquid outlet — both
        // issues in one response, not first-failure.
        const string doc = """
        {
          "schemaVersion": 1,
          "name": "collect-all dangling ports",
          "compounds": ["Methane", "Ethane"],
          "propertyPackage": "PR",
          "objects": [
            { "tag": "FEED", "kind": "materialStream",
              "spec": { "temperature": { "value": -40, "unit": "C" },
                        "pressure": { "value": 10, "unit": "bar" },
                        "massFlow": { "value": 100, "unit": "kg/h" },
                        "composition": { "basis": "molar",
                                         "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
            { "tag": "V-1", "kind": "unitOp", "type": "separator" },
            { "tag": "MID", "kind": "materialStream" },
            { "tag": "V-2", "kind": "unitOp", "type": "separator" },
            { "tag": "VAP", "kind": "materialStream" }
          ],
          "connections": [
            { "from": "FEED", "to": "V-1", "port": "Inlet" },
            { "from": "V-1", "to": "MID", "port": "Vapor Outlet" },
            { "from": "MID", "to": "V-2", "port": "Inlet" },
            { "from": "V-2", "to": "VAP", "port": "Vapor Outlet" }
          ]
        }
        """;
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/validate", ValidateBody(doc));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.False(r.GetProperty("valid").GetBoolean());
        var tags = r.GetProperty("issues").EnumerateArray()
            .Where(i => i.GetProperty("code").GetString() == "MISSING_REQUIRED_PORT")
            .Select(i => i.GetProperty("tag").GetString())
            .OrderBy(t => t)
            .ToArray();
        Assert.Equal(new[] { "V-1", "V-2" }, tags);
    }

    [SkippableFact]
    public async Task Valid_document_passes_semantic_validation()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/validate", ValidateBody(BuildSolveTests.FlashDrumDoc));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(r.GetProperty("valid").GetBoolean(),
            "expected valid document, got issues: " + r.GetProperty("issues"));
    }
}
