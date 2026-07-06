// dwsim-runner API — GPL-3.0
// Bounded LRU for solve results (FR-013). Key = SHA-256 of templateId +
// template file mtime + canonicalized overrides, so republishing a template
// invalidates its entries and equivalent requests collapse to one entry.
// Only converged results are stored (the caller decides). Values are the
// exact response body string → cache hits are byte-identical (FR-008).

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
}
