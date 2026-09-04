// FND-0029 (ISK-198) — the aggregate work bound on /optimize and /compare.
//
// Bounding each evaluation says nothing about the total. `/optimize` runs golden section
// SEQUENTIALLY, so one request holds a solve slot for maxEvaluations x timeoutSeconds — 30 x 600 s
// is five hours, and four of them starve every other caller at MAX_CONCURRENT_SOLVES=4. The fix is
// an UP-FRONT refusal: the matrix is rejected before the first worker spawn, rather than accepted
// and then abandoned halfway.
//
// The tests are about the REFUSAL, not about elapsed time — a test that proves the bound by
// waiting for it is a test that takes as long as the bug.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class WorkBudgetTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object OptimizeRequest(int maxEvaluations, int timeoutSeconds) => new
    {
        templateId = "t",
        variable = new { @object = "__objective", property = "pressure", min = 0, max = 100, unit = "bar" },
        objective = new { @object = "W_comp", property = "duty", direction = "minimize" },
        tolerance = 0.5,
        maxEvaluations,
        timeoutSeconds,
    };

    [Fact]
    public async Task Optimize_refuses_the_matrix_that_pins_a_slot_for_hours()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        // The host's constructor warms the catalog, which itself spawns a FakeWorker — so the
        // claim is "no NEW spawn", measured against a baseline rather than against zero.
        var before = host.StartMarkers().Length;

        // The exact case from the finding: 30 evaluations x 600 s = 18,000 s on one slot.
        var resp = await host.Client.PostAsJsonAsync("/optimize", OptimizeRequest(30, 600));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("WORK_BUDGET_EXCEEDED", body.GetProperty("error").GetString());

        // Refused UP FRONT: not one worker was spawned for it.
        Assert.Equal(before, host.StartMarkers().Length);
    }

    [Fact]
    public async Task Optimize_at_the_default_per_case_timeout_is_untouched_at_any_legal_count()
    {
        // Regression guard on the calibration: the deployed SOLVE_TIMEOUT_SECONDS is 60 s and
        // every one of spec 143's 216 corpus solves finished inside it, so a caller who does not
        // override timeoutSeconds must never meet this bound — 30 x 60 = 1800 s < 3600 s.
        using var host = new RunnerHost(new() { ["SOLVE_TIMEOUT_SECONDS"] = "60" });
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/optimize", new
        {
            templateId = "t",
            variable = new { @object = "__objective", property = "pressure", min = 0, max = 100, unit = "bar" },
            objective = new { @object = "W_comp", property = "duty", direction = "minimize" },
            tolerance = 0.5,
            maxEvaluations = 30,
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Compare_refuses_an_over_budget_case_set_too()
    {
        // /compare is the sibling that expands one request into many solves. It fans out
        // concurrently rather than sequentially, but 25 x 600 s is the same 4 hours of worker
        // time held — so the same budget applies, from the same helper.
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var before = host.StartMarkers().Length;

        var cases = Enumerable.Range(0, 25)
            .ToDictionary(i => $"c{i}", _ => new object[] { new { @object = "F1", property = "pressure", value = 1.0 } });

        var resp = await host.Client.PostAsJsonAsync("/compare", new { templateId = "t", cases, timeoutSeconds = 600 });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("WORK_BUDGET_EXCEEDED", body.GetProperty("error").GetString());
        Assert.Equal(before, host.StartMarkers().Length);
    }

    [Fact]
    public async Task The_budget_is_configuration_not_a_magic_number()
    {
        using var host = new RunnerHost(new() { ["MAX_REQUEST_WORK_SECONDS"] = "60000" });
        host.AddTemplate("t");

        // Raised above 30 x 600 = 18,000: the same request the default refuses now runs.
        var resp = await host.Client.PostAsJsonAsync("/optimize", OptimizeRequest(30, 600));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
