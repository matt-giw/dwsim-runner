// dwsim-runner API — GPL-3.0
// User-template persistence (T036/US3): flowsheets saved by
// /flowsheets/build-solve as engine-written .dwxmz files under
// USER_TEMPLATES_PATH, listed alongside the curated read-only templates/ set.
// A `<id>.doc.json` provenance sidecar carries the source document plus
// createdUtc/solvedAtSave metadata (data-model.md §User Template). No DWSIM
// types here — the Worker writes the .dwxmz; this class manages the directory.

using System.Text.Json;

namespace DwsimRunner.Api;

public sealed record TemplateEntry(string Id, string Source, string? CreatedUtc, bool? SolvedAtSave);

public sealed class UserTemplateStore(string userTemplatesPath, string curatedTemplatesPath)
{
    public string UserTemplatesPath { get; } = Path.GetFullPath(userTemplatesPath);
    public string CuratedTemplatesPath { get; } = Path.GetFullPath(curatedTemplatesPath);

    /// <summary>True when the user-template directory exists and is writable;
    /// saves are rejected with a clear error when it isn't (e.g. a read-only
    /// mount without a USER_TEMPLATES_PATH volume). Never crashes startup.</summary>
    public bool Writable { get; private set; }

    public void EnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(UserTemplatesPath);
            var probe = Path.Combine(UserTemplatesPath, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            Writable = true;
        }
        catch (IOException) { Writable = false; }
        catch (UnauthorizedAccessException) { Writable = false; }
    }

    public string UserTemplateFile(string id) => Path.Combine(UserTemplatesPath, id + ".dwxmz");
    public string SidecarFile(string id) => Path.Combine(UserTemplatesPath, id + ".doc.json");

    public bool CuratedExists(string id) => File.Exists(Path.Combine(CuratedTemplatesPath, id + ".dwxmz"));
    public bool UserExists(string id) => File.Exists(UserTemplateFile(id));

    /// <summary>Merged listing: curated first (readonly), then user templates
    /// with sidecar metadata. Ordered by id within each source.</summary>
    public List<TemplateEntry> List()
    {
        var entries = new List<TemplateEntry>();
        if (Directory.Exists(CuratedTemplatesPath))
            entries.AddRange(Directory.EnumerateFiles(CuratedTemplatesPath, "*.dwxmz")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(id => new TemplateEntry(id!, "curated", null, null)));

        if (Directory.Exists(UserTemplatesPath))
            entries.AddRange(Directory.EnumerateFiles(UserTemplatesPath, "*.dwxmz")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(id =>
                {
                    var (created, solved) = ReadSidecarMetadata(id!);
                    return new TemplateEntry(id!, "user", created, solved);
                }));
        return entries;
    }

    /// <summary>Write the provenance sidecar next to a freshly saved template.</summary>
    public void WriteSidecar(string id, JsonElement document, bool solvedAtSave)
    {
        var payload = new Dictionary<string, object?>
        {
            ["createdUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["solvedAtSave"] = solvedAtSave,
            ["document"] = document,
        };
        File.WriteAllText(SidecarFile(id),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Delete a user template (dwxmz + sidecar). Returns:
    /// 204-equivalent true on success; throws for curated/unknown handled by caller.</summary>
    public void Delete(string id)
    {
        File.Delete(UserTemplateFile(id));
        try { File.Delete(SidecarFile(id)); } catch { /* sidecar is best-effort */ }
    }

    private (string? CreatedUtc, bool? SolvedAtSave) ReadSidecarMetadata(string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SidecarFile(id)));
            var created = doc.RootElement.TryGetProperty("createdUtc", out var c) ? c.GetString() : null;
            bool? solved = doc.RootElement.TryGetProperty("solvedAtSave", out var s)
                && s.ValueKind is JsonValueKind.True or JsonValueKind.False ? s.GetBoolean() : null;
            return (created, solved);
        }
        catch { return (null, null); }
    }
}
