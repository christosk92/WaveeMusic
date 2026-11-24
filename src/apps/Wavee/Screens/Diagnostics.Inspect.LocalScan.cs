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
        /// <summary>`state kind=music-folders value=none|scanning|ready|offline|changed|missing|listing|paused`.</summary>
        string MusicFoldersState(int id, string name, Inspect.Args a)
        {
            string v = a.Has("value") ? a.Str("value").ToLowerInvariant() : "ready";
            if (v is not ("none" or "scanning" or "ready" or "offline" or "changed" or "missing" or "listing" or "paused")) return Inspect.Reply.Fail(id, name, "state kind=music-folders wants value=none|scanning|ready|offline|changed|missing|listing|paused, not '" + v + "'");
            LocalLibrary.InspectFixture(v);
            LocalCollectionFixture.Fill(v);   // #240 P2-page: the "On this PC" collection on top (albums, years, formats)
            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
            return Settled(id, name, a, w =>
            {
                w.WriteString("kind", "music-folders");
                w.WriteString("value", v);
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
