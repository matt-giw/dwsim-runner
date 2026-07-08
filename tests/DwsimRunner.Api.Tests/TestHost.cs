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
        };
        foreach (var kv in overrides ?? []) settings[kv.Key] = kv.Value;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings)));
        Client = Factory.CreateClient();
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
