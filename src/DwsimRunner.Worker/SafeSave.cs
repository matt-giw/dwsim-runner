namespace DwsimRunner.Worker;

/// <summary>
/// The engine save, best effort (spec 011 Cut 2: a save failure MUST NOT throw — the solve result is
/// the answer; the API infers a failed save from the file's absence).
///
/// iskra 286 — a save that fails part-way can leave a truncated archive at a FRESH path, which the
/// export route's "exists and is not empty" check would serve as a 200. So a failed save removes the
/// file — but ONLY if there was no file there before: `saveAsTemplate` with `overwrite:true` re-saves
/// over a user's existing template, and a re-save that fails must leave that template as it was.
/// </summary>
public static class SafeSave
{
    public static void Run(string path, Action<string> save)
    {
        var existed = File.Exists(path);
        try
        {
            save(path);
        }
        catch (Exception)
        {
            if (!existed)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* nothing more to do */ }
            }
        }
    }
}
