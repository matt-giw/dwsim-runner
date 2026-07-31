// T007 — 002 catalog endpoints: /catalog/compounds, /catalog/property-packages,
// /catalog/unit-op-types (FR-CAT-001..004). Served by the worker's `catalog`
// mode (FakeWorker cans the payload), cached per engine version, 503 when the
// engine is unavailable.

using System.Net;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class CatalogTests
{
    [Fact]
    public async Task Compounds_have_names_and_engine_version()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.GetAsync("/catalog/compounds");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal("9.0.5.0", body.GetProperty("engineVersion").GetString());
        var compounds = body.GetProperty("compounds").EnumerateArray().ToList();
        Assert.Contains(compounds, c => c.GetProperty("name").GetString() == "Methane");
        Assert.Contains(compounds, c => c.GetProperty("formula").GetString() == "CH4");
    }

    [Fact]
    public async Task Property_packages_have_id_and_name()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.GetAsync("/catalog/property-packages");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var packages = body.GetProperty("propertyPackages").EnumerateArray().ToList();
        var pr = packages.Single(p => p.GetProperty("id").GetString() == "PR");
        Assert.Contains("Peng-Robinson", pr.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unit_op_types_expose_ports_and_parameters()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.GetAsync("/catalog/unit-op-types");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var types = body.GetProperty("unitOpTypes").EnumerateArray().ToList();

        var separator = types.Single(t => t.GetProperty("type").GetString() == "separator");
        var portNames = separator.GetProperty("ports").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("Inlet", portNames);
        Assert.Contains("Vapor Outlet", portNames);
        Assert.Contains("Liquid Outlet", portNames);

        var heater = types.Single(t => t.GetProperty("type").GetString() == "heater");
        Assert.Contains(heater.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "outletTemperature");
    }

    [Fact]
    public async Task Catalog_is_cached_one_worker_spawn_for_repeated_requests()
    {
        using var host = new RunnerHost();

        await host.Client.GetAsync("/catalog/compounds");
        await host.Client.GetAsync("/catalog/compounds");
        await host.Client.GetAsync("/catalog/property-packages");   // same cached catalog document

        Assert.Single(host.StartMarkers());
    }

    [Fact]
    public async Task Engine_unavailable_returns_503()
    {
        using var host = new RunnerHost(new() { ["WORKER_PATH"] = "/nonexistent/worker.dll" });

        var resp = await host.Client.GetAsync("/catalog/compounds");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ENGINE_UNAVAILABLE", body.GetProperty("error").GetString());
    }

    // The unit vocabulary, published so a CLIENT can check its own copy against it.
    //
    // iskra's `RUNNER_UNITS` is this table transcribed into TypeScript, and nothing compared them:
    // its tests only asserted the table agreed with itself. App stricter drops a value before it
    // ships; runner stricter refuses it with INVALID_UNIT after it does.
    [Fact]
    public async Task Units_publishes_the_vocabulary_the_validator_enforces()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.GetAsync("/catalog/units");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var units = body.GetProperty("units");

        // Not a copy of the table — the SAME object the validator rejects with. Anything else here
        // would be a second opinion about the first, which is the defect this endpoint exists to end.
        foreach (var (quantity, spellings) in DocumentValidator.UnitVocabulary)
        {
            var published = units.GetProperty(quantity).EnumerateArray().Select(u => u.GetString()).ToList();
            Assert.Equal(spellings, published);
        }
        Assert.Equal(DocumentValidator.UnitVocabulary.Count, units.EnumerateObject().Count());

        // One spot check with real content, so a vocabulary that serialised as empty objects cannot
        // satisfy the loop above by vacuous agreement.
        Assert.Contains("bar", units.GetProperty("pressure").EnumerateArray().Select(u => u.GetString()));
    }
}
