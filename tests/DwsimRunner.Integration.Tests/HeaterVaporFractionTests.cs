using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

// iskra 285 (ISK-485, GP-19) — a heater in `outletVaporFraction` mode drives the PVF flash with the
// stated fraction. Spec 099/200 recorded the property absent on Heater; on DWSIM 9.0.5 it exists
// (m_VFout). The observable is the outlet's vapour fraction AND the duty (099's lesson: on the
// saturation line the temperature does not move).
[Trait("Category", "CalcMode")]
public class HeaterVaporFractionTests
{
    private static string Doc(string parameters) => $$"""
    {
      "schemaVersion": 1, "name": "285 heater vf", "compounds": ["Water"], "propertyPackage": "STEAM",
      "objects": [
        { "tag": "Feed", "kind": "materialStream",
          "spec": { "temperature": { "value": 25, "unit": "C" }, "pressure": { "value": 1.01325, "unit": "bar" },
                    "massFlow": { "value": 1, "unit": "kg/s" }, "composition": { "basis": "molar", "fractions": { "Water": 1 } } } },
        { "tag": "H-1", "kind": "unitOp", "type": "heater", "parameters": {{parameters}} },
        { "tag": "Out", "kind": "materialStream" }
      ],
      "connections": [ { "from": "Feed", "to": "H-1", "port": "Inlet" }, { "from": "H-1", "to": "Out", "port": "Outlet" } ]
    }
    """;

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Solve(string parameters)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve",
            new StringContent($"{{\"document\":{Doc(parameters)}}}", Encoding.UTF8, "application/json"));
        return (resp.StatusCode, JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private static (double Vf, double Duty) Outlet(JsonElement r) => (
        r.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Out").GetProperty("vaporFraction").GetDouble(),
        r.GetProperty("unitOps").EnumerateArray().Single(u => u.GetProperty("name").GetString() == "H-1").GetProperty("dutyKw").GetDouble());

    [SkippableFact]
    public async Task The_stated_vapour_fraction_reaches_the_engine_and_moves_the_outlet_and_the_duty()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (s1, r1) = await Solve("""{ "calcMode": "outletVaporFraction", "outletVaporFraction": 1 }""");
        var (s5, r5) = await Solve("""{ "calcMode": "outletVaporFraction", "outletVaporFraction": 0.5 }""");
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal(HttpStatusCode.OK, s5);
        var (vf1, q1) = Outlet(r1);
        var (vf5, q5) = Outlet(r5);
        Assert.Equal(1.0, vf1, 3);                 // saturated vapour
        Assert.Equal(0.5, vf5, 3);                 // two-phase
        Assert.InRange(q1, 2500, 2650);            // ~314 kW sensible + ~2257 kW latent for 1 kg/s
        Assert.InRange(q1 - q5, 1100, 1160);       // half the latent heat
    }

    [SkippableFact]
    public async Task An_explicit_other_mode_with_the_value_is_refused_by_name()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (status, body) = await Solve("""{ "calcMode": "heatAdded", "heatDuty": { "value": 100, "unit": "kW" }, "outletVaporFraction": 0.5 }""");
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Contains("PARAMETER_NOT_READ_BY_MODE", body.GetRawText());
    }

    [SkippableFact]
    public async Task The_value_alone_selects_its_mode_and_is_never_silently_inert()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (status, r) = await Solve("""{ "outletVaporFraction": 0.5 }""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0.5, Outlet(r).Vf, 3);
    }
}
