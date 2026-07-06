// T007 Tier B guard — the worker's stdout contract: a solve response must be
// exactly one parseable JSON document (DWSIM console noise must never reach
// stdout).

using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

public class WorkerProtocolTests
{
    [SkippableFact]
    public async Task Solve_response_is_a_single_json_document()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsJsonAsync("/solve",
            new { templateId = "methanol_synthesis" });
        var text = await resp.Content.ReadAsStringAsync();

        resp.EnsureSuccessStatusCode();
        var doc = JsonSerializer.Deserialize<JsonElement>(text); // throws on trailing/leading noise
        Assert.True(doc.TryGetProperty("converged", out _));
    }
}
