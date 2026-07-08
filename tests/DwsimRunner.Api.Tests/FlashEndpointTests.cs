// T042 — POST /flash (US4, contracts/runner-api-v2.md "New: POST /flash").
// Served by the FakeWorker's flash mode (canned TP result; compound "__bad"
// → exit 2 FLASH_INVALID). The route validates the flashType/spec pairing
// in-process (no worker spawn) and caches results by request body.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class FlashEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Spec(double value, string unit) => new { value, unit };

    private static Dictionary<string, object?> BaseRequest(string flashType) => new()
    {
        ["compounds"] = new[] { "Methane", "Ethane" },
        ["composition"] = new
        {
            basis = "molar",
            fractions = new Dictionary<string, double> { ["Methane"] = 0.5, ["Ethane"] = 0.5 },
        },
        ["propertyPackage"] = "PR",
        ["flashType"] = flashType,
    };

    private static Dictionary<string, object?> TpRequest()
    {
        var r = BaseRequest("TP");
        r["temperature"] = Spec(0, "C");
        r["pressure"] = Spec(10, "bar");
        return r;
    }

    [Fact]
    public async Task Tp_flash_returns_the_flash_result_shape()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/flash", TpRequest());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0.83, body.GetProperty("vaporFraction").GetDouble(), 3);
        var phases = body.GetProperty("phases").EnumerateArray().ToList();
        Assert.Contains(phases, p => p.GetProperty("phase").GetString() == "Vapor");
        Assert.Contains(phases, p => p.GetProperty("phase").GetString() == "Liquid");
        var vapor = phases.First(p => p.GetProperty("phase").GetString() == "Vapor");
        Assert.True(vapor.GetProperty("composition").GetProperty("Methane").GetDouble() > 0);
    }

    [Fact]
    public async Task Ph_flash_with_pressure_and_enthalpy_is_accepted()
    {
        using var host = new RunnerHost();

        var req = BaseRequest("PH");
        req["pressure"] = Spec(10, "bar");
        req["enthalpy"] = Spec(-120.0, "kJ/kg");
        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Ps_flash_with_pressure_and_entropy_is_accepted()
    {
        using var host = new RunnerHost();

        var req = BaseRequest("PS");
        req["pressure"] = Spec(10, "bar");
        req["entropy"] = Spec(-1.0, "kJ/[kg.K]");
        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("TP", "pressure")]     // TP without temperature
    [InlineData("PH", "pressure")]     // PH without enthalpy
    [InlineData("PS", "entropy")]      // PS without pressure
    public async Task Mismatched_spec_pair_is_400_flash_invalid_without_a_worker_spawn(
        string flashType, string onlySpec)
    {
        using var host = new RunnerHost();
        var before = host.StartMarkers().Length;

        var req = BaseRequest(flashType);
        req[onlySpec] = Spec(10, onlySpec == "pressure" ? "bar" : "kJ/kg");
        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("FLASH_INVALID", body.GetProperty("error").GetString());
        Assert.Equal(before, host.StartMarkers().Length);   // rejected in-process
    }

    [Fact]
    public async Task Unknown_flash_type_is_400_flash_invalid()
    {
        using var host = new RunnerHost();

        var req = BaseRequest("TV");
        req["temperature"] = Spec(0, "C");
        req["pressure"] = Spec(10, "bar");
        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("FLASH_INVALID", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Missing_compounds_is_400_flash_invalid()
    {
        using var host = new RunnerHost();

        var req = TpRequest();
        req.Remove("compounds");
        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("FLASH_INVALID", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unparsable_body_is_400_invalid_request()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flash",
            new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("INVALID_REQUEST", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_compound_from_the_worker_maps_to_400_flash_invalid()
    {
        using var host = new RunnerHost();

        // "__bad" makes the FakeWorker exit 2 with a FLASH_INVALID body.
        var req = TpRequest();
        req["compounds"] = new[] { "__bad", "Ethane" };
        req["composition"] = new
        {
            basis = "molar",
            fractions = new Dictionary<string, double> { ["__bad"] = 0.5, ["Ethane"] = 0.5 },
        };
        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("FLASH_INVALID", body.GetProperty("error").GetString());
        Assert.Contains("__bad", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Identical_flash_requests_are_cache_served_without_a_second_spawn()
    {
        using var host = new RunnerHost();

        var first = await host.Client.PostAsJsonAsync("/flash", TpRequest());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var spawnsAfterFirst = host.StartMarkers().Length;

        var second = await host.Client.PostAsJsonAsync("/flash", TpRequest());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(spawnsAfterFirst, host.StartMarkers().Length);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        // A different spec is a different cache key → new spawn.
        var req = TpRequest();
        req["temperature"] = Spec(25, "C");
        var third = await host.Client.PostAsJsonAsync("/flash", req);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(spawnsAfterFirst + 1, host.StartMarkers().Length);
    }
}
