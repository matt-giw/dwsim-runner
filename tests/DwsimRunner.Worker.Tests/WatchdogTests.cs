// FND-0103 / FND-0104 (ISK-198) — the worker's own deadline.
//
// The defect was a SENTENCE: "the API process owns timeouts", in a file that cannot check it.
// These tests prove the worker now kills itself, with no caller timeout anywhere in the picture —
// `onExpiry` is the only seam, and it is a seam precisely because the production value is
// Environment.Exit, which a test cannot survive.
//
// TIER NOTE: this project is NOT built in CI (the Worker references DWSIM assemblies at compile
// time — see .github/workflows/ci.yml, which builds Api + FakeWorker only). It runs locally and
// in the Docker image build, where DWSIM is present.

using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class WatchdogTests
{
    [Fact]
    public async Task A_job_that_overruns_its_deadline_is_killed_by_the_worker_itself()
    {
        var exited = new TaskCompletionSource<int>();
        using var timer = Watchdog.Arm(TimeSpan.FromMilliseconds(50), code => exited.TrySetResult(code));

        // No caller timeout, no external supervisor — the only thing here is the worker.
        var settled = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(exited.Task, settled);
        Assert.Equal(Watchdog.DeadlineExitCode, await exited.Task);
    }

    [Fact]
    public void A_job_that_finishes_first_is_never_killed()
    {
        var fired = false;
        using (Watchdog.Arm(TimeSpan.FromSeconds(30), _ => fired = true))
        {
            // the "job" completes
        }
        Thread.Sleep(100);
        Assert.False(fired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    public void A_missing_or_malformed_deadline_falls_back_to_the_default_rather_than_off(string? configured)
    {
        // A bound that a bad value can switch OFF is the fail-open shape this ticket is about.
        Assert.Equal(Watchdog.DefaultDeadlineSeconds,
            Watchdog.DeadlineSeconds(_ => configured));
    }

    [Fact]
    public void The_deadline_is_configuration()
    {
        Assert.Equal(120, Watchdog.DeadlineSeconds(name => name == Watchdog.EnvVar ? "120" : null));
    }

    [Fact]
    public void The_default_deadline_cannot_preempt_a_request_the_api_considers_legal()
    {
        // The API clamps timeoutSeconds to 600 and /health advertises it as maxTimeoutSeconds.
        // A worker deadline at or below that would kill solves the platform explicitly allows —
        // this is a backstop, not a second policy.
        Assert.True(Watchdog.DefaultDeadlineSeconds > 600,
            $"default deadline {Watchdog.DefaultDeadlineSeconds}s must exceed the API's 600s ceiling");
    }
}
