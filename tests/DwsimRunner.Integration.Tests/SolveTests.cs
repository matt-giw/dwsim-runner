// T009 — US1 Tier B: real solves of methanol_synthesis against a running
// runner. Pins physical values (incl. the fixed 1000× energy bug), override
// scaling, descriptive errors, and cache determinism.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

public class SolveTests
{
    private const string Template = "methanol_synthesis";

    private static async Task<JsonElement> SolveAsync(object payload)
    {
        var resp = await RunnerConnection.Client.PostAsJsonAsync("/solve", payload);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static double Duty(JsonElement result, string name) =>
        result.GetProperty("energy").EnumerateArray()
              .First(e => e.GetProperty("name").GetString() == name)
              .GetProperty("dutyKw").GetDouble();

    [SkippableFact]
    public async Task Baseline_converges_with_known_energy_values()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var r = await SolveAsync(new { templateId = Template });

        Assert.True(r.GetProperty("converged").GetBoolean());
        Assert.InRange(Duty(r, "W_comp"), 10.5, 12.5);   // ~11.5 kW — kW, not MW×1000
        Assert.InRange(Duty(r, "Q_rx"), -3.0, -2.0);      // ~-2.5 kW
    }

    [SkippableFact]
    public async Task Doubled_feed_scales_flows_and_duties()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var r = await SolveAsync(new
        {
            templateId = Template,
            overrides = new[] { new { @object = "Syngas", property = "massflow", value = 230.7, unit = "kg/h" } },
        });

        Assert.True(r.GetProperty("converged").GetBoolean());
        var syngas = r.GetProperty("streams").EnumerateArray()
                      .First(s => s.GetProperty("name").GetString() == "Syngas");
        Assert.InRange(syngas.GetProperty("massFlowKgH").GetDouble(), 230.0, 231.5);
        Assert.InRange(Duty(r, "W_comp"), 21.5, 24.5);    // ~2× baseline
    }

    [SkippableFact]
    public async Task Unknown_object_tag_is_400_listing_available_tags()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var resp = await RunnerConnection.Client.PostAsJsonAsync("/solve", new
        {
            templateId = Template,
            overrides = new[] { new { @object = "Syngass", property = "massflow", value = 100.0 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_OBJECT", body.GetProperty("error").GetString());
        Assert.Contains("Syngas", body.GetProperty("detail").GetString());
    }

    [SkippableFact]
    public async Task Unsupported_stream_property_is_400_not_a_silent_warning()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var resp = await RunnerConnection.Client.PostAsJsonAsync("/solve", new
        {
            templateId = Template,
            overrides = new[] { new { @object = "Syngas", property = "enthalpy", value = 1.0 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_PROPERTY", body.GetProperty("error").GetString());
    }

    [SkippableFact]
    public async Task Identical_solves_return_byte_identical_bodies()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var payload = new
        {
            templateId = Template,
            overrides = new[] { new { @object = "Syngas", property = "massflow", value = 173.0, unit = "kg/h" } },
        };

        var r1 = await RunnerConnection.Client.PostAsJsonAsync("/solve", payload);
        var r2 = await RunnerConnection.Client.PostAsJsonAsync("/solve", payload);

        Assert.Equal(await r1.Content.ReadAsStringAsync(), await r2.Content.ReadAsStringAsync());
    }
}
