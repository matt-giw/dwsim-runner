// ISK-541 — a solve stops when its caller hangs up.
//
// Measured on a running runner before this (2026-10-04): the API noticed the hang-up (logged 499)
// and abandoned the request, but the worker ran on — to its natural end when its result fit the
// stdout pipe, and forever when it did not, because nothing drained the pipe any more. The
// concurrency slot was released while the process was still computing, so every abandoned solve
// ran OUTSIDE MAX_CONCURRENT_SOLVES. The cause was one `when (!ct.IsCancellationRequested)` on the
// kill: a caller's cancellation skipped it.
//
// The FakeWorker drops run-{id}.pid beside its markers so these tests can ask the operating system
// whether the process is still there, rather than inferring it from a marker that never appears.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class CallerAbortTests
{
    private const string SleepDoc = """
    {
      "schemaVersion": 1,
      "name": "caller abort",
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 25, "unit": "C" },
                    "pressure": { "value": 50, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar", "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
        { "tag": "__sleep:30", "kind": "unitOp", "type": "separator" },
        { "tag": "VAP", "kind": "materialStream" },
        { "tag": "LIQ", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "__sleep:30", "port": "Inlet" },
        { "from": "__sleep:30", "to": "VAP", "port": "Vapor Outlet" },
        { "from": "__sleep:30", "to": "LIQ", "port": "Liquid Outlet" }
      ]
    }
    """;

    private static object Sleep(int seconds) => new { @object = $"__sleep:{seconds}", property = "x", value = 0.0 };

    private static string[] PidFiles(RunnerHost host) => Directory.GetFiles(host.TemplatesDir, "run-*.pid");

    /// <summary>The pid of the first worker started after <paramref name="before"/> was taken.</summary>
    private static async Task<int> NewWorkerPid(RunnerHost host, string[] before)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var fresh = PidFiles(host).Except(before).FirstOrDefault();
            if (fresh is not null && int.TryParse(File.ReadAllText(fresh), out var pid)) return pid;
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("the request never reached the worker");
    }

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task<bool> GoneWithin(int pid, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (!Alive(pid)) return true;
            await Task.Delay(25);
        }
        return !Alive(pid);
    }

    [Fact]
    public async Task Aborting_a_template_solve_kills_its_worker()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");
        var before = PidFiles(host);
        using var caller = new CancellationTokenSource();

        var request = host.Client.PostAsJsonAsync("/solve",
            new { templateId = "t", overrides = new[] { Sleep(30) } }, caller.Token);
        var pid = await NewWorkerPid(host, before);
        Assert.True(Alive(pid), "the worker should be running before the caller hangs up");

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.True(await GoneWithin(pid, TimeSpan.FromSeconds(2)),
            "the worker was still running 2 s after its caller hung up");
    }

    [Fact]
    public async Task Aborting_a_document_solve_kills_its_worker_and_frees_the_slot()
    {
        using var host = new RunnerHost(new() { ["MAX_CONCURRENT_SOLVES"] = "1" });
        host.AddTemplate("t");
        var before = PidFiles(host);
        using var caller = new CancellationTokenSource();

        var request = host.Client.PostAsJsonAsync("/flowsheets/build-solve",
            new { document = JsonSerializer.Deserialize<JsonElement>(SleepDoc), timeoutSeconds = 60 }, caller.Token);
        var pid = await NewWorkerPid(host, before);

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        // The only slot must come back — and by the time anyone else holds it, the aborted worker
        // must be gone: one slot, one process.
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var next = await host.Client.PostAsJsonAsync("/solve",
            new { templateId = "t", overrides = Array.Empty<object>() }, patience.Token);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.False(Alive(pid), "a second solve ran while the aborted worker was still alive");
    }

    [Fact]
    public async Task A_timeout_is_still_a_504()
    {
        // The kill now serves two causes; the caller-facing answer for the old one must not move.
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/solve",
            new { templateId = "t", overrides = new[] { Sleep(30) }, timeoutSeconds = 5 });

        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SOLVE_TIMEOUT", body.GetProperty("error").GetString());
    }
}
