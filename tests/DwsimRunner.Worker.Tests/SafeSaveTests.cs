// iskra 286 — the worker's best-effort save (review of dwsim-runner#30). The rule is pure, so it is
// pinned here with a stand-in for SaveFlowsheet2 rather than the engine.
using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class SafeSaveTests
{
    private static string TempPath() =>
        Path.Combine(Directory.CreateTempSubdirectory("safesave-").FullName, "t.dwxmz");

    [Fact]
    public void A_fresh_path_that_fails_part_way_leaves_no_file()
    {
        var path = TempPath();
        SafeSave.Run(path, p => { File.WriteAllText(p, "trunc"); throw new IOException("disk full"); });
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void An_existing_template_survives_a_failed_overwrite()
    {
        var path = TempPath();
        File.WriteAllText(path, "the user's saved template");
        SafeSave.Run(path, _ => throw new InvalidOperationException("engine refused before writing"));
        Assert.Equal("the user's saved template", File.ReadAllText(path));
    }

    [Fact]
    public void A_successful_save_is_left_alone_and_never_throws_on_failure()
    {
        var path = TempPath();
        SafeSave.Run(path, p => File.WriteAllText(p, "saved"));
        Assert.Equal("saved", File.ReadAllText(path));
        var ex = Record.Exception(() => SafeSave.Run(TempPath(), _ => throw new Exception("anything")));
        Assert.Null(ex);
    }
}
