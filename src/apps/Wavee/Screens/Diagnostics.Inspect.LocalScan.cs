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
        /// <summary>`state kind=music-folders value=none|scanning|ready|offline|changed|missing|listing|paused|noaccess|checking|network-online|network-waking|network-slow|network-signin|network-offline|folder|startup` (folder and startup: with path=).</summary>
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
            else if (v == "startup")
            {
                // `value=startup path=<a folder of music>`: the app's own start-up (LocalLibrary.Use) over the scratch profile's index file, on its
                // threads; `state kind=rescope` after it is the login's scope switch. The rows land over the next frames.
                string path = a.Str("path");
                if (path.Length == 0 || !Directory.Exists(path)) return Inspect.Reply.Fail(id, name, "state kind=music-folders value=startup wants path=<a folder of music>, not '" + path + "'");
                if (LocalLibrary.InspectStartup(path) is not { } index)
                    return Inspect.Reply.Fail(id, name, "state kind=music-folders value=startup runs once per process, before any other music-folders state");
                scanned = "startup index=" + index;
            }
            else if (v is not ("none" or "scanning" or "ready" or "offline" or "changed" or "missing" or "listing" or "paused" or "noaccess" or "checking"
                or "network-online" or "network-waking" or "network-slow" or "network-signin" or "network-offline"))
                return Inspect.Reply.Fail(id, name, "state kind=music-folders wants value=none|scanning|ready|offline|changed|missing|listing|paused|noaccess|checking|network-online|network-waking|network-slow|network-signin|network-offline|folder|startup, not '" + v + "'");
            if (v is not ("folder" or "startup"))
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

        /// <summary>`state kind=local-peek value=counting|landed|empty|capped|none`: what "Find music on this PC" counted (#240, 0dm), no disk: with
        /// `state kind=music-folders value=none` first (the card shows while no folder is set). `trace=` on `landed` proves the card keeps still.</summary>
        string LocalPeekState(int id, string name, Inspect.Args a)
        {
            string v = a.Has("value") ? a.Str("value").ToLowerInvariant() : "landed";
            if (v is not ("counting" or "landed" or "empty" or "capped" or "none"))
                return Inspect.Reply.Fail(id, name, "state kind=local-peek wants value=counting|landed|empty|capped|none, not '" + v + "'");
            LocalLibrary.InspectPeek(v);
            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
            return Settled(id, name, a, w =>
            {
                w.WriteString("kind", "local-peek");
                w.WriteString("value", v);
                w.WriteStartArray("rows");
                foreach (var r in LocalLibrary.Peek.Peek()) w.WriteStringValue(r.Key + " songs=" + r.Songs + " cloud=" + r.Cloud + " videos=" + r.Videos + " done=" + r.Done);
                w.WriteEndArray();
            });
        }

        /// <summary>`state kind=linked-video value=attach|seed-legacy|migrate|unlink|list uri= path=`: a Spotify track's video version (one concept:
        /// an attached video is a library file linked to the track; Shell/Video.LinkedVideos.cs). The reply lists every linked video, the track's
        /// row bit, the video host's file for it and whether the old store still holds anything.</summary>
        string LinkedVideoState(int id, string name, Inspect.Args a)
        {
            string v = a.Has("value") ? a.Str("value").ToLowerInvariant() : "list";
            string uri = a.Str("uri"), path = a.Str("path");
            string full = path.Length > 0 ? Path.GetFullPath(path) : "";
            switch (v)
            {
                case "attach":
                    if (Video.LinkedVideos.Attach(uri, full, full, 0) is null) return Inspect.Reply.Fail(id, name, "the attach was refused (a Spotify track uri= and an .mp4 path=)");
                    break;
                case "seed-legacy": Video.LinkedVideos.SeedLegacy(uri, full); break;
                case "migrate": Video.LinkedVideos.MigrateLegacy(); break;
                case "unlink": if (!Video.LinkedVideos.Remove(uri)) return Inspect.Reply.Fail(id, name, "the track has no linked video"); break;
                case "default":   // "Play by default" on the track's video version (the drawer's and the menu's radio item)
                    if (LocalLink.VideoFileOf(uri) is var video and not 0) LocalLink.SetDefault(uri, video);
                    else return Inspect.Reply.Fail(id, name, "the track has no linked video");
                    break;
                case "list": break;
                default: return Inspect.Reply.Fail(id, name, "state kind=linked-video wants value=attach|seed-legacy|migrate|unlink|default|list");
            }
            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
            return Settled(id, name, a, w =>
            {
                w.WriteString("kind", "linked-video");
                w.WriteString("value", v);
                w.WriteStartArray("linked");
                foreach (var r in Video.LinkedVideos.All())
                {
                    var tid = EntityId.Parse(r.Uri.AsSpan());
                    var row = Entities.Track(tid);
                    w.WriteStringValue(r.Uri + " | " + Path.GetFileName(r.Path) + " | " + Video.LinkedVideos.Decide(r.Uri).Tier + " | rowHasVideo=" + row.HasVideo
                        + " | host=" + (Playback.Video.LocalVideos.PathOf(tid) is { } p ? Path.GetFileName(p) : "none"));
                }
                w.WriteEndArray();
                if (uri.Length > 0)
                {
                    var tid = EntityId.Parse(uri.AsSpan());
                    w.WriteBoolean("trackHasVideo", Entities.Track(tid).HasVideo);
                    w.WriteString("trackHost", Playback.Video.LocalVideos.PathOf(tid) ?? "");
                    w.WriteNumber("trackVersions", LocalLink.FilesFor(uri).Count);
                    w.WriteBoolean("playsAsVideo", LocalLink.PlaysAsVideo(tid));
                    var row = Entities.Track(tid);
                    w.WriteBoolean("deckLocalVideoRow", Playback.IsLocalVideoRow(new EntityRef(EntityKind.Track, row.Slot)));
                }
                w.WriteBoolean("legacyStoreEmpty", Platform.Settings.Get(new SettingKey<string>("video.overrides.v1", "")).Length == 0);
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
                // Phase 2: where the fake "Savin' Me" plays from (--fake plays never open a source, so the route is answered here).
                var savin = EntityUri.Parse(LocalLink.FixtureCopy != 0 && LocalLink.LinkOf(LocalLink.FixtureCopy) is { } fl ? fl.Uri : "spotify:track:nb0n3").Id;   // the fixture copy's song, else the fake Savin' Me
                long playsAs = LocalLibrary.PlayAsOf(savin);
                w.WriteString("savinRoute", Playback.Audio.RouteOf(savin).ToString());
                w.WriteNumber("savinPlaysAs", playsAs);
                w.WriteString("savinPath", LocalLibrary.PathOf(savin) ?? "");
                w.WriteString("savinReportAs", playsAs != 0 && LocalLibrary.LocalUriOfFile(playsAs) is { } own ? own.Uri : "");
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

        /// <summary>`localindex prefix=<relative path start> [rename=<LocalId> title=<name>]`: the app's index as it is now (#240 folder management
        /// checks: a rename keeps every LocalId, a recycled folder's files turn Missing, a hidden folder's leave; LocalId safety: `next`, the id
        /// mark kept outside the bin, and each file's Wavee rename). `rename=` renames a file in Wavee first, as the drawer's Rename does.</summary>
        string LocalIndexVerb(int id, string name, Inspect.Args a)
        {
            string prefix = a.Str("prefix");
            var index = LocalLibrary.Index;
            if (a.Has("rename"))
            {
                long target = a.Int("rename", 0);
                if (!index.TryGet(target, out _)) return Inspect.Reply.Fail(id, name, "localindex rename=<LocalId> wants an indexed file, not " + target);
                LocalLink.Rename(target, new LocalRename(a.Str("title"), "", ""));
                RunFrames(5);
            }
            if (a.Bool("compact", false)) index.Compact();
            // `mark=`: the id high-water mark as a previous run left it (an inspector's settings live in memory), before a `value=startup`.
            if (a.Has("mark")) Platform.Settings.Set(Platform.Keys.MusicIdMark, (long)a.Int("mark", 0));   // write the bin now (the app compacts when the scanner has been idle 30 s)
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteNumber("entries", index.Count);
                w.WriteNumber("missing", index.MissingCount);
                w.WriteNumber("unplayable", index.UnplayableCount);
                w.WriteNumber("next", index.NextId);
                w.WriteNumber("idMark", Platform.Settings.Get(Platform.Keys.MusicIdMark));
                w.WriteStartArray("roots");
                foreach (var r in index.Roots()) w.WriteStringValue(r.RootId + " | " + r.Path);
                w.WriteEndArray();
                w.WriteStartArray("files");
                foreach (var e in index.Snapshot())
                    if (e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        w.WriteStringValue(e.LocalId + " | " + e.RootId + " | " + e.RelativePath + " | " + e.State + (e.Why.Length > 0 ? " | " + e.Why : "")
                                           + (LocalLink.RenameOf(e.LocalId) is { } rn ? " | rename=" + rn.Title : ""));
                w.WriteEndArray();
                w.WriteStartArray("folders");
                foreach (var f in index.Folders())
                    if (f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) w.WriteStringValue(f.RootId + " | " + f.RelativePath);
                w.WriteEndArray();
                w.WriteString("skip", Platform.Settings.Get(Platform.Keys.MusicSkip));
            });
        }

        /// <summary>`decode path= out= rate= seekMs= ms=`: a local file through the decoder playback would open it with (its content's format,
        /// `Playback.Audio.CreateDecoderFor`), into raw float32 stereo frames, so its output can be compared with another decoder's sample by
        /// sample (#240 WAV/AIFF). No device, no session, no mount.</summary>
        string DecodeVerb(int id, string name, Inspect.Args a)
        {
            string path = a.Str("path");
            if (path.Length == 0 || !File.Exists(path)) return Inspect.Reply.Fail(id, name, "decode wants path=<a local file>, not '" + path + "'");
            if (!LocalTags.TryRead(path, out Spotify.Audio.Format format, out LocalTagSet tags, out string why))
                return Inspect.Reply.Fail(id, name, "the tag reader could not read it: " + why);
            // The play path's content probe (LocalSource's first step): which decoder the bytes name.
            byte[] head = new byte[8 * 1024];
            int headLen;
            using (var hf = File.OpenRead(path)) headLen = hf.Read(head, 0, head.Length);
            Playback.Audio.LocalProbe probe = Playback.Audio.ClassifyLocal(head.AsSpan(0, headLen), head.AsSpan(0, headLen));
            var opened = new Playback.Audio.Opened(format, tags.DurationMs, 0f, "", 0, IsLive: false);
            FluentGpu.Media.IAudioDecoder decoder = Playback.Audio.CreateDecoderFor(in opened);
            int rate = a.Int("rate", 0);
            if (rate <= 0) rate = tags.SampleRate > 0 ? tags.SampleRate : 48_000;
            var src = new FluentGpu.Media.FileByteSource(path);
            bool open = decoder.TryOpen(src, new FluentGpu.Media.MixFormat(rate, 2), out FluentGpu.Media.DecodedInfo info);
            long seekMs = a.Int("seekMs", -1), seekedTo = -1, frames = 0, limit = a.Int("ms", 0) * (long)rate / 1000;
            float peak = 0f;
            string outPath = a.Str("out");
            if (open)
            {
                if (seekMs >= 0) seekedTo = decoder.Seek(seekMs * rate / 1000);
                using FileStream? file = outPath.Length > 0 ? new FileStream(outPath, FileMode.Create, FileAccess.Write) : null;
                var buffer = new float[4096 * 2];
                var bytes = new byte[buffer.Length * 4];
                while (limit <= 0 || frames < limit)
                {
                    int n = decoder.Read(buffer);
                    if (n <= 0) break;
                    if (limit > 0) n = (int)Math.Min(n, limit - frames);
                    for (int i = 0; i < n * 2; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
                    if (file is not null)
                    {
                        Buffer.BlockCopy(buffer, 0, bytes, 0, n * 8);
                        file.Write(bytes, 0, n * 8);
                    }
                    frames += n;
                }
            }
            src.Close();
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("format", format.ToString());
                w.WriteString("probe", probe.Kind + " " + probe.Format + (probe.Why.Length > 0 ? " " + probe.Why : ""));
                w.WriteString("decoder", decoder.GetType().Name);
                w.WriteBoolean("opened", open);
                w.WriteString("why", decoder is Playback.Audio.PcmAudioDecoder p0 && p0.Why.Length > 0 ? p0.Why : why);
                w.WriteNumber("tagDurationMs", tags.DurationMs);
                w.WriteBoolean("estimated", tags.DurationEstimated);
                w.WriteNumber("rate", rate);
                w.WriteNumber("sourceRate", info.SourceFormat.SampleRate);
                w.WriteNumber("sourceChannels", info.SourceFormat.Channels);
                w.WriteNumber("exactFrames", decoder.Gapless.ExactFrames);
                w.WriteBoolean("tailKnown", decoder.Gapless.TailKnown);
                w.WriteNumber("leadIn", decoder.Gapless.LeadInFrames);
                w.WriteNumber("seekedTo", seekedTo);
                w.WriteNumber("frames", frames);
                w.WriteNumber("peak", peak);
                if (decoder is Playback.Audio.PcmAudioDecoder pcm)
                {
                    w.WriteString("label", pcm.Layout.Label);
                    w.WriteNumber("gain", pcm.AppliedGainLinear);
                    w.WriteNumber("layoutFrames", pcm.Layout.Frames);
                    w.WriteString("sample", pcm.Layout.Sample + (pcm.Layout.BigEndian ? " be" : "") + " " + pcm.Layout.BytesPerSample * 8);
                }
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
