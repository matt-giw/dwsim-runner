// dwsim-runner Worker — GPL-3.0
// iskra 316 (ISK-581) — the wind turbine and the solar panel: the first units with NO material
// port. Numbers are the 2026-10-06 measurements (specs/316-expose-engine-unit-ops/research.md R1):
// wind power goes as speed³ with the Betz limit applied, solar is linear in irradiance. Each test
// changes one input and asserts the answer moves (GP-19): a parameter the engine ignored would
// hold the ledger's "capability" claim up with nothing behind it.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Generators")]
public class GeneratorTests
{
    private static string Wind(double windSpeed, string unit = "m/s", bool withPower = true) => $$"""
    {
      "schemaVersion": 1, "name": "316 wind", "compounds": ["Air"], "propertyPackage": "PR",
      "objects": [
        { "tag": "WT", "kind": "unitOp", "type": "windTurbine",
          "parameters": { "windSpeed": { "value": {{windSpeed}}, "unit": "{{unit}}" }, "diskArea": { "value": 100, "unit": "m2" },
                          "efficiency": 40, "numberOfUnits": 1 } }{{(withPower ? """, { "tag": "PWR", "kind": "energyStream" }""" : "")}}
      ],
      "connections": [{{(withPower ? """{ "from": "WT", "to": "PWR", "port": "Power Outlet" }""" : "")}}]
    }
    """;

    private static string Solar(double irradiation, string unit) => $$"""
    {
      "schemaVersion": 1, "name": "316 solar", "compounds": ["Air"], "propertyPackage": "PR",
      "objects": [
        { "tag": "SP", "kind": "unitOp", "type": "solarPanel",
          "parameters": { "solarIrradiation": { "value": {{irradiation}}, "unit": "{{unit}}" }, "panelArea": { "value": 10, "unit": "m2" },
                          "efficiency": 20, "numberOfUnits": 5 } },
        { "tag": "PWR", "kind": "energyStream" }
      ],
      "connections": [{ "from": "SP", "to": "PWR", "port": "Power Outlet" }]
    }
    """;

    private static async Task<(HttpStatusCode, JsonElement)> Post(string doc)
    {
        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", BuildSolveTests.BuildSolveBody(doc));
        return (resp.StatusCode, JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private static double PowerKw(JsonElement r) =>
        r.GetProperty("energy").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "PWR").GetProperty("dutyKw").GetDouble();

    [SkippableTheory]
    [InlineData(10.0, 13.864)]
    [InlineData(12.0, 23.957)]
    public async Task Wind_power_is_the_measured_value(double speed, double expectedKw)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (status, r) = await Post(Wind(speed));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(r.GetProperty("converged").GetBoolean(), r.ToString());
        Assert.Equal(expectedKw, PowerKw(r), 2);
    }

    /// ½·ρ·A·v³ × 16/27 × η: the ratio between 12 and 10 m/s is (1.2)³ = 1.728 whatever ρ is.
    [SkippableFact]
    public async Task Wind_power_goes_as_the_cube_of_the_speed()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (_, slow) = await Post(Wind(10.0));
        var (_, fast) = await Post(Wind(12.0));
        Assert.Equal(1.728, PowerKw(fast) / PowerKw(slow), 3);
    }

    /// 36 km/h is 10 m/s: the velocity table scales, and the engine reads the scaled number.
    [SkippableFact]
    public async Task Wind_speed_in_kilometres_per_hour_is_converted()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (_, a) = await Post(Wind(10.0));
        var (_, b) = await Post(Wind(36.0, "km/h"));
        Assert.Equal(PowerKw(a), PowerKw(b), 3);
    }

    /// A generator's only product is its energy stream; without one the engine dereferences null
    /// inside Calculate. The runner refuses first, by name.
    [SkippableFact]
    public async Task A_generator_without_its_power_stream_is_refused_before_the_solver()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (status, r) = await Post(Wind(10.0, withPower: false));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("MISSING_REQUIRED_PORT", r.ToString());
    }

    [SkippableTheory]
    [InlineData(1.0, "kW/m2", 10.0)]
    [InlineData(0.5, "kW/m2", 5.0)]
    [InlineData(850.0, "W/m2", 8.5)]
    public async Task Solar_power_is_irradiance_times_area_times_efficiency_times_count(double irr, string unit, double expectedKw)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var (status, r) = await Post(Solar(irr, unit));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(r.GetProperty("converged").GetBoolean(), r.ToString());
        Assert.Equal(expectedKw, PowerKw(r), 3);
    }
}
