using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Hydration;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Core;
using EntityKind = Wavee.Core.EntityKind;   // disambiguate: Wavee.Backend.Metadata has its own PERSISTED kind enum; this file speaks the ROUTING one

namespace Wavee.SpotifyLive;

// LIVE library/playlist round-trips — the L1 acceptance probes. Each builds the real spclient pipeline, runs a fetcher
// (the same code the app uses), and prints the result. Needs creds + network, so the USER runs them:
//   --spotify-playlist spotify:playlist:<id>   --spotify-rootlist   --spotify-collection [liked|albums|artists|shows|episodes]
public static class SpotifyLibraryProbe
{
    public static async Task<int> RunPlaylistAsync(string uri, WaveeLogger log, CancellationToken ct, string language = "en")
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, language: language).ConfigureAwait(false);
        if (live is null) return 1;

        var store = new InMemoryStore();
        var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, CatalogHydrate(live, store, log), () => live.Username);

        log.Info("Fetching playlist " + uri + " ...");
        try { await fetcher.FetchPlaylistAsync(uri, ct).ConfigureAwait(false); }
        catch (Exception ex) { log.Info("playlist fetch failed: " + ex.Message); return 1; }

        var membership = store.Membership(uri);
        var rev = store.PlaylistRevision(uri);
        var header = store.GetPlaylist(uri);
        log.Info("  name: " + (header?.Name ?? "(none)") + "   revision: " + (rev is null ? "(none)" : System.Convert.ToHexString(rev)));
        log.Info("  " + membership.Count + " items:");
        for (int i = 0; i < membership.Count; i++)
        {
            if (i >= 50) { log.Info("    ... (" + (membership.Count - 50) + " more)"); break; }
            var m = membership[i];
            var t = store.GetTrack(m.ItemUri);
            string by = m.AddedBy is { Length: > 0 } a ? "  (added by " + a + ")" : "";
            log.Info("    " + (i + 1) + ". " + (t is { } tt ? tt.Title + " - " + string.Join(", ", tt.Artists.Select(x => x.Name)) : m.ItemUri) + by);
        }
        return 0;
    }

    public static async Task<int> RunRootlistAsync(WaveeLogger log, CancellationToken ct, string language = "en")
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, language: language).ConfigureAwait(false);
        if (live is null) return 1;

        var store = new InMemoryStore();
        var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, (uris, c) => Task.CompletedTask, () => live.Username);   // rootlist items are playlist uris

        string rootlistUri = "spotify:user:" + live.Username + ":rootlist";
        log.Info("Fetching rootlist " + rootlistUri + " ...");
        try { await fetcher.FetchRootlistAsync(rootlistUri, ct).ConfigureAwait(false); }
        catch (Exception ex) { log.Info("rootlist fetch failed: " + ex.Message); return 1; }

        var rl = store.Rootlist();
        log.Info("  " + rl.Count + " rootlist entries:");
        foreach (var e in rl)
        {
            string indent = new string(' ', 4 + System.Math.Max(0, e.Depth) * 2);
            string label = e.Kind == 1 ? "[folder] " + (e.GroupName ?? "") : e.Kind == 2 ? "[/folder]" : e.Uri;
            log.Info(indent + label);
        }
        return 0;
    }

    public static async Task<int> RunCollectionAsync(string setId, WaveeLogger log, CancellationToken ct, string language = "en")
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, language: language).ConfigureAwait(false);
        if (live is null) return 1;

        var store = new InMemoryStore();
        var revs = new Dictionary<string, string?>();
        var fetcher = new CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, store,
            s => revs.TryGetValue(s, out var r) ? r : null, (s, r) => revs[s] = r, CatalogHydrate(live, store, log));

        log.Info("Fetching collection set '" + setId + "' ...");
        try { await fetcher.FetchSetAsync(setId, ct).ConfigureAwait(false); }
        catch (Exception ex) { log.Info("collection fetch failed: " + ex.Message); return 1; }

        var items = store.SavedUris(setId);
        log.Info("  " + items.Count + " items in '" + setId + "' (sync token " + (revs.GetValueOrDefault(setId) ?? "none") + "):");
        for (int i = 0; i < items.Count; i++)
        {
            if (i >= 50) { log.Info("    ... (" + (items.Count - 50) + " more)"); break; }
            log.Info("    " + (i + 1) + ". " + PrintItem(items[i], store));
        }
        return 0;
    }

    /// <summary>The fetchers' hydrate delegate, built exactly as go-live builds it: THE catalogue arm (one mixed-kind
    /// extended-metadata POST per 300 uris, etag-conditional) rather than a probe-only transport. The membership rows a
    /// fetcher lands need Identity facts, which is precisely what this arm writes (hydration-facade-design.md §2.2).</summary>
    static Func<IReadOnlyList<string>, CancellationToken, Task> CatalogHydrate(LiveSpclient live, IStore store, WaveeLogger log)
    {
        var source = new ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);
        var catalog = new XmCatalogFetch(new ExtensionEtagCache(source, () => live.Session, log), store, log);
        return async (uris, ct) =>
        {
            var refs = new List<EntityUri>(uris.Count);
            for (int i = 0; i < uris.Count; i++) refs.Add(EntityUri.Parse(uris[i]));
            await catalog.FetchAsync(refs, null, TraitSurface.None, ct).ConfigureAwait(false);
        };
    }

    static string PrintItem(string uri, IStore store) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Track => store.GetTrack(uri) is { } t ? t.Title + " - " + string.Join(", ", t.Artists.Select(a => a.Name)) : uri,
        EntityKind.Album => store.GetAlbum(uri)?.Name ?? uri,
        EntityKind.Artist => store.GetArtist(uri)?.Name ?? uri,
        EntityKind.Show => store.GetShow(uri)?.Name ?? uri,
        EntityKind.Episode => store.GetEpisode(uri)?.Title ?? uri,
        _ => uri,
    };
}
