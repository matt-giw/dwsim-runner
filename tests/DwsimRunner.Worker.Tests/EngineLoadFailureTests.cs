// dwsim-runner Worker tests — GPL-3.0
// iskra 285, #31 final review — a load failure in the NON-read modes must name the engine's cause.
// DWSIM.Logging.Logger writes to $HOME/Documents/DWSIM Application Data when $HOME/Documents exists,
// else to a directory relative to the engine's working directory (measured: the engine install, which
// is root-owned in the image), so without $HOME/Documents the logger's type initializer throws and masks
// the real cause. The image creates /home/runner/Documents. This runs the real worker in `inspect` mode
// on a template whose property package the engine does not have, with HOME/Documents present, and checks
// the log went THERE and the error names the engine's cause. (Whether the fallback location is writable
// depends on the host, so the masked case is measured on the image, not here.)
// Skipped where the engine's natives cannot load on this CPU.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;

namespace DwsimRunner.Worker.Tests;

[Trait("Category", "EngineLoad")]
public sealed class EngineLoadFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("engine-load-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [SkippableFact]
    public void An_inspect_load_failure_names_the_engines_cause_and_logs_under_home_documents()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-only");
        var mismatch = EngineNatives.MismatchReason();
        Skip.If(mismatch is not null, mismatch);

        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        Directory.CreateDirectory(Path.Combine(home, "Documents"));
        var template = BrokenTemplate();
        var jobFile = Path.Combine(_root, "job.json");
        File.WriteAllText(jobFile, new JsonObject { ["mode"] = "inspect", ["template"] = template }.ToJsonString());

        var psi = new ProcessStartInfo("dotnet", $"\"{typeof(Sanitizer).Assembly.Location}\" \"{jobFile}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = "/",
        };
        psi.Environment["HOME"] = home;
        psi.Environment["TMPDIR"] = _root;
        psi.Environment["DWSIM_PATH"] = DwsimResolver.DwsimPath;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "worker did not exit");
        var report = JsonNode.Parse(stdout.Result.Trim())!;

        Assert.Equal(3, p.ExitCode);
        var message = report["message"]!.GetValue<string>();
        Assert.DoesNotContain("DWSIM.Logging.Logger", message);
        Assert.Contains("Property Package", message);   // the engine's own cause
        Assert.True(File.Exists(Path.Combine(home, "Documents", "DWSIM Application Data", "errors.log")),
            "the engine's logger did not write under $HOME/Documents");
    }

    /// <summary>The curated template with its property package renamed to one the engine lacks.</summary>
    private string BrokenTemplate()
    {
        var src = Path.Combine(RepoRoot(), "templates", "methanol_synthesis.dwxmz");
        var dst = Path.Combine(_root, "broken.dwxmz");
        using var input = ZipFile.OpenRead(src);
        using var output = ZipFile.Open(dst, ZipArchiveMode.Create);
        foreach (var e in input.Entries)
        {
            using var from = e.Open();
            using var to = output.CreateEntry(e.FullName).Open();
            if (!e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) { from.CopyTo(to); continue; }
            var doc = XDocument.Load(from);
            doc.Root!.Element("PropertyPackages")!.Elements("PropertyPackage").First()
               .Element("ComponentName")!.Value = "No Such Package";
            doc.Save(to);
        }
        return dst;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "templates")) && File.Exists(Path.Combine(d.FullName, "DwsimRunner.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root (templates/) not found above " + AppContext.BaseDirectory);
    }
}
