// dwsim-runner Worker — GPL-3.0
// Spec 099 FR-001/FR-004 (implementing spec 034 FR-020/021) — `GET /catalog/engine-inventory`.
//
// WHY THIS EXISTS: every ledger in iskra compares the app to the runner's hand-written 21-entry
// allowlist. Nothing compared either side to what DWSIM itself declares — so the claim "DWSIM has no
// electrolyzer unit op" had nothing on the other side of it and stood for a year, while
// `WaterElectrolyzer` shipped in the DLL already vendored here. This endpoint is that other side.
//
// The Constitution VII assertion lives HERE rather than app-side on purpose: both capture scripts
// abort the entire capture when a DWSIM string appears, so app-side a leak surfaces as an opaque
// `exit 1` in the wrong repository.

using System.Net;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "EngineInventory")]
public class EngineInventoryTests
{
    private static async Task<JsonElement> Inventory()
    {
        var resp = await RunnerConnection.Client.GetAsync("/catalog/engine-inventory");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static List<JsonElement> Entries(JsonElement root) =>
        root.GetProperty("engineInventory").EnumerateArray().ToList();

    private static bool Instantiable(JsonElement e) => e.GetProperty("instantiable").GetBoolean();
    private static string? ExposedAs(JsonElement e) =>
        e.TryGetProperty("exposedAs", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;

    [SkippableFact]
    public async Task Endpoint_answers_with_the_engine_version_and_a_populated_inventory()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var root = await Inventory();
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("engineVersion").GetString()));
        // 49 was the planning estimate; measured 50 on 9.0.5.0. The bound is deliberately loose and
        // one-sided — this asserts the inventory is REAL, not that the engine never grows.
        Assert.True(Entries(root).Count >= 49, $"only {Entries(root).Count} entries");
    }

    /// The inventory must agree with the allowlist it is measured against, in both directions.
    [SkippableFact]
    public async Task Every_exposed_type_resolves_in_the_unit_op_catalog()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.GetAsync("/catalog/unit-op-types");
        var catalog = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var wireTypes = catalog.GetProperty("unitOpTypes").EnumerateArray()
            .Select(t => t.GetProperty("type").GetString()!).ToHashSet(StringComparer.Ordinal);

        var exposed = Entries(await Inventory()).Select(ExposedAs).Where(x => x is not null).ToList();

        Assert.NotEmpty(exposed);
        foreach (var wire in exposed)
            Assert.Contains(wire!, wireTypes);
        // Every allowlist entry is reachable from the inventory. `exposedAs` is a reverse lookup over
        // the allowlist computed at response time, so a shortfall here means an entry's ObjectType is
        // not an engine type at all — which is the drift this endpoint exists to make impossible.
        Assert.Equal(wireTypes.Count, exposed.Distinct(StringComparer.Ordinal).Count());
    }

    /// The assertion that would have caught `ElectrolyzerStack`, stated as a test.
    [SkippableFact]
    public async Task The_engine_declares_a_water_electrolyzer()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var electrolyzer = Entries(await Inventory())
            .Where(e => e.GetProperty("name").GetString()!.Replace(" ", "")
                         .Equals("WaterElectrolyzer", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(electrolyzer);   // exactly one row — see the dedup test below
        Assert.True(Instantiable(electrolyzer[0]),
            "the engine declares a water electrolyzer and cannot construct one — that IS news, " +
            "but it contradicts the measurement this endpoint was built on");
    }

    /// `instantiable` must mean "the engine built one", not "a list mentioned it".
    [SkippableFact]
    public async Task Nothing_is_exposed_that_the_engine_cannot_construct()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // The first implementation read `GetAvailableFlowsheetObjectTypeNames()` and reported
        // `separator` and `mixer` as NOT instantiable while carrying an `exposedAs` — the runner
        // builds and solves both, in tests that pass. That list is a GUI palette. This assertion is
        // what makes the difference detectable: an exposed type the engine cannot construct is a
        // contradiction, and it was the tell.
        var contradictions = Entries(await Inventory())
            .Where(e => ExposedAs(e) is not null && !Instantiable(e))
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        Assert.Empty(contradictions);
    }

    /// One unit op, one row.
    [SkippableFact]
    public async Task No_unit_op_appears_twice_under_two_spellings()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        // DWSIM names the same op three ways: `ObjectType.WaterElectrolyzer`, the factory list's
        // "Water Electrolyzer", and the external registry's punctuated "Gibbs Reactor (Reaktoro)".
        // Before normalisation the electrolyzer appeared TWICE — once as an enum member reported not
        // instantiable, once as an external reported instantiable. Two rows for one thing,
        // disagreeing, in the artifact whose whole job is to say what exists.
        var keys = Entries(await Inventory())
            .Select(e => new string(e.GetProperty("name").GetString()!
                .Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .ToList();

        var dupes = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    /// The external registry was CONSULTED, and "none found" must be distinguishable from "never looked".
    [SkippableFact]
    public async Task External_unit_operations_are_reported_as_external()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var sources = Entries(await Inventory())
            .Select(e => e.GetProperty("source").GetString()).Distinct().ToList();

        Assert.Contains("enum", sources);
        // 099's own contract called this "the always-empty external set". It is not empty — this build
        // registers a Reaktoro Gibbs reactor and an OPEM PEM fuel cell, and after de-duplication those
        // are the two that are external-only. If a future engine registers none, this assertion is the
        // thing that should be revisited deliberately rather than the claim quietly becoming true.
        Assert.Contains("external", sources);
    }

    /// Constitution VII — no engine identity crosses the hop.
    [SkippableFact]
    public async Task The_payload_names_no_vendor()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.GetAsync("/catalog/engine-inventory");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("dwsim", body, StringComparison.OrdinalIgnoreCase);
    }
}
