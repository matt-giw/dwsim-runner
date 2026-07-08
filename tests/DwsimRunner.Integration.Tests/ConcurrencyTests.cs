// T066 — SC-007: 10 simultaneous build-solve requests ride the bounded
// worker queue without failures — every request gets a real result (the
// pool runs MAX_CONCURRENT_SOLVES wide and admits 5× that, so 10 distinct
// documents must all be admitted and solved).

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Concurrency")]
public class ConcurrencyTests
{
    [SkippableFact]
    public async Task Ten_simultaneous_build_solves_all_complete_without_failures()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // Ten distinct documents (different feed temperatures) — distinct
        // cache keys, so every request is real work for the pool.
        var requests = Enumerable.Range(0, 10).Select(i =>
        {
            var doc = BuildSolveTests.FlashDrumDoc.Replace("\"value\": -40", $"\"value\": {-40 - i}");
            return RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
                BuildSolveTests.BuildSolveBody(doc, timeoutSeconds: 300));
        }).ToList();

        var responses = await Task.WhenAll(requests);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var r in responses)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
            Assert.True(body.GetProperty("converged").GetBoolean(),
                "every queued build-solve must converge: " + body.GetProperty("warnings"));
        }
    }
}
