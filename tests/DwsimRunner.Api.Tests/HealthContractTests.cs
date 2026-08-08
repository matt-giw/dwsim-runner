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

    // 147 US2 (T022, FR-006) — a running engine must say WHICH BUILD it is.
    //
    // `dwsimVersion` is the DWSIM LIBRARY version. Measured 2026-08-08: the engine deployed to
    // iskra's development environment (df13a91, built 2026-07-30) and the one the repository
    // pins today (8e53e1f) return byte-identical /health payloads while DISAGREEING about which
    // flash types they accept. A consumer could not tell them apart, so a vocabulary mismatch
    // could not name which side was behind.
    [Fact]
    public async Task Health_reports_a_build_identity_distinct_from_the_library_version()
    {
        var dwsimDir = MakeFixtureDwsimDir();
        using var host = new RunnerHost(new()
        {
            ["DWSIM_PATH"] = dwsimDir,
            ["BUILD_REF"] = "abc1234",
        });

        var h = await host.Client.GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("abc1234", h.GetProperty("buildRef").GetString());
        Assert.NotEqual(h.GetProperty("dwsimVersion").GetString(), h.GetProperty("buildRef").GetString());
    }

    // Unset must be an EXPLICIT "unknown", never an absent field. An absent field and a stale one
    // are indistinguishable to a consumer; an explicit "unknown" is not, and it is the difference
    // between "drift of unknown size" and "no drift" — 055's unset-is-not-a-gate, applied to a
    // version handshake.
    [Fact]
    public async Task Health_reports_buildRef_unknown_when_the_image_was_built_without_it()
    {
        using var host = new RunnerHost(new() { ["DWSIM_PATH"] = MakeFixtureDwsimDir() });

        var h = await host.Client.GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("unknown", h.GetProperty("buildRef").GetString());
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
