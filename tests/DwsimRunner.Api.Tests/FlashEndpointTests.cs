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

    // ── iskra spec 227 (ISK-266) — a batch of states in ONE call ─────────────────────────────
    //
    // Measured on iskra's development (2026-09-06): a study turn made 113 flash calls, ten per
    // step, every one a worker spawn that loads DWSIM and builds a scratch flowsheet, 3.5 s each
    // under load. `states` carries N spec sets against one base (compounds, composition, package,
    // flashType); the worker sets up once and evaluates each state. One spawn for the step.
    private static Dictionary<string, object?> BatchRequest(params object[] states)
    {
        var r = BaseRequest("TP");
        r["states"] = states;
        return r;
    }

    private static object TpState(double tC, double pBar) => new { temperature = Spec(tC, "C"), pressure = Spec(pBar, "bar") };

    [Fact]
    public async Task Batch_of_states_returns_one_result_per_state_from_one_spawn()
    {
        using var host = new RunnerHost();
        var before = host.StartMarkers().Length;

        var resp = await host.Client.PostAsJsonAsync("/flash", BatchRequest(TpState(0, 10), TpState(25, 10), TpState(50, 10)));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var results = body.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal(0.83, results[0].GetProperty("vaporFraction").GetDouble(), 3);
        Assert.Equal(before + 1, host.StartMarkers().Length);   // ONE spawn for three states
    }

    [Fact]
    public async Task Batch_state_missing_its_spec_pair_is_400_without_a_spawn()
    {
        using var host = new RunnerHost();
        var before = host.StartMarkers().Length;
        var resp = await host.Client.PostAsJsonAsync("/flash", BatchRequest(TpState(0, 10), new { temperature = Spec(25, "C") }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("FLASH_INVALID", body.GetProperty("error").GetString());
        Assert.Contains("states[1]", body.GetProperty("message").GetString());
        Assert.Equal(before, host.StartMarkers().Length);
    }

    [Fact]
    public async Task Empty_batch_is_400_without_a_spawn()
    {
        using var host = new RunnerHost();
        var resp = await host.Client.PostAsJsonAsync("/flash", BatchRequest());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_state_the_engine_refuses_is_an_error_entry_and_the_others_still_answer()
    {
        using var host = new RunnerHost();
        // The FakeWorker treats temperature -9999 as an engine refusal for that state only.
        var resp = await host.Client.PostAsJsonAsync("/flash", BatchRequest(TpState(0, 10), TpState(-9999, 10)));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("results").EnumerateArray().ToList();
        Assert.True(results[0].TryGetProperty("vaporFraction", out _));
        Assert.Equal("FLASH_INVALID", results[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Identical_batches_are_cache_served_and_a_different_state_set_is_not()
    {
        using var host = new RunnerHost();
        var first = await host.Client.PostAsJsonAsync("/flash", BatchRequest(TpState(0, 10), TpState(25, 10)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var spawns = host.StartMarkers().Length;
        var second = await host.Client.PostAsJsonAsync("/flash", BatchRequest(TpState(0, 10), TpState(25, 10)));
        Assert.Equal(spawns, host.StartMarkers().Length);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        var third = await host.Client.PostAsJsonAsync("/flash", BatchRequest(TpState(0, 10), TpState(30, 10)));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(spawns + 1, host.StartMarkers().Length);
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

    // DENSITY on the wire (iskra spec 090 eval tier, finding F5).
    //
    // iskra asks the co-pilot to report a stream's density and then checks it against ~997 kg/m3 for
    // water at 25 C. Nothing on the wire carried a density, so the agent answered from MEMORY and the
    // check could only be recorded as unverifiable — a right number with no provenance, which is the
    // one failure mode the whole system exists to prevent.
    //
    // Nullable, and that is deliberate: a density the engine will not give must never fail a flash
    // that otherwise converged. The caller distinguishes "not reported" from "wrong".
    [Fact]
    public async Task Tp_flash_reports_density()
    {
        using var host = new RunnerHost();
        var res = await host.Client.PostAsJsonAsync("/flash", TpRequest());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.TryGetProperty("densityKgM3", out var d),
            "the flash response carries no densityKgM3 field");
        Assert.Equal(52.31, d.GetDouble(), 3);
    }

    // 120 US2 (T014) — the API precheck accepts the four new pairs and still rejects the
    // solids pairs. The precheck DUPLICATES the worker's switch by design (50 ms answers,
    // no spawn); during 120 the API vetoed the worker's new pairs for a whole debugging
    // session because only one side was extended — these pin both sides agreeing.
    [Theory]
    [InlineData("PVF", "pressure", "vaporFraction")]
    [InlineData("TVF", "temperature", "vaporFraction")]
    public async Task New_pairs_pass_the_precheck_and_reach_the_worker(string flashType, string a, string b)
    {
        using var host = new RunnerHost();
        var req = BaseRequest(flashType);
        req[a] = Spec(a == "vaporFraction" ? 0.5 : 25, a == "temperature" ? "C" : a == "pressure" ? "bar" : a == "enthalpy" ? "kJ/kg" : "");
        req[b] = Spec(b == "vaporFraction" ? 0.5 : 100, b == "temperature" ? "C" : b == "pressure" ? "bar" : b == "entropy" ? "kJ/[kg.K]" : b == "enthalpy" ? "kJ/kg" : "");

        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        // FakeWorker answers any accepted flash with the canned result — a 200 here proves
        // the precheck let the pair through; a 400 would be the API vetoing the worker again.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Tvf_on_a_single_compound_is_refused_before_the_worker()
    {
        using var host = new RunnerHost();
        var req = BaseRequest("TVF");
        req["compounds"] = new[] { "Water" };
        req["composition"] = new { basis = "molar", fractions = new Dictionary<string, double> { ["Water"] = 1 } };
        req["temperature"] = Spec(100, "C");
        req["vaporFraction"] = Spec(0.5, "");
        var spawnsBefore = host.StartMarkers().Length;

        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("FLASH_INVALID", body.GetProperty("error").GetString());
        Assert.Contains("PVF", body.GetProperty("message").GetString());
        Assert.Equal(spawnsBefore, host.StartMarkers().Length);
    }

    [Theory]
    [InlineData("PSF")]   // solids ledgered will-not-yet
    [InlineData("TSF")]
    [InlineData("TH")]    // measured engine crash 2026-08-01 — refused, not exposed
    [InlineData("TS")]
    public async Task Solid_fraction_pairs_stay_rejected_by_decision(string flashType)
    {
        using var host = new RunnerHost();
        var req = BaseRequest(flashType);
        req["pressure"] = Spec(1.01325, "bar");
        req["temperature"] = Spec(25, "C");

        var resp = await host.Client.PostAsJsonAsync("/flash", req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("FLASH_INVALID", await resp.Content.ReadAsStringAsync());
    }
}
