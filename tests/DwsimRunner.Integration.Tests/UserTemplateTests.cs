// T041 — US3 Tier B (SC-004, quickstart Scenario 3): a flowsheet built from a
// document persists as a user template loadable by the spec-001 pipeline —
// /solve with overrides works and matches a direct build-solve; deletion is
// safe while a /compare on the same template is in flight.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "UserTemplate")]
public class UserTemplateTests
{
    private static StringContent SaveBody(string doc, string id) =>
        new(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["document"] = JsonSerializer.Deserialize<JsonElement>(doc),
            ["timeoutSeconds"] = 180,
            ["saveAsTemplate"] = new Dictionary<string, object?> { ["id"] = id, ["overwrite"] = true },
        }), System.Text.Encoding.UTF8, "application/json");

    [SkippableFact]
    public async Task Saved_template_resolves_via_solve_with_override_and_matches_direct_build()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        const string id = "it-flash-drum";

        // Save (overwrite so reruns are stable).
        var save = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            SaveBody(BuildSolveTests.FlashDrumDoc, id));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saveBody = JsonSerializer.Deserialize<JsonElement>(await save.Content.ReadAsStringAsync());
        Assert.Equal("user", saveBody.GetProperty("template").GetProperty("source").GetString());

        try
        {
            // Listed as source:"user".
            var listing = JsonSerializer.Deserialize<JsonElement>(
                await RunnerConnection.Client.GetStringAsync("/templates"));
            var entry = listing.EnumerateArray().First(t => t.GetProperty("id").GetString() == id);
            Assert.Equal("user", entry.GetProperty("source").GetString());
            Assert.True(entry.GetProperty("solvedAtSave").GetBoolean());

            // Baseline /solve parity with the direct build-solve (SC-004).
            var solve = await RunnerConnection.Client.PostAsJsonAsync("/solve",
                new { templateId = id, timeoutSeconds = 180 });
            Assert.Equal(HttpStatusCode.OK, solve.StatusCode);
            var solved = JsonSerializer.Deserialize<JsonElement>(await solve.Content.ReadAsStringAsync());
            Assert.True(solved.GetProperty("converged").GetBoolean());
            Assert.InRange(
                Math.Abs(BuildSolveTests.MassFlow(solved, "VAP") - BuildSolveTests.MassFlow(saveBody, "VAP")),
                0, 0.5);   // same engine, same flowsheet — same split

            // /solve with an override actually re-solves at new conditions.
            // The feed is all-vapor at -40 °C / 10 bar; chilling to -80 °C
            // condenses most of the ethane, so the vapor draw must shrink.
            var cold = await RunnerConnection.Client.PostAsJsonAsync("/solve", new
            {
                templateId = id,
                timeoutSeconds = 180,
                overrides = new[] { new { @object = "FEED", property = "temperature", value = -80.0, unit = "C" } },
            });
            Assert.Equal(HttpStatusCode.OK, cold.StatusCode);
            var coldBody = JsonSerializer.Deserialize<JsonElement>(await cold.Content.ReadAsStringAsync());
            Assert.True(coldBody.GetProperty("converged").GetBoolean());
            Assert.True(BuildSolveTests.MassFlow(coldBody, "VAP") < BuildSolveTests.MassFlow(solved, "VAP"),
                "chilling the feed must condense liquid and reduce the vapor draw");
            Assert.True(BuildSolveTests.MassFlow(coldBody, "LIQ") > 1.0,
                "chilled flash must produce a real liquid stream");
        }
        finally
        {
            await RunnerConnection.Client.DeleteAsync($"/templates/{id}");
        }
    }

    [SkippableFact]
    public async Task Delete_during_inflight_compare_completes_the_compare_then_404s()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        const string id = "it-delete-race";

        (await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            SaveBody(BuildSolveTests.FlashDrumDoc, id))).EnsureSuccessStatusCode();

        // Kick off a compare, then delete while it runs.
        var compareTask = RunnerConnection.Client.PostAsJsonAsync("/compare", new
        {
            templateId = id,
            timeoutSeconds = 180,
            cases = new Dictionary<string, object[]>
            {
                ["cold"] = [new { @object = "FEED", property = "temperature", value = -50.0, unit = "C" }],
                ["warm"] = [new { @object = "FEED", property = "temperature", value = -20.0, unit = "C" }],
            },
        });
        await Task.Delay(500);   // let the compare admit + start
        var del = await RunnerConnection.Client.DeleteAsync($"/templates/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // The in-flight compare completes (workers already hold the file open
        // or fail per-case — never a whole-request crash)…
        var compare = await compareTask;
        Assert.True(compare.StatusCode is HttpStatusCode.OK,
            $"in-flight compare must complete, got {(int)compare.StatusCode}");

        // …and subsequent requests see 404.
        var after = await RunnerConnection.Client.PostAsJsonAsync("/solve", new { templateId = id });
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }
}
