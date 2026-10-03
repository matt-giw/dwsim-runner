// Tier B gate: these tests exercise a RUNNING dwsim-runner (with a real DWSIM
// install) over HTTP — start it with `docker compose up -d --build`. Target is
// SIM_RUNNER_URL (default http://localhost:8080). Every test calls
// Skip.IfNot(RunnerConnection.Available, …) so the suite self-skips on
// machines/CI without the service, per research.md R7.

using System.Text.Json;

namespace DwsimRunner.Integration.Tests;

public static class RunnerConnection
{
    public static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("SIM_RUNNER_URL") ?? "http://localhost:8080";

    /// <summary>
    /// The shared key, sent on every request. Found while adding the ISKS-176 Keq cases: the runner
    /// fails CLOSED, so an image built from this revision answers `503 AUTH_NOT_CONFIGURED` with no
    /// key set and `401` with one set and no header — and this client sent no header either way. The
    /// whole Tier B suite was therefore unrunnable against its own image, which reads as "integration
    /// tests pass" only because they `Skip` when `/health` is unreachable and FAIL fast when it is.
    ///
    /// `/health` stays open, so `Available` could not see it: the probe succeeded while every real
    /// route was refused. Matching the compose default keeps `docker compose up -d` working with no
    /// extra environment.
    /// </summary>
    public static readonly string ApiKey =
        Environment.GetEnvironmentVariable("RUNNER_API_KEY") ?? "local-dev-key";

    public static readonly HttpClient Client = NewClient(TimeSpan.FromSeconds(120));

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = timeout };
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    private static readonly Lazy<bool> _available = new(() =>
    {
        try
        {
            using var probe = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(3) };
            var health = JsonSerializer.Deserialize<JsonElement>(
                probe.GetStringAsync("/health").GetAwaiter().GetResult());
            return health.GetProperty("ok").GetBoolean()
                && health.GetProperty("dwsimFound").GetBoolean();
        }
        catch { return false; }
    });

    public static bool Available => _available.Value;
    public const string SkipReason = "dwsim-runner not reachable with a DWSIM install (set SIM_RUNNER_URL / docker compose up)";
}
