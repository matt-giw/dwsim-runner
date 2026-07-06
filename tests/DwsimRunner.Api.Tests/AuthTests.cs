// T031 — FR-016: optional shared API key. When RUNNER_API_KEY is set, every
// route except GET /health requires X-Api-Key; unset = open (local dev).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class AuthTests
{
    private const string Key = "test-secret-key";

    private static RunnerHost KeyedHost() => new(new() { ["RUNNER_API_KEY"] = Key });

    [Theory]
    [InlineData("GET", "/templates")]
    [InlineData("GET", "/templates/t/objects")]
    [InlineData("POST", "/solve")]
    [InlineData("POST", "/compare")]
    public async Task Protected_routes_reject_missing_key_with_401(string method, string path)
    {
        using var host = KeyedHost();
        host.AddTemplate("t");

        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            req.Content = JsonContent.Create(new { templateId = "t" });
        var resp = await host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNAUTHORIZED", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrong_key_is_401_and_correct_key_is_accepted()
    {
        using var host = KeyedHost();
        host.AddTemplate("t");

        using var bad = new HttpRequestMessage(HttpMethod.Get, "/templates");
        bad.Headers.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(bad)).StatusCode);

        using var good = new HttpRequestMessage(HttpMethod.Get, "/templates");
        good.Headers.Add("X-Api-Key", Key);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(good)).StatusCode);
    }

    [Fact]
    public async Task Health_stays_open_for_probes()
    {
        using var host = KeyedHost();
        var resp = await host.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Unset_key_leaves_all_routes_open()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await host.Client.PostAsJsonAsync("/solve", new { templateId = "t" })).StatusCode);
    }
}
