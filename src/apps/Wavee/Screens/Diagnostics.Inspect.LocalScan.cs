// ── Screens/Diagnostics.Inspect.LocalScan.cs ────────────────────────────────────────────────────────────────────────
// the local library in the inspector (#240): `state kind=music-folders value=none|scanning|ready|offline|changed` seeds the music
// folders and the index in memory; `localscan path=<folder>` runs the real scanner on a folder against a private index
//
// Role: SHELL (UI thread; the verb blocks the server while it scans, like `perf`)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §5 "Fixtures first", §3.6 "Targets"
//
// The state seeds `LocalLibrary` with a fixed folder list and index (no disk, no thread, no setting): P1b draws nothing from
// it yet; P2's Settings ▸ Storage and the Local Files list read it. The verb is the probe's runner without the persist step
// and the change test (those need a temp folder: `Wavee.exe --local-scan`); it never touches the app's own index.

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`state kind=music-folders value=none|scanning|ready|offline|changed|missing|listing|paused|noaccess|checking|network-online|network-waking|network-slow|network-signin|network-offline|folder` (folder: with path=).</summary>
        string MusicFoldersState(int id, string name, Inspect.Args a)
        {
            string v = a.Has("value") ? a.Str("value").ToLowerInvariant() : "ready";
            string scanned = "";
            if (v == "folder")
            {
                // `value=folder path=<a folder of music>`: the real scanner over real files (#240 P4's empirical matching).
                string path = a.Str("path");
                if (path.Length == 0 || !Directory.Exists(path)) return Inspect.Reply.Fail(id, name, "state kind=music-folders value=folder wants path=<a folder of music>, not '" + path + "'");
                scanned = LocalLibrary.InspectFolder(path);
            }
            else if (v is not ("none" or "scanning" or "ready" or "offline" or "changed" or "missing" or "listing" or "paused" or "noaccess" or "checking"
                or "network-online" or "network-waking" or "network-slow" or "network-signin" or "network-offline"))
                return Inspect.Reply.Fail(id, name, "state kind=music-folders wants value=none|scanning|ready|offline|changed|missing|listing|paused|noaccess|checking|network-online|network-waking|network-slow|network-signin|network-offline|folder, not '" + v + "'");
            if (v != "folder")
            {
                LocalLibrary.InspectFixture(v);
                LocalCollectionFixture.Fill(v);   // #240 P2-page: the "On this PC" collection on top (albums, years, formats)
            }
            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
            return Settled(id, name, a, w =>
            {
                w.WriteString("kind", "music-folders");
                w.WriteString("value", v);
                if (scanned.Length > 0) w.WriteString("scan", scanned);
                w.WriteString("library", LocalLibrary.StateWord);
                w.WriteNumber("folders", LocalLibrary.Folders.Count);
                w.WriteNumber("entries", LocalLibrary.Index.Count);
                w.WriteNumber("unplayable", LocalLibrary.Index.UnplayableCount);
                w.WriteNumber("missing", LocalLibrary.Index.MissingCount);
                w.WriteNumber("cloud", LocalLibrary.Index.CloudOnlyCount);
                LocalMatchResult m = LocalLibrary.Index.Match("Says", "Nils Frahm", "Spaces", 211);
                w.WriteString("matchSays", m.Entry is { } e ? "tier" + m.Tier + " id=" + e.LocalId : "none");
                w.WriteBoolean("firstScan", LocalLibrary.FirstScanRunning);
                w.WriteNumber("songs", LocalLibrary.SongCount);
            });
        }

        /// <summary>`state kind=spotify-match value=off|offer|matching|review|focus|accepted` ("Your files and Spotify", LocalLink.Fixture.cs).</summary>
        string SpotifyMatchState(int id, string name, Inspect.Args a)
        {
            string v = a.Has("value") ? a.Str("value").ToLowerInvariant() : "review";
            if (Array.IndexOf(LocalLink.FixtureValues.Split('|'), v) < 0)
                return Inspect.Reply.Fail(id, name, "state kind=spotify-match wants value=" + LocalLink.FixtureValues + ", not '" + v + "'");
            string why = LocalLink.InspectFixture(v);
            if (why.Length > 0) return Inspect.Reply.Fail(id, name, why);
            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
            return Settled(id, name, a, w =>
            {
                var view = LocalLink.View.Peek();
                w.WriteString("kind", "spotify-match");
                w.WriteString("value", v);
                w.WriteString("phase", view.Phase.ToString().ToLowerInvariant());
                w.WriteNumber("groups", view.Groups.Count);
                w.WriteNumber("exact", view.Exact);
                w.WriteNumber("strong", view.Strong);
                w.WriteNumber("likely", view.Likely);
                w.WriteNumber("check", view.Check);
                w.WriteNumber("accepted", view.Accepted);
                w.WriteString("reviewing", LocalLink.Reviewing.Peek() ?? "");
                w.WriteStartArray("evidence");
                foreach (var g in view.Groups)
                    w.WriteStringValue(g.Key + " | " + g.Confidence + " | " + (g.Best is { } b ? LocalLinkWords.Evidence(b, g.IsAlbum) + " | " + b.Uri : "none"));
                w.WriteEndArray();
                // The asks matching made, with the pathfinder bodies a real session posts (`Spotify.Api.SearchBody`).
                w.WriteStartArray("requests");
                foreach (string r in LocalLink.Requests) w.WriteStringValue(r);
                w.WriteEndArray();
            });
        }

        /// <summary>`localscan path=<folder> runs=N`.</summary>
        string LocalScanVerb(int id, string name, Inspect.Args a)
        {
            string path = a.Str("path");
            if (path.Length == 0 || !Directory.Exists(path)) return Inspect.Reply.Fail(id, name, "localscan wants path=<a folder of music>, not '" + path + "'");
            var lines = new List<string>();
            LocalScanProbe.Run(path, workDir: null, runs: a.Int("runs", 3), changeTest: false, lines.Add);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteStartArray("lines");
                foreach (string l in lines) w.WriteStringValue(l);
                w.WriteEndArray();
            });
        }
    }
}
