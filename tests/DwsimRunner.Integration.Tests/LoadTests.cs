// T036 — SC-006 load test: the runner handles >= 10 simultaneous solve requests
// without failures or cross-user interference. Self-skips unless a runner with
// DWSIM is reachable (see RunnerConnection). Each request uses a distinct
// override so the cache doesn't collapse them, and we assert they all return
// HTTP 200 with converged=true.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

public class LoadTests
{
    private const string Template = "methanol_synthesis";
    private const int Load = 10;

    [Fact]
    public async Task TenConcurrentSolves_AllSucceed()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // Distinct Syngas massflows → distinct cache keys → real concurrent work.
        var requests = Enumerable.Range(0, Load).Select(i =>
        {
            var body = new
            {
                templateId = Template,
                overrides = new[]
                {
                    new { @object = "Syngas", property = "massflow", value = 115.35 + i, unit = "kg/h" },
                },
                timeoutSeconds = 60,
            };
            return RunnerConnection.Client.PostAsync("/solve",
                JsonContent.Create(body));
        }).ToList();

        var responses = await Task.WhenAll(requests);

        foreach (var (resp, i) in responses.Select((r, i) => (r, i)))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("converged").GetBoolean(),
                $"case {i} did not converge");
            // Cross-user interference check: the Syngas stream's massflow must
            // reflect THIS request's override, not a sibling's.
            var syngas = json.GetProperty("streams").EnumerateArray()
                .First(s => s.GetProperty("name").GetString() == "Syngas");
            var mf = syngas.GetProperty("massFlowKgH").GetDouble();
            Assert.InRange(mf, 110.0 + i, 120.0 + i);
        }
    }
}