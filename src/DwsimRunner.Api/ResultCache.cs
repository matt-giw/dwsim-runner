// dwsim-runner API — GPL-3.0
// Bounded LRU for solve results (FR-013). Key = SHA-256 of templateId +
// template file mtime + canonicalized overrides, so republishing a template
// invalidates its entries and equivalent requests collapse to one entry.
// Values are the exact response body string → cache hits are byte-identical (FR-008).
//
// The caller decides what to store, and NOT everything stored is converged: the
// document path (`/flowsheets/build-solve`, keyed by `KeyForDocument`) caches any
// HTTP 200, and a non-convergence IS a 200 (Modes.cs: "Non-convergence is a 200 with
// converged:false, never an error code"). That is correct — the engine is
// deterministic and the catalog version is in the key — but it surprises anyone
// timing solves: re-sending a byte-identical failing document returns in ~0 ms.
// Spec 143 measured a whole grid before noticing that half its elapsed times were
// cache hits. The outcomes were right; the timings were not measurements.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class ResultCache(int capacity)
{
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, string Value)>> _map = new();
    private readonly LinkedList<(string Key, string Value)> _lru = new();

    public bool TryGet(string key, out string value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = "";
        return false;
    }

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }
            var node = _lru.AddFirst((key, value));
            _map[key] = node;
            while (_map.Count > _capacity)
            {
                var last = _lru.Last!;
                _map.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    public static string KeyFor(string templateId, string templateFile, IEnumerable<PropertyOverride> overrides)
    {
        var mtime = File.GetLastWriteTimeUtc(templateFile).Ticks;
        var canon = string.Join(";", overrides
            .OrderBy(o => o.Object, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Property, StringComparer.OrdinalIgnoreCase)
            .Select(o => string.Join('|',
                o.Object.ToLowerInvariant(),
                o.Property.ToLowerInvariant(),
                o.Value.ToString("R", CultureInfo.InvariantCulture),
                o.Unit?.ToLowerInvariant() ?? "")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{templateId}\n{mtime}\n{canon}")));
    }

    /// <summary>Key for a flowsheet-document request (FR-BUILD-005): documents
    /// hash identically regardless of property order/whitespace (arrays stay
    /// order-significant); the engine version partitions the key space.</summary>
    public static string KeyForDocument(JsonElement document, string engineVersion)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(document, writer);
        buffer.Write(Encoding.UTF8.GetBytes($"\n{engineVersion}"));
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(JsonElement el, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    WriteCanonical(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteCanonical(item, w);
                w.WriteEndArray();
                break;
            default:
                el.WriteTo(w);
                break;
        }
    }
}
