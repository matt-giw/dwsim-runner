// T017 — US2 Tier B: turndown comparison on methanol_synthesis; valid case
// results must byte-equal the corresponding individual /solve results (FR-008).

using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

public class CompareTests
{
    private const string Template = "methanol_synthesis";

    [SkippableFact]
    public async Task Turndown_set_returns_per_case_results_and_errors()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var scaled = new[] { new { @object = "Syngas", property = "massflow", value = 173.0, unit = "kg/h" } };
        var resp = await RunnerConnection.Client.PostAsJsonAsync("/compare", new
        {
            templateId = Template,
            cases = new Dictionary<string, object>
            {
                ["base"] = Array.Empty<object>(),
                ["150%"] = scaled,
                ["bad"] = new[] { new { @object = "Syngass", property = "massflow", value = 100.0 } },
            },
        });

        resp.EnsureSuccessStatusCode();
        var results = JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync()).GetProperty("results");

        Assert.True(results.GetProperty("base").GetProperty("converged").GetBoolean());
        Assert.True(results.GetProperty("150%").GetProperty("converged").GetBoolean());
        Assert.Equal("INVALID_OBJECT", results.GetProperty("bad").GetProperty("error").GetString());

        // FR-008: the compare case equals an individual solve of the same inputs
        var solveBody = await (await RunnerConnection.Client.PostAsJsonAsync("/solve",
            new { templateId = Template, overrides = scaled })).Content.ReadAsStringAsync();
        Assert.Equal(solveBody, results.GetProperty("150%").GetRawText());
    }
}
