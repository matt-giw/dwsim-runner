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

    public static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri(BaseUrl),
        Timeout = TimeSpan.FromSeconds(120),
    };

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
