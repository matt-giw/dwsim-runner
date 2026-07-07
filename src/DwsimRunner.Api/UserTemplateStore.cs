// dwsim-runner API — GPL-3.0
// User-template persistence: flowsheets saved by /flowsheets/build-solve as
// engine-written .dwxmz files under USER_TEMPLATES_PATH, listed alongside the
// curated read-only templates/ set. No DWSIM types here — the Worker writes
// the files; this class only manages the directory.

namespace DwsimRunner.Api;

public sealed class UserTemplateStore(string userTemplatesPath, string curatedTemplatesPath)
{
    public string UserTemplatesPath { get; } = Path.GetFullPath(userTemplatesPath);
    public string CuratedTemplatesPath { get; } = Path.GetFullPath(curatedTemplatesPath);

    public void EnsureDirectory() => Directory.CreateDirectory(UserTemplatesPath);
}
