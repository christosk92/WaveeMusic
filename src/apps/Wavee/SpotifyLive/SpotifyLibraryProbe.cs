using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Metadata;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
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

        var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username);

        log.Info("Fetching playlist " + uri + " ...");
        PlaylistReadResult read;
        try { read = await fetcher.FetchPlaylistAsync(uri, ct).ConfigureAwait(false); }
        catch (Exception ex) { log.Info("playlist fetch failed: " + ex.Message); return 1; }

        var membership = read.Members;
        var rev = read.Revision;
        var header = read.Header;
        log.Info("  name: " + (header?.Name ?? "(none)") + "   revision: " + (rev is null ? "(none)" : System.Convert.ToHexString(rev)));
        log.Info("  " + membership.Length + " items:");
        for (int i = 0; i < membership.Length; i++)
        {
            if (i >= 50) { log.Info("    ... (" + (membership.Length - 50) + " more)"); break; }
            var m = membership[i];
            string by = m.AddedBy is { Length: > 0 } a ? "  (added by " + a + ")" : "";
            log.Info("    " + (i + 1) + ". " + m.ItemUri + by);
        }
        return 0;
    }

    public static async Task<int> RunRootlistAsync(WaveeLogger log, CancellationToken ct, string language = "en")
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, language: language).ConfigureAwait(false);
        if (live is null) return 1;

        var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username);   // rootlist items are playlist uris

        string rootlistUri = "spotify:user:" + live.Username + ":rootlist";
        log.Info("Fetching rootlist " + rootlistUri + " ...");
        RootlistReadResult read;
        try { read = await fetcher.FetchRootlistAsync(rootlistUri, ct).ConfigureAwait(false); }
        catch (Exception ex) { log.Info("rootlist fetch failed: " + ex.Message); return 1; }

        var rl = read.Entries;
        log.Info("  " + rl.Length + " rootlist entries:");
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

        var fetcher = new CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, log);

        // The fetcher speaks WIRE sets (one walk of "collection" carries both liked and albums); the probe takes the
        // logical name the user knows and prints that set's members. The token is the wire set's.
        string wireSet = CollectionSets.WireSet(setId);
        if (CollectionSets.LogicalSetsForWireSet(wireSet).Count == 0) { log.Info("unknown collection set '" + setId + "'"); return 2; }
        log.Info("Fetching collection set '" + setId + "' (wire set '" + wireSet + "') ...");
        CollectionReadResult read;
        try { read = await fetcher.FetchWireSetAsync(wireSet, null, ct).ConfigureAwait(false); }
        catch (Exception ex) { log.Info("collection fetch failed: " + ex.Message); return 1; }

        var items = read.Items.Where(x => !x.Removed && CollectionSets.LogicalSetForItem(wireSet, x.Uri) == setId).Select(x => x.Uri).ToArray();
        log.Info("  " + items.Length + " items in '" + setId + "' (sync token " + (read.Token ?? "none") + "):");
        for (int i = 0; i < items.Length; i++)
        {
            if (i >= 50) { log.Info("    ... (" + (items.Length - 50) + " more)"); break; }
            log.Info("    " + (i + 1) + ". " + items[i]);
        }
        return 0;
    }

}
