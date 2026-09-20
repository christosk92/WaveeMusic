using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wavee.LocPack;

public enum IcuKind { None, Plural, Select }

public sealed record Surface(string Id, string Where, string Screenshot);

public static class LocCatalog
{
    static readonly HashSet<string> IcuKeywords = new(StringComparer.Ordinal)
    {
        "plural", "select", "other", "one", "few", "many", "zero", "two", "offset",
    };

    public static Dictionary<string, string> Flatten(JsonElement root)
    {
        var sink = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, prefix: "", sink);
        return sink;
    }

    public static Dictionary<string, string> FlattenJson(string json)
        => Flatten(JsonDocument.Parse(json).RootElement);

    static void Walk(JsonElement node, string prefix, Dictionary<string, string> sink)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name.Length > 0 && prop.Name[0] == '$') continue;
            string dotted = prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name;
            if (prop.Value.ValueKind == JsonValueKind.Object)
                Walk(prop.Value, dotted, sink);
            else if (prop.Value.ValueKind == JsonValueKind.String)
                sink[dotted] = prop.Value.GetString() ?? "";
        }
    }

    public static JsonObject Nest(IReadOnlyDictionary<string, string> flat, string culture)
    {
        var root = new JsonObject { ["$culture"] = culture };
        foreach (var kv in flat.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string[] segs = kv.Key.Split('.');
            JsonObject cur = root;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                string seg = segs[i];
                if (cur[seg] is JsonObject next)
                    cur = next;
                else
                {
                    next = new JsonObject();
                    cur[seg] = next;
                    cur = next;
                }
            }
            cur[segs[^1]] = kv.Value;
        }
        return root;
    }

    public static IcuKind Classify(string template)
    {
        if (template.Contains(", plural,", StringComparison.Ordinal)) return IcuKind.Plural;
        if (template.Contains(", select,", StringComparison.Ordinal)) return IcuKind.Select;
        return IcuKind.None;
    }

    public static string[] Placeholders(string template)
    {
        var list = new List<string>();
        int i = 0;
        while (i < template.Length)
        {
            int open = template.IndexOf('{', i);
            if (open < 0) break;
            int j = open + 1;
            if (j >= template.Length) break;
            if (!IsIdentStart(template[j])) { i = open + 1; continue; }
            int k = j + 1;
            while (k < template.Length && IsIdentPart(template[k])) k++;
            string name = template[j..k];
            if (!IcuKeywords.Contains(name) && !list.Contains(name, StringComparer.Ordinal))
                list.Add(name);
            i = k;
        }
        return list.ToArray();
    }

    static bool IsIdentStart(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
    static bool IsIdentPart(char c)
        => IsIdentStart(c) || (c >= '0' && c <= '9');

    public static bool PlaceholdersMatch(string source, string translation)
    {
        var a = Placeholders(source);
        var b = Placeholders(translation);
        if (a.Length != b.Length) return false;
        var set = new HashSet<string>(a, StringComparer.Ordinal);
        return b.All(set.Contains);
    }

    public static bool BracesBalanced(string s)
    {
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth < 0) return false;
            }
        }
        return depth == 0;
    }

    public static string SurfaceOf(string key, IReadOnlyDictionary<string, Surface> map)
    {
        string? best = null;
        foreach (string prefix in map.Keys)
        {
            if (key.Equals(prefix, StringComparison.Ordinal) ||
                key.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                if (best is null || prefix.Length > best.Length) best = prefix;
            }
        }
        return best ?? "other";
    }

    public static Packet MissingOf(
        string culture,
        Dictionary<string, string> en,
        Dictionary<string, string> sat,
        string packetId,
        Func<string, bool> include)
    {
        var rows = new List<PacketRow>();
        foreach (var kv in en.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!include(kv.Key)) continue;
            sat.TryGetValue(kv.Key, out string? current);
            bool missing = current is null;
            bool identical = current is not null && current == kv.Value;
            if (!missing && !identical) continue;
            rows.Add(new PacketRow
            {
                Key = kv.Key,
                English = kv.Value,
                Current = current ?? "",
                AiDraft = "",
                Yours = "",
                Status = "todo",
                Where = "",
                Notes = "",
                Placeholders = Placeholders(kv.Value),
                Icu = Classify(kv.Value) switch
                {
                    IcuKind.Plural => "plural",
                    IcuKind.Select => "select",
                    _ => "none",
                },
                Example = "",
                KeepEnglish = false,
            });
        }
        return new Packet
        {
            Culture = culture,
            PacketId = packetId,
            Surface = packetId,
            Screenshot = "",
            Glossary = [],
            Rows = rows,
        };
    }

    public static Dictionary<string, string> ImportApproved(
        Dictionary<string, string> satellite,
        Packet packet)
    {
        var next = new Dictionary<string, string>(satellite, StringComparer.Ordinal);
        foreach (PacketRow row in packet.Rows)
        {
            if (!string.Equals(row.Status, "approved", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(row.Yours)) continue;
            if (!PlaceholdersMatch(row.English, row.Yours) || !BracesBalanced(row.Yours))
                throw new InvalidOperationException($"Placeholder/ICU QA failed for {row.Key}");
            next[row.Key] = row.Yours;
        }
        return next;
    }

    public static Dictionary<string, Surface> LoadSurfaces(string json)
    {
        var map = new Dictionary<string, Surface>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            map[prop.Name] = new Surface(
                prop.Value.TryGetProperty("surface", out var s) ? s.GetString() ?? prop.Name : prop.Name,
                prop.Value.TryGetProperty("where", out var w) ? w.GetString() ?? "" : "",
                prop.Value.TryGetProperty("screenshot", out var sc) ? sc.GetString() ?? "" : "");
        }
        return map;
    }
}
