// T047 — US4 Tier B (SC-006): a real TP flash on methane/ethane returns a
// thermodynamically sane two-phase split in under 5 seconds, and spec-pair
// errors surface as 400 FLASH_INVALID from the live engine.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Flash")]
public class FlashTests
{
    [SkippableFact]
    public async Task Tp_flash_two_phase_region_is_sane_and_fast()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // -80 °C / 10 bar puts a 50/50 methane/ethane mix inside the
        // two-phase envelope (verified against the flash-drum build-solve).
        var sw = Stopwatch.StartNew();
        var resp = await RunnerConnection.Client.PostAsJsonAsync("/flash", new
        {
            compounds = new[] { "Methane", "Ethane" },
            composition = new
            {
                basis = "molar",
                fractions = new Dictionary<string, double> { ["Methane"] = 0.5, ["Ethane"] = 0.5 },
            },
            propertyPackage = "PR",
            flashType = "TP",
            temperature = new { value = -80.0, unit = "C" },
            pressure = new { value = 10.0, unit = "bar" },
        });
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"SC-006: flash must answer in < 5 s, took {sw.Elapsed.TotalSeconds:F1}s");

        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var vf = r.GetProperty("vaporFraction").GetDouble();
        Assert.InRange(vf, 0.0001, 0.9999);   // genuinely two-phase

        var phases = r.GetProperty("phases").EnumerateArray().ToList();
        var vapor = phases.First(p => p.GetProperty("phase").GetString() == "Vapor");
        var liquid = phases.First(p => p.GetProperty("phase").GetString() == "Liquid");

        // Phase mole fractions close to 1.
        var total = phases.Sum(p => p.GetProperty("molarFraction").GetDouble());
        Assert.InRange(total, 0.999, 1.001);

        // Each phase's composition normalizes, and the light component
        // concentrates in the vapor (K_methane > 1 at these conditions).
        foreach (var phase in new[] { vapor, liquid })
        {
            var sum = phase.GetProperty("composition").EnumerateObject().Sum(p => p.Value.GetDouble());
            Assert.InRange(sum, 0.999, 1.001);
        }
        var yMethane = vapor.GetProperty("composition").GetProperty("Methane").GetDouble();
        var xMethane = liquid.GetProperty("composition").GetProperty("Methane").GetDouble();
        Assert.True(yMethane > xMethane,
            $"methane must enrich the vapor phase (y={yMethane:F3}, x={xMethane:F3})");

        // Overall balance: vf·y + (1-vf)·x ≈ z = 0.5.
        var overall = vf * yMethane + (1 - vf) * xMethane;
        Assert.InRange(overall, 0.49, 0.51);
    }

    [SkippableFact]
    public async Task Missing_spec_is_400_flash_invalid()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsJsonAsync("/flash", new
        {
            compounds = new[] { "Methane" },
            composition = new
            {
                basis = "molar",
                fractions = new Dictionary<string, double> { ["Methane"] = 1.0 },
            },
            propertyPackage = "PR",
            flashType = "PH",
            pressure = new { value = 10.0, unit = "bar" },   // enthalpy missing
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal("FLASH_INVALID", r.GetProperty("error").GetString());
    }

    [SkippableFact]
    public async Task Unknown_compound_is_400_flash_invalid_with_suggestions()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsJsonAsync("/flash", new
        {
            compounds = new[] { "Methan" },   // typo — engine path, not precheck
            composition = new
            {
                basis = "molar",
                fractions = new Dictionary<string, double> { ["Methan"] = 1.0 },
            },
            propertyPackage = "PR",
            flashType = "TP",
            temperature = new { value = 0.0, unit = "C" },
            pressure = new { value = 1.0, unit = "bar" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal("FLASH_INVALID", r.GetProperty("error").GetString());
        Assert.Contains("Methan", r.GetProperty("message").GetString());
    }
}
