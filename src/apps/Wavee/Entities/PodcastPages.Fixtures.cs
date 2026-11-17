// ── Entities/PodcastPages.Fixtures.cs — the podcast/audiobook pages' redacted fixtures (--fake / --inspect only) ──────
//
// Role: SHELL (UI thread)
// Lane: podcast-pages-data (2026-11-17). Files: assets/spotify/fixtures/podcast-pages/<name>.json, written by
// tools/podcast-fixture.py from the captures (its docstring names every source session) and leak-checked by it.
//
// A fixture is the captured extended-metadata ANSWERS for one show and its episodes, so loading one runs the real
// decoders (Spotify.Decode.ExtendedMetadata → the show/episode tables), plus the membership order, an optional
// similarAudiobooks answer, an optional capping answer, and a SYNTHETIC `state` block (resume points, the finished set,
// saved, the last visit): never the owner's own listening. The inspector verb `podcastpages fixture=<name>` loads it and
// dumps the view model; `goto uri=<show>` then opens its page.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Wavee;

public static partial class PodcastPages
{
    public static readonly string[] FixtureNames =
        ["show-episodic", "show-serial", "book-titled", "book-tracks", "book-inprogress", "book-finished"];

    static readonly Dictionary<string, bool> s_savedOverride = new(StringComparer.Ordinal);

    /// <summary>Load one fixture into the current (fake) scope. Returns the show, or an invalid one with
    /// <paramref name="error"/> set. Fake mode only: a live scope is never seeded from a file.</summary>
    public static Show LoadFixture(string name, out string error)
    {
        error = "";
        if (!Platform.Args.Fake) { error = "fixtures load only under --fake/--inspect"; return default; }
        if (Array.IndexOf(FixtureNames, name) < 0) { error = "unknown fixture (" + string.Join("|", FixtureNames) + ")"; return default; }
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "spotify", "fixtures", "podcast-pages", name + ".json");
        if (!File.Exists(path)) { error = "missing " + path; return default; }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        string showUri = root.GetProperty("show").GetString() ?? "";
        var items = new List<string>();
        foreach (var i in root.GetProperty("items").EnumerateArray()) items.Add(i.GetString() ?? "");

        // 1. catalogue: the captured answers through the real decoders, and the membership in the captured order.
        var s = Staging.Rent();
        s.Epoch = Entities.Current.Epoch;
        if (root.TryGetProperty("xm", out var xm))
            foreach (var b64 in xm.EnumerateArray())
                Spotify.Decode.ExtendedMetadata(Convert.FromBase64String(b64.GetString() ?? ""), s);
        var showId = new StagedId(EntityId.Parse(showUri.AsSpan()));
        var run = s.Run(Relation.ShowEpisodes);
        foreach (var uri in items) run.Add(new StagedId(EntityId.Parse(uri.AsSpan())));
        run.End(showId, EdgeState.Complete, items.Count);
        if (root.TryGetProperty("pathfinder", out var pf) && pf.TryGetProperty("similarAudiobooks", out var similar))
        {
            byte[] json = System.Text.Encoding.UTF8.GetBytes(similar.GetRawText());
            Spotify.Decode.SimilarAudiobooks(json, s);
            Spotify.Books.Put("similar", showUri, Spotify.Books.DecodeSimilar(json));
        }
        Entities.Commit(s);
        Staging.Return(s);

        // 2. the synthetic user state, over the committed durations.
        var scope = Entities.Current;
        var state = root.TryGetProperty("state", out var st) ? st : default;
        var p = Staging.Rent();
        p.Epoch = scope.Epoch;
        int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var finished = new HashSet<string>(StringComparer.Ordinal);
        if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("finished", out var fin))
            foreach (var f in fin.EnumerateArray()) finished.Add(f.GetString() ?? "");
        var resume = new Dictionary<string, int>(StringComparer.Ordinal);
        if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("resume", out var res))
            foreach (var r in res.EnumerateObject()) resume[r.Name] = r.Value.GetInt32();
        int order = 0;
        foreach (var uri in items)
        {
            order++;
            bool done = finished.Contains(uri);
            bool started = resume.TryGetValue(uri, out int at);
            if (!done && !started) continue;
            if (!scope.Episodes.TryGetSlot(uri.AsSpan(), out int slot)) continue;
            int duration = scope.Episodes.DurationMs[slot];
            ref var row = ref p.Episodes.RowFor(new StagedId(EntityId.Parse(uri.AsSpan())), Authority.Local, (uint)EpisodeFields.Progress);
            row.ProgressMs = done ? EpisodeProgress.Marked(true, duration) : at;
            row.PlayedAt = now - (items.Count - order) * 3600;      // later in the list = played later
            p.Episodes.Settle();
        }
        Entities.Commit(p);
        Staging.Return(p);
        Entities.Publish();

        if (state.ValueKind == JsonValueKind.Object)
        {
            if (state.TryGetProperty("saved", out var saved)) s_savedOverride[showUri] = saved.ValueKind == JsonValueKind.True;
            if (state.TryGetProperty("lastVisitUnixMs", out var visit)) PinVisit(showUri, visit.GetInt64());
        }
        if (root.TryGetProperty("capping", out var capping) && capping.GetString() is { Length: > 0 } cap)
            Spotify.Books.AllowanceSignal.Value = Spotify.Books.DecodeAllowance(Convert.FromBase64String(cap));

        return scope.Shows.TryGetSlot(showUri.AsSpan(), out int showSlot) ? new Show(showSlot) : default;
    }

    /// <summary>A fixture's stated save, which wins over the fake library (fake mode only; empty otherwise).</summary>
    static bool TryFixtureSaved(string uri, out bool saved)
    {
        saved = false;
        return Platform.Args.Fake && s_savedOverride.TryGetValue(uri, out saved);
    }

    /// <summary>The page's snapshot as JSON (the inspector verb's reply body, and a diag dump).</summary>
    public static void WriteDump(Utf8JsonWriter w, Show show)
    {
        if (show.IsAudiobook)
        {
            var page = AudiobookOf(show);
            w.WriteString("kind", "audiobook");
            w.WritePropertyName("page");
            WriteValue(w, Projection(page));
        }
        else
        {
            var page = ShowOf(show);
            w.WriteString("kind", "show");
            w.WritePropertyName("page");
            WriteValue(w, Projection(page));
            var first = page.Episodes.FirstOrDefault();
            if (first is not null)
            {
                w.WritePropertyName("episode");
                WriteValue(w, Projection(EpisodeOf(first.Episode)));
            }
        }
        var allowance = Spotify.Books.AllowanceSignal.Peek();
        w.WritePropertyName("allowance");
        if (allowance is null) w.WriteNullValue();
        else WriteValue(w, new Dictionary<string, object>
        {
            ["allowanceMs"] = allowance.AllowanceMs, ["periodStart"] = allowance.PeriodStart.ToString("O"),
            ["periodEnd"] = allowance.PeriodEnd.ToString("O"), ["refill"] = allowance.Refill.ToString("O"),
            ["quotaValidTo"] = allowance.QuotaValidTo.ToString("O"), ["entitlement"] = allowance.Entitlement,
        });
    }

    static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string str: w.WriteStringValue(str); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case byte by: w.WriteNumberValue(by); break;
            case float f: w.WriteNumberValue(Math.Round(f, 4)); break;
            case double d: w.WriteNumberValue(d); break;
            case Dictionary<string, object> map:
                w.WriteStartObject();
                foreach (var (k, v) in map) { w.WritePropertyName(k); WriteValue(w, v); }
                w.WriteEndObject();
                break;
            case object[] list:
                w.WriteStartArray();
                foreach (var v in list) WriteValue(w, v);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(value.ToString()); break;
        }
    }

    // Entity handles serialize as their uri; long lists are cut so a dump stays readable.
    static object Projection(object? value) => value switch
    {
        null => "",
        Show s => s.IsValid ? s.Uri.Text : "",
        Episode e => e.IsValid ? e.Uri.Text : "",
        string or bool or int or long or float or double or byte or Enum => value is Enum en ? en.ToString() : value,
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        System.Collections.IEnumerable list => list.Cast<object?>().Take(60).Select(Projection).ToArray(),
        _ => Members(value),
    };

    /// <summary>A page record as its public properties, read by reflection. A published build trims fully (NativeAOT, TrimMode=full), so every
    /// record a page model can hold is declared to the trimmer here. A record type missing from this list dumps as an empty object in a
    /// published build (a Debug build reads it anyway): add it when the page models gain one.</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(ShowPage))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(EpisodePage))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(AudiobookPage))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(EpisodeRow))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(ChapterRow))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(ChapterToc))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(BookProgress))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(BookPreview))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(BookPrice))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(BookAccess))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Money))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(GenreCrumb))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Person))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Rating))]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Every record a page model holds is kept by the DynamicDependency attributes on this method.")]
    static Dictionary<string, object> Members(object value)
        => value.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .ToDictionary(p => p.Name, p => Projection(p.GetValue(value)) ?? "");
}
