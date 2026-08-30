using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// Preview tracks for the home Featured editorial cards (the hover peek): one BATCHED feedBaselineLookup pathfinder
// query per home load resolves every featured playlist's 5 preview tracks (name + cover + 30s MP3 url), exactly the
// call the web player fires alongside its home. Display-only cache — never touches the Store; keyed by playlist uri;
// cleared implicitly on process exit (a home's preview set is stable for the session).
//
// Flow: LiveSessionHost installs the PathfinderResource at go-live; HomePage Primes the featured uris when the feed
// lands; the EditorialCard reads For(uri) and subscribes Epoch so the peek appears the moment the batch returns.
static class HomeBaselinePreviews
{
    const int MaxBatch = 20;   // the web player batches ~20 uris per lookup

    static readonly object Gate = new();
    static readonly Dictionary<string, IReadOnlyList<HomePreviewTrack>> Cache = new();
    static readonly HashSet<string> Pending = new();
    static PathfinderResource? _pf;

    /// <summary>Bumped whenever a batch lands — a component that read <see cref="For"/> subscribes this to re-render.</summary>
    public static readonly Signal<int> Epoch = new(0);

    public static void Install(PathfinderResource pf) { lock (Gate) _pf = pf; }

    /// <summary>Drop the session's Pathfinder resource (and the previews it fetched). Called from the go-live wiring's
    /// teardown: the resource carries the live session's auth, so a Prime() after logout would fault against a dead
    /// pipeline, and the cached previews belong to that account.</summary>
    public static void Uninstall()
    {
        lock (Gate)
        {
            _pf = null;
            Cache.Clear();
            Pending.Clear();
        }
    }

    public static IReadOnlyList<HomePreviewTrack>? For(string uri)
    {
        lock (Gate) return Cache.TryGetValue(uri, out var t) ? t : null;
    }

    /// <summary>Fire-and-forget: batch-fetch previews for every uri not yet cached or in flight. Idempotent — a
    /// re-rendered home re-Primes for free.</summary>
    public static void Prime(IEnumerable<string> uris)
    {
        List<string>? missing = null;
        PathfinderResource? pf;
        lock (Gate)
        {
            pf = _pf;
            if (pf is null) return;
            foreach (var u in uris)
            {
                if (u.Length == 0 || Cache.ContainsKey(u) || Pending.Contains(u)) continue;
                (missing ??= new List<string>()).Add(u);
                Pending.Add(u);
            }
        }
        if (missing is null) return;
        for (int i = 0; i < missing.Count; i += MaxBatch)
        {
            var batch = missing.Skip(i).Take(MaxBatch).ToArray();
            _ = FetchBatchAsync(pf!, batch);
        }
    }

    static async Task FetchBatchAsync(PathfinderResource pf, IReadOnlyList<string> uris)
    {
        try
        {
            using var doc = await pf.QueryAsync(PathfinderOps.FeedBaselineLookup, PathfinderOps.FeedBaselineLookupHash,
                w =>
                {
                    w.WritePropertyName("uris");
                    w.WriteStartArray();
                    foreach (var u in uris) w.WriteStringValue(u);
                    w.WriteEndArray();
                }, PathfinderClient.Platform.WebPlayer).ConfigureAwait(false);

            bool any = false;
            lock (Gate)
            {
                foreach (var u in uris) Pending.Remove(u);
                if (doc is not null &&
                    SpotifyExportMapper.Dig(doc.RootElement, "data", "lookup") is { ValueKind: JsonValueKind.Array } lookup)
                {
                    foreach (var wrap in lookup.EnumerateArray())
                    {
                        string? uri = Str(wrap, "_uri");
                        if (uri is null) continue;
                        var tracks = ParseTracks(wrap);
                        if (tracks.Count > 0) { Cache[uri] = tracks; any = true; }
                    }
                }
            }
            if (any) Epoch.Value = Epoch.Peek() + 1;
        }
        catch
        {
            lock (Gate) foreach (var u in uris) Pending.Remove(u);
        }
    }

    static IReadOnlyList<HomePreviewTrack> ParseTracks(JsonElement wrap)
    {
        var outTracks = new List<HomePreviewTrack>(5);
        var items = SpotifyExportMapper.Dig(wrap, "data", "previewItems", "items");
        if (items.ValueKind != JsonValueKind.Array) return outTracks;
        foreach (var it in items.EnumerateArray())
        {
            var d = SpotifyExportMapper.Dig(it, "data");
            if (d.ValueKind != JsonValueKind.Object) continue;
            string? uri = Str(d, "uri");
            string? name = Str(d, "name");
            if (uri is null || name is null) continue;

            // Smallest usable cover (the peek row is ~36px; the 64px source decodes cheapest).
            string? cover = null;
            var sources = SpotifyExportMapper.Dig(d, "albumOfTrack", "coverArt", "sources");
            if (sources.ValueKind == JsonValueKind.Array)
            {
                int best = int.MaxValue;
                foreach (var s in sources.EnumerateArray())
                {
                    string? url = Str(s, "url");
                    if (url is null) continue;
                    int h = s.TryGetProperty("height", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 300;
                    if (h < best) { best = h; cover = url; }
                }
            }

            string? previewUrl = null;
            var previews = SpotifyExportMapper.Dig(d, "previews", "audioPreviews", "items");
            if (previews.ValueKind == JsonValueKind.Array)
                foreach (var p in previews.EnumerateArray()) { previewUrl = Str(p, "url"); if (previewUrl is not null) break; }

            outTracks.Add(new HomePreviewTrack(uri, name, cover is null ? null : new Image(cover), previewUrl));
        }
        return outTracks;
    }

    static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
