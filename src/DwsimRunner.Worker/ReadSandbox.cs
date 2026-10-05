// dwsim-runner Worker — GPL-3.0
// iskra 285, #31 re-review residuals — the READ worker's least-privilege sandbox.
//
// The sanitiser (Sanitizer.cs) is the primary control on an uploaded file. This is the second: the
// read worker applies it to ITSELF, before any engine code runs and before the upload is opened, so
// that whatever code the engine runs while loading a customer's file has access to nothing except
// the engine's own files and this job's directory, and no network. Unprivileged Linux only:
//
//   1. prctl(PR_SET_NO_NEW_PRIVS)  — required by 2 and 3; nothing exec'd later can gain privilege.
//   2. Landlock                    — filesystem: read-only on the engine install, the .NET runtime,
//                                     the worker's own binaries and the system libraries under /usr;
//                                     read-write on the per-job directory (TMPDIR) only. Everything
//                                     else is not openable: /proc (other processes, the API's
//                                     environment), the template store, other jobs' directories, /tmp,
//                                     /home, /app/api. Landlock also refuses ptrace-style access (which
//                                     is what /proc/<pid>/environ and mem check) to any process outside
//                                     the sandbox.
//   3. seccomp                     — no socket of any family can be created (no network, and no
//                                     connection to the API's local .NET diagnostics socket); no io_uring.
//
// Landlock and seccomp apply to the CALLING THREAD only, and by the time Main runs the .NET runtime
// already has other threads (GC, finalizer, tiered JIT). So the sandbox is applied on the main thread
// and the worker then RE-EXECS itself (execve): the new image starts single-threaded inside the
// sandbox, and every thread it creates inherits it. The re-exec'd worker is marked by an environment
// variable, but the marker is not trusted: before doing anything it checks that the sandbox is in
// force (Verify) and refuses otherwise.
//
// FAIL CLOSED: if any step is unavailable (old kernel, a container runtime without Landlock, another
// CPU architecture) the worker answers SANDBOX_UNAVAILABLE (exit 7 → HTTP 503) and never opens the
// upload. There is no switch that turns the sandbox off for `read`.
//
// Only `read`, and the `sandbox-probe` mode /health and the conformance tests use, pass through here;
// every other mode is untouched.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DwsimRunner.Worker;

internal static class ReadSandbox
{
    internal const int ExitUnavailable = 7;

    /// <summary>Test-only: forces the "unavailable" path. It can only make the worker REFUSE; the API's
    /// worker-environment allow-list never passes it, so production cannot set it.</summary>
    internal const string ForceUnavailableVariable = "DWSIM_READ_SANDBOX_TEST_UNAVAILABLE";

    private const string SandboxedVariable = "DWSIM_READ_SANDBOXED";

    private sealed class Unavailable(string message) : Exception(message);

    /// <summary>
    /// The worker's FIRST act. For `read` (and a sandboxed `sandbox-probe`) it applies the sandbox and
    /// re-execs, so on success it does not return in the first process. Returns null to let the job
    /// run (any other mode, or the re-exec'd and verified process), or exit code 7 after writing the
    /// SANDBOX_UNAVAILABLE document to stdout.
    /// </summary>
    internal static int? EnterIfRequired(string jobFile)
    {
        if (!Required(jobFile)) return null;
        try
        {
            if (Sandboxed) { Verify(); return null; }
            ApplyAndReexec(jobFile);
            throw new Unavailable("execve returned");   // unreachable on success
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine(new JsonObject
            {
                ["error"] = "SANDBOX_UNAVAILABLE",
                ["message"] = $"the read sandbox could not be applied on this host, so the file was not opened: {ex.Message}",
                ["landlockAbi"] = LandlockAbi(),
                ["seccomp"] = SeccompSupported(),
                ["enforced"] = false,
            }.ToJsonString());
            return ExitUnavailable;
        }
    }

    private static bool Sandboxed => Environment.GetEnvironmentVariable(SandboxedVariable) == "1";

    private static bool Required(string jobFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jobFile));
            var root = doc.RootElement;
            var mode = root.TryGetProperty("mode", out var m) ? m.GetString()?.ToLowerInvariant() : null;
            return mode switch
            {
                "read" => true,
                "sandbox-probe" => !(root.TryGetProperty("sandbox", out var s) && s.ValueKind == JsonValueKind.False),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;   // not a job this file can parse; the ordinary path fails it as before
        }
    }

    private static void ApplyAndReexec(string jobFile)
    {
        if (!OperatingSystem.IsLinux()) throw new Unavailable("not Linux");
        if (Environment.GetEnvironmentVariable(ForceUnavailableVariable) == "1") throw new Unavailable("forced by the test switch");
        var arch = Arch.Current ?? throw new Unavailable($"no seccomp filter for {RuntimeInformation.ProcessArchitecture}");

        // The job directory is TMPDIR, set per job by the API. The job file must be inside it: after
        // the re-exec nothing else is readable.
        var tmp = Environment.GetEnvironmentVariable("TMPDIR");
        var jobDir = string.IsNullOrEmpty(tmp) ? "" : Path.GetFullPath(tmp).TrimEnd('/');
        if (jobDir.Length == 0 || !Directory.Exists(jobDir))
            throw new Unavailable("TMPDIR (the per-job directory) is not set or does not exist");
        if (!Path.GetFullPath(jobFile).StartsWith(jobDir + "/", StringComparison.Ordinal))
            throw new Unavailable("the job file is not inside the per-job directory");

        var abi = LandlockAbi() ?? throw new Unavailable("Landlock is not available (landlock_create_ruleset failed)");

        // DWSIM.Logging.Logger writes to $HOME/Documents/DWSIM Application Data when $HOME/Documents
        // exists, and to "<cwd>/DWSIM Application Data" otherwise (Environment.GetFolderPath(Personal)
        // is "" when that folder is missing). HOME is the job directory after the re-exec.
        Directory.CreateDirectory(Path.Combine(jobDir, "Documents"));

        if (Native.prctl(Native.PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0)
            throw new Unavailable($"PR_SET_NO_NEW_PRIVS failed: errno {Marshal.GetLastPInvokeError()}");
        Landlock.Restrict(abi, jobDir);
        Seccomp.Install(arch);

        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        env[SandboxedVariable] = "1";
        env["HOME"] = jobDir;
        env["DOTNET_EnableDiagnostics"] = "0";   // the runtime would otherwise try to open a diagnostics socket
        var self = Environment.ProcessPath!;
        string?[] argv = [self, typeof(ReadSandbox).Assembly.Location, Path.GetFullPath(jobFile), null];
        string?[] envp = [.. env.Select(kv => $"{kv.Key}={kv.Value}"), null];
        Native.execve(self, argv, envp);
        throw new Unavailable($"execve failed: errno {Marshal.GetLastPInvokeError()}");
    }

    /// <summary>In the re-exec'd process: prove the sandbox is in force rather than trusting the marker.</summary>
    private static void Verify()
    {
        if (!OperatingSystem.IsLinux() || Native.prctl(Native.PR_GET_NO_NEW_PRIVS, 0, 0, 0, 0) != 1)
            throw new Unavailable("no_new_privs is not set");
        if (Native.prctl(Native.PR_GET_SECCOMP, 0, 0, 0, 0) != 2)
            throw new Unavailable("no seccomp filter is in force");
        var fd = Native.open("/", Native.O_RDONLY | Native.O_CLOEXEC);
        if (fd >= 0) { Native.close(fd); throw new Unavailable("the filesystem is not restricted (/ is readable)"); }
        var sock = Native.socket(2 /* AF_INET */, 1 /* SOCK_STREAM */, 0);
        if (sock >= 0) { Native.close(sock); throw new Unavailable("sockets can still be created"); }
    }

    /// <summary>The Landlock ABI the kernel offers, or null when it offers none (or under the test switch).</summary>
    internal static int? LandlockAbi()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable(ForceUnavailableVariable) == "1") return null;
        try
        {
            var v = Native.syscall(Landlock.SYS_create_ruleset, 0, 0, Landlock.CREATE_RULESET_VERSION, 0);
            return v > 0 ? (int)v : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>Does the kernel support seccomp filters at all (PR_GET_SECCOMP answers)?</summary>
    internal static bool SeccompSupported()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return Native.prctl(Native.PR_GET_SECCOMP, 0, 0, 0, 0) >= 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // ── `sandbox-probe` ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Report whether the sandbox is in force (for /health) and, for the conformance tests, attempt
    /// each listed operation and say whether the operating system allowed it. Never returns content.
    /// Ops: read | write | list {path}; tcp {port} on 127.0.0.1; unix {path}.
    /// </summary>
    internal static JsonObject Probe(string jobFile)
    {
        var job = JsonNode.Parse(File.ReadAllText(jobFile))!.AsObject();
        var results = new JsonArray();
        foreach (var p in job["probes"]?.AsArray() ?? [])
        {
            var op = p!["op"]!.GetValue<string>();
            var path = p["path"]?.GetValue<string>();
            try
            {
                switch (op)
                {
                    case "read": using (File.OpenRead(path!)) { } break;
                    case "write": File.WriteAllText(path!, "probe"); break;
                    case "list": Directory.GetFileSystemEntries(path!); break;
                    case "tcp":
                        using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                            s.Connect(IPAddress.Loopback, p["port"]!.GetValue<int>());
                        break;
                    case "unix":
                        using (var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                            s.Connect(new UnixDomainSocketEndPoint(path!));
                        break;
                    default: throw new ArgumentException($"unknown probe op '{op}'");
                }
                results.Add(new JsonObject { ["op"] = op, ["ok"] = true });
            }
            catch (Exception ex)
            {
                // "denied" = the OS refused with EACCES/EPERM, as opposed to some other failure.
                var denied = ex is UnauthorizedAccessException
                             || ex is SocketException se && (se.SocketErrorCode == SocketError.AccessDenied || se.NativeErrorCode is 1 or 13);
                results.Add(new JsonObject { ["op"] = op, ["ok"] = false, ["denied"] = denied, ["error"] = $"{ex.GetType().Name}: {ex.Message}" });
            }
        }
        return new JsonObject
        {
            ["landlockAbi"] = LandlockAbi(),
            ["seccomp"] = Sandboxed || SeccompSupported(),
            ["enforced"] = Sandboxed,   // Sandboxed here means Verify() already passed in EnterIfRequired
            ["results"] = results,
        };
    }

    // ── native surface ─────────────────────────────────────────────────────────────────────────

    /// <summary>The seccomp numbers that differ by CPU. The image is linux/amd64; aarch64 is here so
    /// the conformance tests also run natively on an arm64 host.</summary>
    private sealed record Arch(uint AuditArch, uint SysSocket, uint SysIoUringSetup, bool HasX32)
    {
        internal static Arch? Current => RuntimeInformation.ProcessArchitecture switch
        {
            // AUDIT_ARCH_X86_64; socket = 41, io_uring_setup = 425 (arch/x86/entry/syscalls/syscall_64.tbl)
            Architecture.X64 => new Arch(0xC000003E, 41, 425, HasX32: true),
            // AUDIT_ARCH_AARCH64; socket = 198, io_uring_setup = 425 (include/uapi/asm-generic/unistd.h)
            Architecture.Arm64 => new Arch(0xC00000B7, 198, 425, HasX32: false),
            _ => null,
        };
    }

    private static class Landlock
    {
        // landlock_create_ruleset / landlock_add_rule / landlock_restrict_self are 444/445/446 on
        // every architecture (they postdate the per-arch tables).
        internal const long SYS_create_ruleset = 444, SYS_add_rule = 445, SYS_restrict_self = 446;
        internal const nint CREATE_RULESET_VERSION = 1;
        private const nint RULE_PATH_BENEATH = 1;

        // LANDLOCK_ACCESS_FS_* (include/uapi/linux/landlock.h). ABI 1 defines bits 0..12, ABI 2 adds
        // REFER (13), ABI 3 TRUNCATE (14), ABI 5 IOCTL_DEV (15). Every right the kernel knows is
        // HANDLED, so whatever is not granted below is refused.
        private const ulong EXECUTE = 1 << 0, WRITE_FILE = 1 << 1, READ_FILE = 1 << 2, READ_DIR = 1 << 3,
                            TRUNCATE = 1 << 14, IOCTL_DEV = 1 << 15;
        private const ulong ReadOnly = EXECUTE | READ_FILE | READ_DIR;
        private const ulong FileRights = EXECUTE | WRITE_FILE | READ_FILE | TRUNCATE | IOCTL_DEV;

        private static ulong Handled(int abi) => abi switch
        {
            1 => (1UL << 13) - 1, 2 => (1UL << 14) - 1, 3 or 4 => (1UL << 15) - 1, _ => (1UL << 16) - 1,
        };

        // struct landlock_ruleset_attr — only its first field (handled_access_fs), the ABI-1 layout,
        // which every later kernel accepts. struct landlock_path_beneath_attr is packed: u64 + s32.
        [StructLayout(LayoutKind.Sequential)]
        private struct RulesetAttr { public ulong HandledAccessFs; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PathBeneathAttr { public ulong AllowedAccess; public int ParentFd; }

        internal static void Restrict(int abi, string jobDir)
        {
            var handled = Handled(abi);
            var ruleset = (int)Native.WithPinned(new RulesetAttr { HandledAccessFs = handled },
                attr => Native.syscall(SYS_create_ruleset, attr, Marshal.SizeOf<RulesetAttr>(), 0, 0));
            if (ruleset < 0) throw new Unavailable($"landlock_create_ruleset failed: errno {Marshal.GetLastPInvokeError()}");
            try
            {
                // Read-only: the system libraries and .NET (both under /usr in the image; the dotnet root
                // is added for hosts that install it elsewhere), the font configuration SkiaSharp reads,
                // the worker's own binaries and the engine install. /lib and /lib64 are symlinks into /usr
                // on Debian 12; listed for hosts where they are not.
                var engine = Environment.GetEnvironmentVariable("DWSIM_PATH") ?? "/opt/dwsim";
                foreach (var dir in new[] { "/usr", "/lib", "/lib64", "/etc/fonts", Path.GetDirectoryName(Environment.ProcessPath!)! })
                    Grant(ruleset, dir, ReadOnly & handled, required: false);
                // This process's OWN /proc entry: CoreCLR will not start without it (measured: "Failed to
                // create CoreCLR, 0x8007000E" with no /proc grant). "/proc/self" resolves, here, to
                // /proc/<this pid>, which the execve below keeps. No other process's entry is granted.
                Grant(ruleset, "/proc/self", ReadOnly & handled, required: true);
                Grant(ruleset, AppContext.BaseDirectory, ReadOnly & handled, required: true);
                Grant(ruleset, engine, ReadOnly & handled, required: true);
                // Single files the dynamic loader and the runtime open.
                Grant(ruleset, "/etc/ld.so.cache", READ_FILE, required: false);
                Grant(ruleset, "/dev/null", (READ_FILE | WRITE_FILE | TRUNCATE | IOCTL_DEV) & handled, required: false);
                Grant(ruleset, "/dev/urandom", READ_FILE, required: false);
                // Read-write: this job's directory, and nothing else.
                Grant(ruleset, jobDir, handled, required: true);

                if (Native.syscall(SYS_restrict_self, ruleset, 0, 0, 0) != 0)
                    throw new Unavailable($"landlock_restrict_self failed: errno {Marshal.GetLastPInvokeError()}");
            }
            finally { Native.close(ruleset); }
        }

        private static void Grant(int ruleset, string path, ulong access, bool required)
        {
            var fd = Native.open(path, Native.O_PATH | Native.O_CLOEXEC);
            if (fd < 0)
            {
                if (required) throw new Unavailable($"cannot open '{path}' to grant it: errno {Marshal.GetLastPInvokeError()}");
                return;
            }
            try
            {
                if (!Directory.Exists(path)) access &= FileRights;   // a rule on a file may carry only file rights
                var rc = Native.WithPinned(new PathBeneathAttr { AllowedAccess = access, ParentFd = fd },
                    rule => Native.syscall(SYS_add_rule, ruleset, RULE_PATH_BENEATH, rule, 0));
                if (rc != 0) throw new Unavailable($"landlock_add_rule '{path}' failed: errno {Marshal.GetLastPInvokeError()}");
            }
            finally { Native.close(fd); }
        }
    }

    private static class Seccomp
    {
        // Classic BPF over struct seccomp_data { int nr; u32 arch; u64 ip; u64 args[6]; }.
        private const ushort LD_W_ABS = 0x20, JEQ_K = 0x15, JGE_K = 0x35, RET_K = 0x06;
        private const uint RET_ALLOW = 0x7fff0000, RET_ERRNO_EPERM = 0x00050000 | 1;
        private const uint OffsetNr = 0, OffsetArch = 4, X32Bit = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct Insn { public ushort Code; public byte Jt; public byte Jf; public uint K; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Prog { public ushort Len; public nint Filter; }

        internal static void Install(Arch arch)
        {
            // 0 load arch; 1 another arch → deny; 2 load nr; 3 x32 numbering (x86-64 only) → deny;
            // 4 socket → deny; 5 io_uring_setup → deny; 6 allow; 7 deny with EPERM.
            Insn[] f =
            [
                new() { Code = LD_W_ABS, K = OffsetArch },
                new() { Code = JEQ_K, Jt = 0, Jf = 5, K = arch.AuditArch },
                new() { Code = LD_W_ABS, K = OffsetNr },
                new() { Code = JGE_K, Jt = 3, Jf = 0, K = arch.HasX32 ? X32Bit : uint.MaxValue },
                new() { Code = JEQ_K, Jt = 2, Jf = 0, K = arch.SysSocket },
                new() { Code = JEQ_K, Jt = 1, Jf = 0, K = arch.SysIoUringSetup },
                new() { Code = RET_K, K = RET_ALLOW },
                new() { Code = RET_K, K = RET_ERRNO_EPERM },
            ];
            var pin = GCHandle.Alloc(f, GCHandleType.Pinned);
            try
            {
                var rc = Native.WithPinned(new Prog { Len = (ushort)f.Length, Filter = pin.AddrOfPinnedObject() },
                    prog => Native.prctl(Native.PR_SET_SECCOMP, Native.SECCOMP_MODE_FILTER, prog, 0, 0));
                if (rc != 0) throw new Unavailable($"installing the seccomp filter failed: errno {Marshal.GetLastPInvokeError()}");
            }
            finally { pin.Free(); }
        }
    }

    private static class Native
    {
        internal const int PR_GET_SECCOMP = 21, PR_SET_SECCOMP = 22, PR_SET_NO_NEW_PRIVS = 38, PR_GET_NO_NEW_PRIVS = 39;
        internal const nint SECCOMP_MODE_FILTER = 2;
        // O_PATH and O_CLOEXEC have the same values on x86-64 and aarch64.
        internal const int O_RDONLY = 0, O_CLOEXEC = 0x80000, O_PATH = 0x200000;

        // prctl and syscall are variadic in glibc. Every argument here is a full-width integer or
        // pointer, which x86-64 and aarch64 both pass in registers exactly as for a fixed signature.
        [DllImport("libc", SetLastError = true)] internal static extern int prctl(int option, nint a2, nint a3, nint a4, nint a5);
        [DllImport("libc", SetLastError = true)] internal static extern long syscall(long nr, nint a1, nint a2, nint a3, nint a4);
        [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int socket(int domain, int type, int protocol);
        [DllImport("libc", SetLastError = true)] internal static extern int execve(string path, string?[] argv, string?[] envp);

        /// <summary>Pass a struct to native code by address: pinned for the duration of the call.</summary>
        internal static long WithPinned<T>(T value, Func<nint, long> call) where T : struct
        {
            var box = new[] { value };
            var pin = GCHandle.Alloc(box, GCHandleType.Pinned);
            try { return call(pin.AddrOfPinnedObject()); }
            finally { pin.Free(); }
        }
    }
}
