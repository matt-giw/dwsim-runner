// T031 — FR-016: shared API key. Every route except GET /health requires X-Api-Key.
//
// FND-0002 / FND-0075 (ISK-198). This file used to end with a test called
// `Unset_key_leaves_all_routes_open`, and that name was the vulnerability written down as a
// guarantee: the middleware was registered only inside `if (RUNNER_API_KEY is { Length: > 0 })`,
// so a deployment that lost its variable — or any compose/on-prem path, all of which default the
// value to "" — served /solve, /flowsheets/build-solve, DELETE /templates/{id} and
// GET /templates/{id}/file to anyone who could reach port 8080.
//
// The rule now: UNSET IS A REFUSAL. There is no configuration in which a route below is open.
// This is the same rule iskra-app's `checkApiAuth` already enforced (spec 032, "unset = open is
// not a gate"), so the two services finally agree.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class AuthTests
{
    private const string Key = RunnerHost.DefaultApiKey;

    /// <summary>A client with NO default X-Api-Key header — RunnerHost.Client carries one so the
    /// rest of the tier can exercise routes rather than the gate.</summary>
    private static HttpClient Anonymous(RunnerHost host) => host.Factory.CreateClient();

    [Theory]
    [InlineData("GET", "/templates")]
    [InlineData("GET", "/templates/t/objects")]
    [InlineData("GET", "/templates/t/file")]
    [InlineData("GET", "/catalog/compounds")]
    [InlineData("POST", "/solve")]
    [InlineData("POST", "/compare")]
    [InlineData("POST", "/flowsheets/build-solve")]
    [InlineData("DELETE", "/templates/t")]
    public async Task Protected_routes_reject_missing_key_with_401(string method, string path)
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");
        using var client = Anonymous(host);

        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") req.Content = JsonContent.Create(new { templateId = "t" });
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrong_key_is_401_and_correct_key_is_accepted()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");
        using var client = Anonymous(host);

        using var bad = new HttpRequestMessage(HttpMethod.Get, "/templates");
        bad.Headers.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bad)).StatusCode);

        using var good = new HttpRequestMessage(HttpMethod.Get, "/templates");
        good.Headers.Add("X-Api-Key", Key);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(good)).StatusCode);
    }

    [Fact]
    public async Task Health_stays_open_for_probes()
    {
        using var host = new RunnerHost();
        using var client = Anonymous(host);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    // ── fail closed (FND-0002 / FND-0075) ──────────────────────────────────────
    // Two spellings of "no key", because they arrive by different routes: the variable absent
    // from the environment, and `RUNNER_API_KEY: "${SIM_RUNNER_API_KEY:-}"` interpolating to the
    // empty string in compose. The pre-fix code treated both as "run open".

    public static TheoryData<string?> NoKey() => new() { null, "" };

    [Theory]
    [MemberData(nameof(NoKey))]
    public async Task Unset_key_refuses_every_mutating_route(string? configured)
    {
        using var host = new RunnerHost(new() { ["RUNNER_API_KEY"] = configured });
        host.AddTemplate("t");
        using var client = Anonymous(host);

        foreach (var (method, path) in new (string, string)[]
                 {
                     ("POST", "/solve"),
                     ("POST", "/flowsheets/build-solve"),
                     ("POST", "/optimize"),
                     ("DELETE", "/templates/t"),
                     ("GET", "/templates/t/file"),
                     ("GET", "/templates"),
                 })
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") req.Content = JsonContent.Create(new { templateId = "t" });
            var resp = await client.SendAsync(req);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("AUTH_NOT_CONFIGURED", body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Unset_key_cannot_be_bypassed_by_presenting_one()
    {
        // The refusal is a fact about the SERVER, not about the credential — an empty configured
        // key must not be matchable by an empty (or any) presented one.
        using var host = new RunnerHost(new() { ["RUNNER_API_KEY"] = "" });
        using var client = Anonymous(host);

        foreach (var presented in new[] { "", " ", "anything" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/templates");
            if (presented.Length > 0) req.Headers.Add("X-Api-Key", presented);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(req)).StatusCode);
        }
    }

    [Fact]
    public async Task Unset_key_still_answers_health_so_the_misconfiguration_is_diagnosable()
    {
        // The one exemption, and it is load-bearing: a container whose healthcheck fails never
        // goes live, which turns a missing variable into an unrecoverable deploy instead of a
        // legible one.
        using var host = new RunnerHost(new() { ["RUNNER_API_KEY"] = null });
        using var client = Anonymous(host);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
