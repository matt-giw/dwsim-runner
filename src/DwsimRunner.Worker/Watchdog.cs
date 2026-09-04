// dwsim-runner Worker — GPL-3.0
// FND-0103 / FND-0104 — the worker bounds its OWN work.
//
// Program.cs's header used to say "The API process owns timeouts", and that sentence was the
// defect. It is not a fact this file can check: the API is a different process, the enforcement
// point is not verifiable from here, and `auto.CalculateFlowsheet2(fs)` takes no
// CancellationToken and honours no deadline. A solve that diverges runs until something outside
// kills it — and "something outside" is an assumption, not a bound.
//
// So the worker carries a deadline of its own that holds with no caller timeout present at all.
//
// WHY A HARD EXIT AND NOT A CANCELLATION. `CalculateFlowsheet2` is a synchronous call into
// engine code that cannot be interrupted: .NET Core has no Thread.Abort, and
// `Task.Run(...).WaitAsync(timeout)` returns to the caller while the engine thread keeps burning
// CPU — a bound on the ANSWER, not on the WORK, which is the wrong half for a resource-exhaustion
// primitive. Killing the process is the only thing that actually returns the CPU and the ~1-2 GB
// the worker holds. One job per process is already the contract (Program.cs), so there is nothing
// else in here to lose.
//
// The deadline covers the WHOLE JOB, not just the solve: build, connect, configure and harvest
// are all engine work in the same process, and Modes.BuildSolve and Solver.Run are two entry
// points that would otherwise each need their own copy — the duplication-drift this repo keeps
// getting bitten by (see the shared HarvestStream comment).

using System.Text.Json;

namespace DwsimRunner.Worker;

internal static class Watchdog
{
    /// <summary>Exit code for a self-imposed deadline. The API maps it to 504 SOLVE_TIMEOUT,
    /// the same taxonomy as its own process kill — which watchdog noticed is not the caller's
    /// problem. Distinct from 1 (crash) so the two are separable in the logs.</summary>
    internal const int DeadlineExitCode = 6;

    /// <summary>
    /// Default wall-clock ceiling, in seconds. 900 s is deliberately ABOVE the largest timeout a
    /// caller can ask the API for (600 s, capped in Program.cs and advertised by /health as
    /// `maxTimeoutSeconds`), so this can never preempt a request the platform considers legal —
    /// it is the backstop for when the API's own kill does not happen. Measured context: all 216
    /// corpus solves in spec 143 finished inside the deployed 60 s SOLVE_TIMEOUT_SECONDS.
    /// </summary>
    internal const int DefaultDeadlineSeconds = 900;

    internal const string EnvVar = "WORKER_DEADLINE_SECONDS";

    /// <summary>Reads the deadline from the environment. Absent, unparseable or non-positive all
    /// fall back to the default: a deadline that can be switched OFF by a malformed value is the
    /// fail-open shape this ticket is about.</summary>
    internal static int DeadlineSeconds(Func<string, string?> env) =>
        int.TryParse(env(EnvVar), out var s) && s > 0 ? s : DefaultDeadlineSeconds;

    /// <summary>
    /// Arms the deadline. Returns the timer; dispose it when the job completes normally.
    /// <paramref name="onExpiry"/> is the kill — <c>Environment.Exit</c> in production, a
    /// recorder in tests, which is the only reason this is a parameter.
    /// </summary>
    // ponytail: Environment.Exit is the kill. It runs ProcessExit handlers (there are none
    // registered here) but not finalizers on .NET Core, so it is prompt in practice. If a wedged
    // native thread is ever measured to block it, escalate to Process.GetCurrentProcess().Kill();
    // the API's own process kill remains as the outer layer either way.
    internal static IDisposable Arm(TimeSpan deadline, Action<int> onExpiry) =>
        new Timer(_ =>
        {
            WriteTimeoutDocument(deadline);
            onExpiry(DeadlineExitCode);
        }, null, deadline, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// The protocol document a killed job leaves behind, so the API reads a typed answer instead
    /// of an empty stdout it can only report as WORKER_CRASH. ProtocolChannel is first-writer-wins,
    /// so this is a no-op if the job finished in the same instant.
    /// </summary>
    internal static void WriteTimeoutDocument(TimeSpan deadline) =>
        ProtocolChannel.WriteResult(JsonSerializer.Serialize(new
        {
            error = "SOLVE_TIMEOUT",
            message = $"worker exceeded its own {deadline.TotalSeconds:0}s deadline and was terminated",
        }));
}
