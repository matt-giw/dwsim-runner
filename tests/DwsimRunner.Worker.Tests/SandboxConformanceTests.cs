// dwsim-runner Worker tests — GPL-3.0
// iskra 285, #31 re-review residuals — conformance of the read worker's sandbox (ReadSandbox.cs).
//
// Each test runs the REAL worker as a child process in its test-only `sandbox-probe` mode, which
// attempts a list of operations and reports whether the operating system allowed each one. Every
// operation is run twice: once with the sandbox applied (it must be refused with a permission error)
// and once without (it must succeed) — without the second run a refusal could be some unrelated
// failure and the test would prove nothing. The probe reports success or the error, never content.
//
// Linux only, and run as a non-root user (`--user 10001` in the docker recipe). The process tree is
// test-host → worker, so the test host is the worker's parent.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace DwsimRunner.Worker.Tests;

[Trait("Category", "Sandbox")]
public sealed class SandboxConformanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sandbox-conformance-").FullName;
    private string Job => Path.Combine(_root, "job");               // the per-job directory: the only read-write grant
    private string Engine => Path.Combine(_root, "engine");         // stands in for the DWSIM install (DWSIM_PATH)
    private string Neighbour => Path.Combine(_root, "neighbour");   // another job's directory
    private string Templates => Path.Combine(_root, "templates");   // stands in for USER_TEMPLATES_PATH

    public SandboxConformanceTests()
    {
        foreach (var d in new[] { Job, Engine, Neighbour, Templates }) Directory.CreateDirectory(d);
        // DwsimResolver.Install only checks this file exists; the probe mode loads no engine code.
        File.WriteAllText(Path.Combine(Engine, "DWSIM.Automation.dll"), "");
        File.WriteAllText(Path.Combine(Neighbour, "upload.dwxml"), "another job's upload");
        File.WriteAllText(Path.Combine(Templates, "saved.dwxmz"), "a customer's saved flowsheet");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_file_outside_the_granted_directories_cannot_be_opened() =>
        AssertRefusedOnlyUnderSandbox(new() { ["op"] = "read", ["path"] = Path.Combine(Neighbour, "upload.dwxml") });

    [Fact]
    public void The_parent_processes_environment_cannot_be_opened() =>
        AssertRefusedOnlyUnderSandbox(new() { ["op"] = "read", ["path"] = $"/proc/{Environment.ProcessId}/environ" });

    [Fact]
    public void Nothing_can_be_written_under_the_engine_install() =>
        AssertRefusedOnlyUnderSandbox(new() { ["op"] = "write", ["path"] = Path.Combine(Engine, "dropped.dll") });

    [Fact]
    public void The_template_store_cannot_be_listed() =>
        AssertRefusedOnlyUnderSandbox(new() { ["op"] = "list", ["path"] = Templates });

    [Fact]
    public void A_file_in_the_template_store_cannot_be_opened() =>
        AssertRefusedOnlyUnderSandbox(new() { ["op"] = "read", ["path"] = Path.Combine(Templates, "saved.dwxmz") });

    [Fact]
    public void A_tcp_connection_to_a_local_listener_cannot_be_made()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        AssertRefusedOnlyUnderSandbox(new() { ["op"] = "tcp", ["port"] = ((IPEndPoint)listener.LocalEndpoint).Port });
    }

    // The API's own .NET diagnostics channel is a unix socket in its temp directory; a socket of any
    // family is refused, so this is closed with the same rule.
    [Fact]
    public void A_unix_socket_connection_cannot_be_made()
    {
        var path = Path.Combine(Neighbour, "s.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        AssertRefusedOnlyUnderSandbox(new() { ["op"] = "unix", ["path"] = path });
    }

    // The positive control: the sandbox is not "refuse everything". The job directory stays usable.
    [Fact]
    public void The_job_directory_stays_readable_and_writable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var probe = new JsonObject { ["op"] = "write", ["path"] = Path.Combine(Job, "scratch.txt") };
        var (code, report) = RunProbe(sandbox: true, probe);
        Assert.Equal(0, code);
        Assert.True(report["enforced"]!.GetValue<bool>());
        Assert.True(report["results"]![0]!["ok"]!.GetValue<bool>(), report.ToJsonString());
    }

    // Fail closed: when the sandbox cannot be applied the worker answers SANDBOX_UNAVAILABLE (exit 7)
    // and runs nothing — for the probe and for `read` itself. The switch only ever makes the worker
    // refuse; the API's environment allow-list never passes it.
    [Theory]
    [InlineData("sandbox-probe")]
    [InlineData("read")]
    public void Without_the_sandbox_the_worker_refuses_with_a_named_error(string mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var upload = Path.Combine(Job, "upload.dwxml");
        File.WriteAllText(upload, "<DWSIM_Simulation_Data/>");
        var job = new JsonObject { ["mode"] = mode, ["template"] = upload, ["probes"] = new JsonArray() };
        var (code, report) = Run(job, new() { [ReadSandbox.ForceUnavailableVariable] = "1" });
        Assert.Equal(ReadSandbox.ExitUnavailable, code);
        Assert.Equal("SANDBOX_UNAVAILABLE", report["error"]!.GetValue<string>());
    }

    // #31 third review A — the gate must agree with the dispatcher. Program.cs deserialises the job
    // case-insensitively and lower-cases the mode, so every spelling below dispatches to `read`; each
    // must be refused when the sandbox is unavailable, never reach the reader unsandboxed.
    [Theory]
    [InlineData("""{"Mode":"read"}""")]
    [InlineData("""{"MODE":"read"}""")]
    [InlineData("""{"mode":"READ"}""")]
    [InlineData("""{"mode":"Read","sandbox":false}""")]
    public void Every_spelling_that_dispatches_to_read_is_refused_without_the_sandbox(string job)
    {
        if (!OperatingSystem.IsLinux()) return;
        var (code, report) = Run(WithUpload(job), new() { [ReadSandbox.ForceUnavailableVariable] = "1" });
        Assert.Equal(ReadSandbox.ExitUnavailable, code);
        Assert.Equal("SANDBOX_UNAVAILABLE", report["error"]!.GetValue<string>());
    }

    // Spellings the dispatcher does NOT send to `read` (it does not trim; a missing mode is `solve`):
    // whatever they do, the reader — and so the upload — is never reached.
    [Theory]
    [InlineData("""{"mode":" read"}""")]
    [InlineData("""{}""")]
    public void A_spelling_that_does_not_dispatch_to_read_never_reaches_the_reader(string job)
    {
        if (!OperatingSystem.IsLinux()) return;
        var (code, report) = Run(WithUpload(job), new() { [ReadSandbox.ForceUnavailableVariable] = "1" });
        Assert.NotEqual(0, code);
        Assert.NotEqual("LOAD_FAILED", report["error"]?.GetValue<string>());
        Assert.Null(report["document"]);
    }

    // The sink: the reader itself refuses to run unless the sandbox is in force, whatever the
    // dispatch did. Called in-process here, where no sandbox exists.
    [Fact]
    public void The_reader_refuses_to_run_outside_the_sandbox()
    {
        var upload = Path.Combine(Job, "upload.dwxml");
        File.WriteAllText(upload, "<DWSIM_Simulation_Data/>");
        var job = new Job(upload, null, "read", null, null, null, null);
        Assert.Throws<SandboxUnavailableException>(() => Reader.Read(job));
    }

    // #31 third review B — below Landlock ABI 3 the truncate right does not exist, so the sandbox
    // would not be the same sandbox. Refused, not reported as enforced. The test switch can only
    // LOWER the ABI the minimum is checked against; the ruleset is always built from the real one.
    [Theory]
    [InlineData("read")]
    [InlineData("sandbox-probe")]
    public void A_kernel_below_landlock_abi_3_is_refused(string mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var (code, report) = Run(WithUpload($$"""{"mode":"{{mode}}"}"""), new() { [ReadSandbox.TestAbiVariable] = "2" });
        Assert.Equal(ReadSandbox.ExitUnavailable, code);
        Assert.Equal("SANDBOX_UNAVAILABLE", report["error"]!.GetValue<string>());
        Assert.Contains("ABI 3", report["message"]!.GetValue<string>());
        Assert.False(report["enforced"]!.GetValue<bool>());
    }

    // A REAL engine read under the sandbox: the probe-mode tests use a stand-in engine directory, so
    // this is the one that proves the engine can load a file inside it on this kernel. Needs the real
    // DWSIM install (DWSIM_PATH) with native libraries for this CPU — CI's x86-64 worker job.
    // Skipped, with the reason, where the engine's native libraries cannot load on this CPU (the
    // DWSIM bundle's are x86-64, so an Apple-silicon container skips it); it runs on x86-64 CI.
    [SkippableFact]
    [Trait("Category", "SandboxRealRead")]
    public void A_real_engine_read_succeeds_under_the_sandbox()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the read sandbox is Linux-only");
        var mismatch = EngineNatives.MismatchReason();
        Skip.If(mismatch is not null, mismatch);
        var template = Path.Combine(RepoRoot(), "templates", "methanol_synthesis.dwxmz");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(template))
        using (var xml = zip.Entries.First(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Open())
        using (var upload = File.Create(Path.Combine(Job, "upload.dwxml")))
            xml.CopyTo(upload);
        var job = new JsonObject { ["mode"] = "read", ["template"] = Path.Combine(Job, "upload.dwxml") };
        var (code, report) = Run(job, new() { ["DWSIM_PATH"] = DwsimResolver.DwsimPath });
        Assert.True(code == 0, report.ToJsonString());
        Assert.NotEmpty(report["document"]!["objects"]!.AsArray());
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "templates")) && File.Exists(Path.Combine(d.FullName, "DwsimRunner.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root (templates/) not found above " + AppContext.BaseDirectory);
    }

    private JsonObject WithUpload(string job)
    {
        var upload = Path.Combine(Job, "upload.dwxml");
        File.WriteAllText(upload, "<DWSIM_Simulation_Data/>");
        var node = JsonNode.Parse(job)!.AsObject();
        node["template"] = upload;
        return node;
    }

    private void AssertRefusedOnlyUnderSandbox(JsonObject probe)
    {
        if (!OperatingSystem.IsLinux()) return;

        var (openCode, open) = RunProbe(sandbox: false, (JsonObject)probe.DeepClone());
        Assert.Equal(0, openCode);
        Assert.True(open["results"]![0]!["ok"]!.GetValue<bool>(),
            "without the sandbox the operation must succeed, or this test proves nothing: " + open.ToJsonString());

        var (code, boxed) = RunProbe(sandbox: true, probe);
        Assert.Equal(0, code);
        Assert.True(boxed["enforced"]!.GetValue<bool>(), boxed.ToJsonString());
        var result = boxed["results"]![0]!;
        Assert.False(result["ok"]!.GetValue<bool>(), "the sandbox allowed it: " + boxed.ToJsonString());
        Assert.True(result["denied"]!.GetValue<bool>(), "refused, but not with a permission error: " + boxed.ToJsonString());
    }

    private (int Code, JsonObject Report) RunProbe(bool sandbox, JsonObject probe) =>
        Run(new JsonObject { ["mode"] = "sandbox-probe", ["sandbox"] = sandbox, ["probes"] = new JsonArray(probe) });

    private (int Code, JsonObject Report) Run(JsonObject job, Dictionary<string, string>? env = null)
    {
        var jobFile = Path.Combine(Job, $"job-{Guid.NewGuid():N}.json");
        File.WriteAllText(jobFile, job.ToJsonString());
        var worker = typeof(ReadSandbox).Assembly.Location;
        var psi = new ProcessStartInfo("dotnet", $"\"{worker}\" \"{jobFile}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["TMPDIR"] = Job;
        psi.Environment["DWSIM_PATH"] = Engine;
        foreach (var kv in env ?? []) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "worker did not exit");
        var text = stdout.Result.Trim();
        try { return (p.ExitCode, JsonNode.Parse(text)!.AsObject()); }
        catch (JsonException) { throw new Xunit.Sdk.XunitException($"exit {p.ExitCode}, stdout: {text}\nstderr: {stderr.Result}"); }
    }
}
