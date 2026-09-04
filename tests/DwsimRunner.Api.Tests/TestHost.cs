// Shared factory: boots the real API in-proc with a temp templates dir and
// WORKER_PATH pointed at the FakeWorker stub. Each call gets an isolated
// templates dir, which doubles as the FakeWorker's marker drop (it writes
// run-{id}.start/.end files next to the job's template).

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DwsimRunner.Api.Tests;

internal sealed class RunnerHost : IDisposable
{
    /// <summary>
    /// The key every host boots with. FND-0002/FND-0075: the runner now fails CLOSED, so an
    /// unkeyed host answers 503 on every route and there is no "open" configuration left for a
    /// suite to run against. Setting it here (and sending it by default below) is what keeps the
    /// rest of the tier testing routes rather than testing the gate — AuthTests opts OUT of both
    /// to prove the refusal.
    /// </summary>
    public const string DefaultApiKey = "test-runner-key";

    public WebApplicationFactory<Program> Factory { get; }
    public string TemplatesDir { get; }
    public string UserTemplatesDir { get; }
    public HttpClient Client { get; }

    public RunnerHost(Dictionary<string, string?>? overrides = null)
    {
        TemplatesDir = Directory.CreateTempSubdirectory("dwsim-api-tests-").FullName;
        UserTemplatesDir = Path.Combine(TemplatesDir, "user");

        var settings = new Dictionary<string, string?>
        {
            ["TEMPLATES_PATH"] = TemplatesDir,
            ["USER_TEMPLATES_PATH"] = UserTemplatesDir,
            ["WORKER_PATH"] = Path.Combine(AppContext.BaseDirectory, "FakeWorker.dll"),
            ["DWSIM_PATH"] = TemplatesDir,   // no DWSIM present → health reports dwsimFound:false
            ["SOLVE_TIMEOUT_SECONDS"] = "30",
            ["MAX_CONCURRENT_SOLVES"] = "4",
            ["RUNNER_API_KEY"] = DefaultApiKey,
        };
        foreach (var kv in overrides ?? []) settings[kv.Key] = kv.Value;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings)));
        Client = Factory.CreateClient();
        // Sent on every request unless a test builds its own HttpRequestMessage. Whatever key the
        // host actually booted with — so a host constructed with a different (or empty) key still
        // gets a client that matches it.
        if (settings["RUNNER_API_KEY"] is { Length: > 0 } key)
            Client.DefaultRequestHeaders.Add("X-Api-Key", key);
        try { Client.GetAsync("/catalog/compounds").GetAwaiter().GetResult(); } catch { /* best effort */ }
    }

    public string AddTemplate(string id)
    {
        var path = Path.Combine(TemplatesDir, id + ".dwxmz");
        File.WriteAllText(path, "not a real flowsheet — FakeWorker never reads it");
        return path;
    }

    public string[] StartMarkers() => Directory.GetFiles(TemplatesDir, "run-*.start");
    public string[] EndMarkers()   => Directory.GetFiles(TemplatesDir, "run-*.end");

    /// <summary>(start, end) UTC tick intervals of completed FakeWorker runs.</summary>
    public List<(long Start, long End)> RunIntervals() =>
        StartMarkers()
            .Select(s => (s, e: s[..^".start".Length] + ".end"))
            .Where(p => File.Exists(p.e))
            .Select(p => (long.Parse(File.ReadAllText(p.s)), long.Parse(File.ReadAllText(p.e))))
            .ToList();

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        try { Directory.Delete(TemplatesDir, recursive: true); } catch { /* best effort */ }
    }
}
