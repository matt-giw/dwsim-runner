// T022 — US5 Tier B: real inventory + unit-op results for methanol_synthesis.

using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

public class ObjectsAndUnitOpsTests
{
    private const string Template = "methanol_synthesis";

    [SkippableFact]
    public async Task Inventory_lists_streams_and_unit_ops_with_settable_properties()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var body = JsonSerializer.Deserialize<JsonElement>(
            await RunnerConnection.Client.GetStringAsync($"/templates/{Template}/objects"));
        var objects = body.GetProperty("objects").EnumerateArray().ToList();

        var syngas = objects.First(o => o.GetProperty("tag").GetString() == "Syngas");
        Assert.Equal("materialStream", syngas.GetProperty("type").GetString());
        Assert.Equal(
            new[] { "massflow", "molarflow", "pressure", "temperature" },
            syngas.GetProperty("settableProperties").EnumerateArray()
                  .Select(p => p.GetString()).OrderBy(x => x).ToArray());

        Assert.Contains(objects, o => o.GetProperty("type").GetString() == "compressor");
        var wcomp = objects.First(o => o.GetProperty("tag").GetString() == "W_comp");
        Assert.Equal("energyStream", wcomp.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task Solve_reports_compressor_power_consistent_with_energy_stream()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsJsonAsync("/solve", new { templateId = Template });
        resp.EnsureSuccessStatusCode();
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

        var comp = r.GetProperty("unitOps").EnumerateArray()
                    .First(u => u.GetProperty("type").GetString() == "compressor");
        Assert.InRange(comp.GetProperty("powerKw").GetDouble(), 10.5, 12.5); // ≈ W_comp duty
    }
}
