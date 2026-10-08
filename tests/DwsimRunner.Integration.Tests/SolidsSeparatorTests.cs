// dwsim-runner Worker — GPL-3.0
// iskra 316 (ISK-581) — the solids separator, on the defined-solid machinery spec 281 landed.
// Measured 2026-10-06 (specs/316-expose-engine-unit-ops/research.md R1): iron in 700 °C steam
// under RAOULT splits cleanly and the efficiency moves the split; a 25 °C slurry is refused by
// 281's own rule because the engine dissolves part of the iron into the liquid. The ceiling is
// asserted here so it stays a recorded fact rather than a surprise.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Solids")]
public class SolidsSeparatorTests
{
    private static object Q(double value, string unit) => new { value, unit };

    private static Dictionary<string, object?> Doc(double feedC, double efficiencyPct) => new()
    {
        ["schemaVersion"] = 1,
        ["name"] = "316 solids separator",
        ["compounds"] = new[] { "Water", "Iron" },
        ["propertyPackage"] = "RAOULT",
        ["compoundDefinitions"] = new[] { SolidsTests.Iron },
        ["objects"] = new object[]
        {
            new { tag = "F", kind = "materialStream", spec = new {
                temperature = Q(feedC, "C"), pressure = Q(1.01325, "bar"), massFlow = Q(1000, "kg/h"),
                composition = new { basis = "molar", fractions = new Dictionary<string, double> { ["Water"] = 0.9, ["Iron"] = 0.1 } } } },
            new { tag = "SS", kind = "unitOp", type = "solidsSeparator",
                  parameters = new { separationEfficiency = efficiencyPct, liquidSeparationEfficiency = 100.0 } },
            new { tag = "O1", kind = "materialStream" },
            new { tag = "O2", kind = "materialStream" },
        },
        ["connections"] = new[]
        {
            new { from = "F", to = "SS", port = "Inlet" },
            new { from = "SS", to = "O1", port = "Outlet 1" },
            new { from = "SS", to = "O2", port = "Outlet 2" },
        },
    };

    private static async Task<(HttpStatusCode, JsonElement)> Post(Dictionary<string, object?> doc)
    {
        var body = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?> { ["document"] = doc, ["timeoutSeconds"] = 120 }),
            System.Text.Encoding.UTF8, "application/json");
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", body);
        return (resp.StatusCode, JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private static JsonElement Stream(JsonElement r, string name) =>
        r.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    /// 1000 kg/h at 0.1 mol iron: 1000 × (0.1 × 55.845) / (0.9 × 18.015 + 0.1 × 55.845) = 256.2 kg/h.
    [SkippableTheory]
    [InlineData(100.0, 256.19)]
    [InlineData(50.0, 128.10)]
    public async Task Efficiency_moves_the_solid_split(double efficiencyPct, double expectedIronKgH)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (status, r) = await Post(Doc(700, efficiencyPct));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(r.GetProperty("converged").GetBoolean(), r.ToString());

        var solids = Stream(r, "O2");
        Assert.Equal(expectedIronKgH, solids.GetProperty("massFlowKgH").GetDouble(), 1);
        Assert.Equal(1.0, solids.GetProperty("compositionMol").GetProperty("Iron").GetDouble(), 6);
        Assert.Equal("solid", solids.GetProperty("phase").GetString());
    }

    /// Outlet 1 is the fluid: at 100 % it carries no iron at all.
    [SkippableFact]
    public async Task Outlet_1_is_the_fluid()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (_, r) = await Post(Doc(700, 100.0));
        var fluid = Stream(r, "O1");
        Assert.Equal(743.8, fluid.GetProperty("massFlowKgH").GetDouble(), 0);
        Assert.False(fluid.GetProperty("compositionMol").TryGetProperty("Iron", out var fe) && fe.GetDouble() > 1e-9, fluid.ToString());
    }

    /// The ceiling: a liquid slurry is refused by 281's rule, because the engine places part of the
    /// defined solid in the liquid phase. Not this unit's defect, and not silently a split of less iron.
    [SkippableFact]
    public async Task A_liquid_slurry_is_refused_by_281s_rule_not_split()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (status, r) = await Post(Doc(25, 100.0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("SOLID_REPORTED_AS_FLUID", r.GetProperty("error").GetString());
    }
}
