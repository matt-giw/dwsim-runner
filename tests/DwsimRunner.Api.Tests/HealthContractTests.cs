// T027 — US4 /health contract (FR-007): readiness + engine version + template
// list in one call; actionable hint when not ready. The "DWSIM install" fixture
// is a copy of FakeWorker.dll renamed DWSIM.Automation.dll — PEReader reads its
// (1.0.x) assembly version, which is outside the supported range.

using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class HealthContractTests
{
    private static string MakeFixtureDwsimDir()
    {
        var dir = Directory.CreateTempSubdirectory("dwsim-fixture-").FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "FakeWorker.dll"),
                  Path.Combine(dir, "DWSIM.Automation.dll"));
        return dir;
    }

    [Fact]
    public async Task Health_reports_version_fields_and_templates_when_engine_found()
    {
        var dwsimDir = MakeFixtureDwsimDir();
        using var host = new RunnerHost(new() { ["DWSIM_PATH"] = dwsimDir });
        host.AddTemplate("methanol_synthesis");

        var h = await host.Client.GetFromJsonAsync<JsonElement>("/health");

        Assert.True(h.GetProperty("ok").GetBoolean());
        Assert.True(h.GetProperty("dwsimFound").GetBoolean());
        Assert.False(string.IsNullOrEmpty(h.GetProperty("dwsimVersion").GetString()));
        Assert.Equal(">=9.0 <10", h.GetProperty("supportedRange").GetString());
        Assert.False(h.GetProperty("versionSupported").GetBoolean()); // fixture is 1.0.x
        Assert.Contains("methanol_synthesis",
            h.GetProperty("templates").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Health_not_ready_reports_ok_false_with_actionable_hint()
    {
        using var host = new RunnerHost(); // DWSIM_PATH points at an empty dir
        var h = await host.Client.GetFromJsonAsync<JsonElement>("/health");

        Assert.False(h.GetProperty("ok").GetBoolean());
        Assert.False(h.GetProperty("dwsimFound").GetBoolean());
        var hint = h.GetProperty("hint").GetString();
        Assert.False(string.IsNullOrEmpty(hint));
        Assert.Contains("DWSIM_PATH", hint);
    }

    [Fact]
    public async Task Templates_endpoint_returns_empty_list_for_missing_dir()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "nonexistent-templates-dir-" + Guid.NewGuid());
        using var host = new RunnerHost(new()
        {
            ["TEMPLATES_PATH"] = missingDir,
            ["USER_TEMPLATES_PATH"] = Path.Combine(missingDir, "user"),
        });
        var entries = await host.Client.GetFromJsonAsync<JsonElement>("/templates");
        Assert.Empty(entries.EnumerateArray());
    }

    [Fact]
    public async Task Solve_appends_warning_when_engine_version_unsupported()
    {
        var dwsimDir = MakeFixtureDwsimDir();
        using var host = new RunnerHost(new() { ["DWSIM_PATH"] = dwsimDir });
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/solve", new { templateId = "t" });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(body.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.Contains("outside supported range"));
    }
}
