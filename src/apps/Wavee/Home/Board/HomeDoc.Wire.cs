// ── Home/Board/HomeDoc.Wire.cs: home.acct-<hash>.json v3 (design 1.2.1) ──────────────────────────────────────────────
//
// Hand-read with JsonDocument and hand-written with Utf8JsonWriter: NativeAOT-safe with no reflection, and every member
// this build does not know (an unknown source kind, widget argument, option key, item or file member) survives a round
// trip byte for byte as its raw JSON text. Unknown sources are UnknownSource(kind, raw object).

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wavee;

public enum HomeReadFault : byte { None, Malformed, TooNew }

public readonly record struct HomeReadResult(HomeDoc Doc, HomeReadFault Fault, int FileVersion);

public static class HomeDocWire
{
    static readonly JsonDocumentOptions s_read = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };
    static readonly JsonWriterOptions s_write = new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonWriterOptions s_compact = new() { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static readonly HashSet<string> s_docMembers = new(StringComparer.Ordinal)
        { "version", "rev", "updatedAtMs", "groups", "hidden", "seen", "legacyHidden", "seededFromSidebar" };
    static readonly HashSet<string> s_itemMembers = new(StringComparer.Ordinal) { "id", "source", "size", "options", "shape" };

    // ── read ──

    public static HomeReadResult Read(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var json = JsonDocument.Parse(utf8.ToArray(), s_read);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Bad();
            int version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out int vi) ? vi : 0;
            if (version > HomeDoc.CurrentVersion) return new HomeReadResult(HomeDocs.Default(), HomeReadFault.TooNew, version);
            if (version != HomeDoc.CurrentVersion) return Bad();

            long rev = Long(root, "rev"), at = Long(root, "updatedAtMs");
            var groups = ImmutableArray.CreateBuilder<HomeGroup>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var g in gs.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Object) continue;
                    string? gid = Str(g, "id");
                    if (string.IsNullOrEmpty(gid)) continue;
                    var items = ImmutableArray.CreateBuilder<HomeItem>();
                    if (g.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array)
                        foreach (var it in its.EnumerateArray())
                            if (ReadItem(it) is { } item && ids.Add(item.Id)) items.Add(item);
                    groups.Add(new HomeGroup(gid, Str(g, "title"), Str(g, "titleKey"), items.ToImmutable()));
                }

            var hidden = ImmutableArray.CreateBuilder<SectionRef>();
            if (root.TryGetProperty("hidden", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var h in hs.EnumerateArray()) if (ReadRef(h) is { } r) hidden.Add(r);

            var seen = ImmutableArray.CreateBuilder<SeenRef>();
            if (root.TryGetProperty("seen", out var ss) && ss.ValueKind == JsonValueKind.Array)
                foreach (var s in ss.EnumerateArray()) if (ReadRef(s) is { } r) seen.Add(new SeenRef(r, Long(s, "lastSeenMs")));

            var legacy = ImmutableArray.CreateBuilder<string>();
            if (root.TryGetProperty("legacyHidden", out var ls) && ls.ValueKind == JsonValueKind.Array)
                foreach (var l in ls.EnumerateArray()) if (l.ValueKind == JsonValueKind.String && l.GetString() is { Length: > 0 } name) legacy.Add(name);

            bool seeded = root.TryGetProperty("seededFromSidebar", out var sd) && sd.ValueKind == JsonValueKind.True;

            var doc = new HomeDoc(HomeDoc.CurrentVersion, rev, at, groups.ToImmutable(), hidden.ToImmutable(), seen.ToImmutable(),
                legacy.ToImmutable(), seeded) { Extra = ExtraOf(root, s_docMembers) };
            return new HomeReadResult(doc, HomeReadFault.None, version);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return Bad();
        }

        static HomeReadResult Bad() => new(HomeDocs.Default(), HomeReadFault.Malformed, 0);
    }

    static HomeItem? ReadItem(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object) return null;
        string? id = Str(it, "id");
        if (string.IsNullOrEmpty(id)) return null;
        if (!it.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object) return null;
        var source = ReadSource(src);
        if (source is null) return null;
        var size = TryParseSize(Str(it, "size"), out var sz) ? sz : HomeSize.Auto;
        var opts = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (it.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) opts[p.Name] = p.Value.GetString() ?? "";
        return new HomeItem(id, source, size, opts.ToImmutable()) { Extra = ExtraOf(it, s_itemMembers), Shape = ReadShape(it) };
    }

    /// <summary>An item's <c>shape</c>: an array of {key, t (the template's name), n, fold}; a member this build cannot read is dropped (the next answer
    /// writes it again).</summary>
    static ImmutableArray<SeedShape> ReadShape(JsonElement it)
    {
        if (!it.TryGetProperty("shape", out var a) || a.ValueKind != JsonValueKind.Array) return ImmutableArray<SeedShape>.Empty;
        var b = ImmutableArray.CreateBuilder<SeedShape>();
        foreach (var e in a.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || Str(e, "key") is not { } key || !Enum.TryParse(Str(e, "t"), out SectionTemplate t)) continue;
            int n = e.TryGetProperty("n", out var nv) && nv.TryGetInt32(out int nn) ? Math.Clamp(nn, 0, 64) : 0;
            bool fold = e.TryGetProperty("fold", out var fv) && fv.ValueKind == JsonValueKind.True;
            b.Add(new SeedShape(key, t, n, fold));
        }
        return b.ToImmutable();
    }

    static HomeSource? ReadSource(JsonElement src)
    {
        string? kind = Str(src, "kind");
        if (string.IsNullOrEmpty(kind)) return null;
        // A known kind with a member this build does not know is kept raw too: rewriting it would drop the member.
        switch (kind)
        {
            case "spotifySection" when OnlyMembers(src, "kind", "key", "fp", "family", "titleHint"):
                return ReadRef(src) is { } r ? new SpotifySectionSource(r, Str(src, "titleHint") ?? "") : null;
            case "spotifyFeed" when OnlyMembers(src, "kind", "facet"):
                return new SpotifyFeedSource(Str(src, "facet") ?? "");
            case "fill" when OnlyMembers(src, "kind", "of", "max"):
                return new FillSource(Str(src, "of") ?? HomeDocs.ShortcutsFill,
                    src.TryGetProperty("max", out var m) && m.TryGetInt32(out int mx) ? Math.Clamp(mx, 0, 64) : HomeDocs.FillMax);
            case "pin" when OnlyMembers(src, "kind", "pinId", "nameHint", "coverHint"):
                return Str(src, "pinId") is { Length: > 0 } pid ? new PinSource(pid, Str(src, "nameHint") ?? "", Str(src, "coverHint") ?? "") : null;
            case "widget" when OnlyMembers(src, "kind", "widget", "args"):
                return Str(src, "widget") is { Length: > 0 } w ? new WidgetSource(w, Str(src, "args")) : null;
            default:
                return new UnknownSource(kind, src.GetRawText());
        }
    }

    static bool OnlyMembers(JsonElement o, params ReadOnlySpan<string> names)
    {
        foreach (var p in o.EnumerateObject())
        {
            bool known = false;
            foreach (var n in names) if (string.Equals(n, p.Name, StringComparison.Ordinal)) { known = true; break; }
            if (!known) return false;
        }
        return true;
    }

    static SectionRef? ReadRef(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? key = Str(e, "key");
        if (key is null) return null;
        ulong fp = ulong.TryParse(Str(e, "fp"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var f) ? f : 0;
        return new SectionRef(key, fp, Str(e, "family") ?? "");
    }

    static ImmutableSortedDictionary<string, string> ExtraOf(JsonElement o, HashSet<string> known)
    {
        ImmutableSortedDictionary<string, string>.Builder? b = null;
        foreach (var p in o.EnumerateObject())
        {
            if (known.Contains(p.Name)) continue;
            b ??= ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            b[p.Name] = p.Value.GetRawText();
        }
        return b?.ToImmutable() ?? ImmutableSortedDictionary<string, string>.Empty;
    }

    static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long Long(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0L;

    // ── write ──

    public static byte[] Write(HomeDoc doc) => Write(doc, indented: true);

    public static byte[] Write(HomeDoc doc, bool indented)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, indented ? s_write : s_compact))
        {
            w.WriteStartObject();
            w.WriteNumber("version", HomeDoc.CurrentVersion);
            w.WriteNumber("rev", doc.Rev);
            w.WriteNumber("updatedAtMs", doc.UpdatedAtMs);
            w.WriteStartArray("groups");
            foreach (var g in doc.Groups)
            {
                w.WriteStartObject();
                w.WriteString("id", g.Id);
                if (g.Title is not null) w.WriteString("title", g.Title);
                if (g.TitleKey is not null) w.WriteString("titleKey", g.TitleKey);
                w.WriteStartArray("items");
                foreach (var it in g.Items) WriteItem(w, it);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("hidden");
            foreach (var r in doc.Hidden) { w.WriteStartObject(); WriteRef(w, r); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("seen");
            foreach (var s in doc.Seen) { w.WriteStartObject(); WriteRef(w, s.Ref); w.WriteNumber("lastSeenMs", s.LastSeenMs); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("legacyHidden");
            foreach (var l in doc.LegacyHidden) w.WriteStringValue(l);
            w.WriteEndArray();
            w.WriteBoolean("seededFromSidebar", doc.SeededFromSidebar);
            foreach (var (name, raw) in doc.Extra) { w.WritePropertyName(name); w.WriteRawValue(raw, skipInputValidation: true); }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The document as indented JSON text (the inspector's <c>home do=doc</c>).</summary>
    public static string WriteText(HomeDoc doc, bool indented = true) => Encoding.UTF8.GetString(Write(doc, indented));

    static void WriteItem(Utf8JsonWriter w, HomeItem it)
    {
        w.WriteStartObject();
        w.WriteString("id", it.Id);
        w.WritePropertyName("source");
        WriteSource(w, it.Source);
        w.WriteString("size", SizeName(it.Size));
        if (it.Options.Count > 0)
        {
            w.WriteStartObject("options");
            foreach (var (k, v) in it.Options) w.WriteString(k, v);
            w.WriteEndObject();
        }
        if (it.Shape.Length > 0)
        {
            w.WriteStartArray("shape");
            foreach (var sh in it.Shape)
            {
                w.WriteStartObject();
                w.WriteString("key", sh.Key);
                w.WriteString("t", sh.Template.ToString());
                w.WriteNumber("n", sh.Count);
                if (sh.Fold) w.WriteBoolean("fold", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        foreach (var (name, raw) in it.Extra) { w.WritePropertyName(name); w.WriteRawValue(raw, skipInputValidation: true); }
        w.WriteEndObject();
    }

    static void WriteSource(Utf8JsonWriter w, HomeSource s)
    {
        if (s is UnknownSource u) { w.WriteRawValue(u.RawJson, skipInputValidation: true); return; }
        w.WriteStartObject();
        w.WriteString("kind", s.Kind);
        switch (s)
        {
            case SpotifySectionSource sec: WriteRef(w, sec.Ref); w.WriteString("titleHint", sec.TitleHint); break;
            case SpotifyFeedSource feed: w.WriteString("facet", feed.Facet); break;
            case FillSource fill: w.WriteString("of", fill.Of); w.WriteNumber("max", fill.Max); break;
            case PinSource pin: w.WriteString("pinId", pin.PinId); w.WriteString("nameHint", pin.NameHint); w.WriteString("coverHint", pin.CoverHint); break;
            case WidgetSource wid: w.WriteString("widget", wid.Widget); if (wid.Args is not null) w.WriteString("args", wid.Args); break;
        }
        w.WriteEndObject();
    }

    static void WriteRef(Utf8JsonWriter w, SectionRef r)
    {
        w.WriteString("key", r.Key);
        w.WriteString("fp", r.Fp.ToString("x16", CultureInfo.InvariantCulture));
        w.WriteString("family", r.Family);
    }

    public static string SizeName(HomeSize s) => s switch
    {
        HomeSize.S => "s", HomeSize.M => "m", HomeSize.L => "l", HomeSize.Full => "full", _ => "auto",
    };

    public static bool TryParseSize(string? s, out HomeSize size)
    {
        switch (s)
        {
            case "auto": size = HomeSize.Auto; return true;
            case "s": size = HomeSize.S; return true;
            case "m": size = HomeSize.M; return true;
            case "l": size = HomeSize.L; return true;
            case "full": size = HomeSize.Full; return true;
            default: size = HomeSize.Auto; return false;
        }
    }
}
