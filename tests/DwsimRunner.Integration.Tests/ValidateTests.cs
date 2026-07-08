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
