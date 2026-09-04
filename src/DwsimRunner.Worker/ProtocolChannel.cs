// dwsim-runner Worker — GPL-3.0
using System.Runtime.InteropServices;

namespace DwsimRunner.Worker;

/// <summary>
/// The worker's contract with the API is "exactly one JSON document on stdout".
/// <c>Console.SetOut(Console.Error)</c> was written to hold that line, and it does
/// — for MANAGED writes. It cannot hold it for native ones.
///
/// <para>
/// <c>Console.Out</c> is a .NET <see cref="TextWriter"/>; redirecting it changes
/// where <em>this runtime</em> writes. A native library linked into the process
/// writes to <b>file descriptor 1</b> directly and never consults it. Ipopt does
/// exactly that, printing its EPL banner the first time it is loaded:
/// </para>
/// <code>
/// ******************************************************************************
/// This program contains Ipopt, a library for large-scale nonlinear optimization.
/// ...
/// ******************************************************************************
/// {"converged":true,"elapsedMs":2982,"streams":[...
/// </code>
/// <para>
/// The solve had SUCCEEDED. The API could not parse the reply, reported
/// <c>WORKER_CRASH: worker returned an invalid response</c>, and 13 of the eval
/// corpus's NRTL cases scored 0 for a banner. Measured 2026-08-08.
/// </para>
/// <para>
/// So the redirect has to happen at the layer the noise is emitted from: dup the
/// real stdout aside, point fd 1 at stderr, and write the one JSON document to the
/// saved descriptor as the final act. Anything any library writes to fd 1 in
/// between — banner, solver progress, a future dependency nobody has met yet —
/// lands on stderr, where the API already treats it as diagnostics.
/// </para>
/// </summary>
internal static class ProtocolChannel
{
    [DllImport("libc", SetLastError = true)] private static extern int dup(int oldfd);
    [DllImport("libc", SetLastError = true)] private static extern int dup2(int oldfd, int newfd);

    private const int StdOut = 1;
    private const int StdErr = 2;

    private static int _saved = -1;

    // FIRST WRITER WINS. Since the watchdog (Watchdog.cs) writes the timeout document from a
    // TIMER THREAD while the main thread may be mid-solve and about to write its own, two
    // documents could reach fd 1 — which is exactly the "exactly one JSON document on stdout"
    // violation this whole class exists to prevent, arriving through its own remedy.
    private static int _written;

    /// <summary>
    /// Moves fd 1 out of the way. Call before any DWSIM-typed or native code runs.
    /// A platform without libc (or a dup that fails) leaves the descriptors alone —
    /// the managed redirect in Program.cs still applies, so behaviour degrades to
    /// exactly what it was before this file existed rather than to a dead worker.
    /// </summary>
    internal static void Divert()
    {
        try
        {
            var saved = dup(StdOut);
            if (saved < 0) return;
            if (dup2(StdErr, StdOut) < 0) return;
            _saved = saved;
        }
        catch (DllNotFoundException) { /* not Linux — nothing to divert */ }
        catch (EntryPointNotFoundException) { /* ditto */ }
    }

    /// <summary>
    /// Writes the single protocol document to the real stdout, whether or not
    /// <see cref="Divert"/> managed to take it aside.
    /// </summary>
    internal static void WriteResult(string json)
    {
        if (Interlocked.Exchange(ref _written, 1) != 0) return;

        if (_saved < 0)
        {
            // Divert() did not run or did not take. Program.cs has already restored
            // the managed writer, so this is the original path.
            Console.Out.Write(json);
            Console.Out.Write('\n');
            Console.Out.Flush();
            return;
        }

        using var real = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)_saved, ownsHandle: true),
                                        FileAccess.Write);
        using var writer = new StreamWriter(real);
        writer.Write(json);
        writer.Write('\n');
        writer.Flush();
        _saved = -1;
    }
}
