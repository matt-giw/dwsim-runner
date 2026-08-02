// 120-runner-dwsim-parity US2 (T014) — Tier B: the remaining flash specification pairs.
//
// The engine's FlashCalculationType carries nine pairs; the runner exposed three (TP/PH/PS).
// These tests were written FAILING (each new pair 400'd "not supported (TP|PH|PS)") and gate
// the switch extension. Every pair is verified by ROUND TRIP from a TP reference at 25 C /
// 1.01325 bar — a pair that cannot recover the reference state is not exposed, it is recorded
// measured-unavailable in the capability fixture. Solid-fraction pairs (PSF/TSF) are
// deliberately absent: solids are ledgered will-not-yet (no flash-algorithm selection).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "FlashPairs")]
public class FlashPairsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Req(string flashType, object specs) => new
    {
        compounds = new[] { "Water" },
        composition = new { basis = "molar", fractions = new Dictionary<string, double> { ["Water"] = 1 } },
        propertyPackage = "STEAM",
        flashType,
        temperature = GetSpec(specs, "temperature"),
        pressure = GetSpec(specs, "pressure"),
        enthalpy = GetSpec(specs, "enthalpy"),
        entropy = GetSpec(specs, "entropy"),
        vaporFraction = GetSpec(specs, "vaporFraction"),
    };

    private static object? GetSpec(object specs, string name) =>
        specs.GetType().GetProperty(name)?.GetValue(specs);

    private static async Task<JsonElement> Flash(object body)
    {
        var resp = await RunnerConnection.Client.PostAsJsonAsync("/flash", body, Json);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"flash failed ({resp.StatusCode}): {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static async Task<JsonElement> Reference() =>
        await Flash(Req("TP", new
        {
            temperature = new { value = 25.0, unit = "C" },
            pressure = new { value = 1.01325, unit = "bar" },
        }));

    private static double Num(JsonElement e, string prop) => e.GetProperty(prop).GetDouble();

    // MEASURED 2026-08-01 (9.0.5.0): TemperatureEnthalpy and TemperatureEntropy flashes
    // CRASH the engine — hard worker death (WORKER_CRASH), not an exception — under both
    // STEAM and PR. The pairs are therefore NOT exposed, and these tests pin the refusal.
    // If DWSIM fixes the crash, re-measure: flip these to round-trip tests and re-add the
    // switch cases (worker AND API — the precheck duplicates the switch by design).
    [SkippableTheory]
    [InlineData("TH")]
    [InlineData("TS")]
    public async Task Crashing_pairs_are_refused_not_exposed(string pair)
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        var reference = await Reference();

        var resp = await RunnerConnection.Client.PostAsJsonAsync("/flash", Req(pair, new
        {
            temperature = new { value = 25.0, unit = "C" },
            enthalpy = new { value = Num(reference, "enthalpyKJKg"), unit = "kJ/kg" },
            entropy = new { value = Num(reference, "entropyKJKgK"), unit = "kJ/[kg.K]" },
        }), Json);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("FLASH_INVALID", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task PVF_at_half_vapor_finds_the_boiling_point()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var pvf = await Flash(Req("PVF", new
        {
            pressure = new { value = 1.01325, unit = "bar" },
            vaporFraction = new { value = 0.5, unit = "" },
        }));
        // Water at 1 atm, vf 0.5 sits ON the boiling point: 100 C.
        Assert.InRange(Num(pvf, "temperatureC"), 99.5, 100.5);
        Assert.InRange(pvf.GetProperty("vaporFraction").GetDouble(), 0.49, 0.51);
    }

    [SkippableFact]
    public async Task TVF_at_half_vapor_finds_the_saturation_pressure()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var tvf = await Flash(Req("TVF", new
        {
            temperature = new { value = 100.0, unit = "C" },
            vaporFraction = new { value = 0.5, unit = "" },
        }));
        // Water at 100 C, vf 0.5: saturation pressure ~1.01325 bar.
        Assert.InRange(Num(tvf, "pressureBar"), 0.98, 1.05);
    }

    [SkippableFact]
    public async Task An_unsupported_pair_is_refused_naming_the_verdict()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsJsonAsync("/flash", Req("PSF", new
        {
            pressure = new { value = 1.01325, unit = "bar" },
        }), Json);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("FLASH_INVALID", body);
    }
}
