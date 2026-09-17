// ── Playback/Playback.Video.Source.cs ──────────────────────────────────────────────────────────────────────────────
// what plays: the resolver tiers, the manifest memo, the v9 manifest parse, the PlayReady licence relay. NAMED PARTIAL
// of Playback/Playback.Video.cs (the 30 % rule: the host + the source together are ~1,920 lines against its 1,100)
//
// Role: SHELL
// Owner: H
// Wave: 3 (gap batch B7)
// Budget: 800 lines
// Spec: docs/plans/wavee/wavee-0.3-video-engine-implementation.md §1.4.1, §3.1.5, §6.2 H2; ch 24 §7 DATA GAPS 1-3;
//       gap register G-056, G-144, G-145, G-146, G-147, G-148
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS FILE IS. A playable → a `VideoSource`, or null for "this playable has no video". The host half
// (`Playback.Video.cs`) opens whatever this answers; nothing here holds a player, a thread of its own or a signal.
//
// THE TIERS, ONE DEFINITION (0.2.9's `CompositeVideoResolver` + `SpotifyVideoManifestResolver`, flattened):
//   1. the user's attached local file — it ALWAYS wins, including over the source's own official video;
//   2. the resolver installed BEFORE these tiers, for every playable they do not own — the modules owner's tier
//      (`Modules.InstallVideoTier`, a module `form: video` answer → `VideoSource.Clear`) is chained, never replaced;
//   3. a Spotify track's manifest id from the catalogue (`Track.VideoGid`, TrackV4 field 38) → one manifest GET;
//   4. a Spotify track with none (a linked-uri track, or a row whose TrackV4 has not landed): ONE extended-metadata POST
//      for {TrackV4, VIDEO_ASSOCIATIONS} → its own gid, else the associated video track's TrackV4 → its gid.
// `SourceTiers.First` is the pure answer to "which tier speaks first"; `Resolve` walks it with I/O.
//
// THE MEMO (G-146). A manifest is fetched once per id per `PrefetchSchedule.ManifestTtlMs` (10 min, 0.2.9's
// SingleFlightMemo), single-flight: a second resolve of an id already in flight waits for that fetch instead of
// issuing its own. A FAILURE is never remembered — the next resolve asks again.
//
// THREADS. `Resolve` blocks: api threads only (C9). It reads no table: the uri comes off the `EntityId` (a gid id
// formats, a text id reads the string table, which is safe against a concurrent intern) and the manifest id is the one
// the UI thread already read into the call.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using FluentGpu.WindowsApi.Media.PlayReady;
using TrackKind = FluentGpu.Media.TrackKind;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

public static partial class Playback
{
    public static partial class Video
    {
        // ── 1. the tiers (G-056, G-148) ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Which tier of the walk answered (the `tier=` of `[video] resolve`).</summary>
        public enum SourceTier : byte { None, Override, Chained, Manifest, Wire }

        /// <summary>The pure half of the walk: the tier that answers FIRST for what is known before any I/O.</summary>
        public static class SourceTiers
        {
            /// <param name="overrideTier">The tier-1 decision for the playable (<c>Video.Overrides.Decide</c>).</param>
            /// <param name="spotifyTrack">The playable is a Spotify catalogue track (the only kind a manifest exists for).</param>
            /// <param name="manifestId">The catalogue's manifest id for the row, or empty.</param>
            /// <param name="chained">A resolver was installed before the tiers (the module tier) and answers for the rest.</param>
            public static SourceTier First(global::Wavee.Video.OverrideTier overrideTier, bool spotifyTrack, string? manifestId,
                                           bool chained)
            {
                // A Broken or Quarantined attachment FALLS THROUGH: the link is kept for repair, the music never blocks.
                if (overrideTier == global::Wavee.Video.OverrideTier.UseOverride) return SourceTier.Override;
                if (!spotifyTrack) return chained ? SourceTier.Chained : SourceTier.None;
                return IsManifestId(manifestId) ? SourceTier.Manifest : SourceTier.Wire;
            }

            /// <summary>A manifest id is the video gid as 32 lowercase hex characters — what
            /// <c>/manifests/v9/json/sources/{id}</c> is addressed by. Anything else is not asked about.</summary>
            public static bool IsManifestId(string? id)
            {
                if (id is not { Length: 32 }) return false;
                foreach (char c in id)
                    if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
                return true;
            }
        }

        static Func<EntityId, string, CancellationToken, VideoSource?>? s_chained;
        static int s_resolverInstalled;

        /// <summary>Composition (`Video.Install`): put the tiers IN FRONT of the resolver <see cref="Playback.VideoResolver"/>
        /// holds now, which keeps answering for every playable the tiers do not own (a module row). Idempotent — a second
        /// call never chains the tiers onto themselves.</summary>
        public static void InstallResolver()
        {
            if (Interlocked.Exchange(ref s_resolverInstalled, 1) != 0) return;
            s_chained = Playback.VideoResolver;
            Playback.VideoResolver = ResolveSource;
        }

        /// <summary>THE resolver, in the shape <see cref="Playback.VideoResolver"/> calls it: the deck's id and the manifest
        /// id the UI thread read off its row. Blocks — api threads only. Null means "no video for this playable".</summary>
        public static VideoSource? ResolveSource(EntityId id, string manifestId, CancellationToken ct)
            => ResolveCore(id, manifestId, ct, forPlayback: true);

        static VideoSource? ResolveCore(EntityId id, string manifestId, CancellationToken ct, bool forPlayback)
        {
            if (id.IsEmpty) return null;
            long t0 = FrameNowMs();
            string uri = id.Text;
            if (forPlayback) ToUi(s_markResolving);

            // Tier 1. `Decide` runs a File.Exists probe (seconds against an offline share) — which is why the walk is on an
            // api thread and never inline on the UI thread. A Broken link raises `Overrides.BrokenLink` once per session.
            var decision = global::Wavee.Video.Overrides.Decide(uri);
            bool spotifyTrack = id.Kind == EntityKind.Track && id.Provider == EntityProvider.Spotify;
            Func<EntityId, string, CancellationToken, VideoSource?>? chained = s_chained;
            SourceTier tier = SourceTiers.First(decision.Tier, spotifyTrack, manifestId, chained is not null);

            VideoSource? source;
            bool cached = false;
            switch (tier)
            {
                case SourceTier.Override:
                    source = VideoSource.LocalFile(decision.Override.Path) with { PlayableUri = uri, OverrideSourceKey = decision.Override.SourceKey };
                    break;
                case SourceTier.Chained:
                    source = chained!(id, manifestId, ct) is { } answered ? answered with { PlayableUri = uri } : null;
                    break;
                case SourceTier.Manifest:
                    source = ManifestMemo.Get(manifestId, ct, out cached);
                    break;
                case SourceTier.Wire:
                    string wired = ManifestIds.Lookup(uri) ?? Wire.ManifestIdOf(uri, ct);
                    if (SourceTiers.IsManifestId(wired)) ManifestIds.Remember(uri, wired);
                    source = SourceTiers.IsManifestId(wired) ? ManifestMemo.Get(wired, ct, out cached) : null;
                    break;
                default:
                    source = null;
                    break;
            }
            if (source is not null && tier is SourceTier.Manifest or SourceTier.Wire) source = source with { PlayableUri = uri };

            Log.Info("video", $"[video] resolve track={Tail(uri)} tier={TierName(tier)} key={(source is null ? "(none)" : Tail(source.Key))} " +
                              $"cached={(cached ? "true" : "false")} override={decision.Tier} ms={FrameNowMs() - t0}");
            return source;
        }

        static readonly Action s_markResolving = static () => Phase.SetIfChanged(SwitchPhase.Resolving);

        static string TierName(SourceTier t) => t switch
        {
            SourceTier.Override => "override", SourceTier.Chained => "chained", SourceTier.Manifest => "manifest",
            SourceTier.Wire => "wire", _ => "none",
        };

        /// <summary>The uri → manifest id answers the wire tier found, so a re-toggle of a linked track costs no POST. A
        /// small bounded map (a session watches a handful of videos); full means start over, never grow.</summary>
        static class ManifestIds
        {
            const int Capacity = 64;
            static readonly Dictionary<string, string> s_ids = new(StringComparer.Ordinal);
            static readonly Lock s_gate = new();

            public static string? Lookup(string uri) { lock (s_gate) return s_ids.TryGetValue(uri, out string? id) ? id : null; }

            public static void Remember(string uri, string id)
            {
                lock (s_gate)
                {
                    if (s_ids.Count >= Capacity && !s_ids.ContainsKey(uri)) s_ids.Clear();
                    s_ids[uri] = id;
                }
            }
        }

        // ── 2. the manifest memo: single-flight, ten minutes, failures forgotten (G-146) ────────────────────────────

        public static class ManifestMemo
        {
            const int Capacity = 32;

            sealed class Entry
            {
                public readonly TaskCompletionSource<VideoSource?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public long AtMs;
            }

            static readonly Dictionary<string, Entry> s_entries = new(StringComparer.Ordinal);
            static readonly Lock s_gate = new();

            /// <summary>The source for <paramref name="manifestId"/>: a fresh remembered one, the answer of a fetch already in
            /// flight, or one fetched now. Blocks — api threads only.</summary>
            public static VideoSource? Get(string manifestId, CancellationToken ct, out bool cached)
            {
                Entry entry;
                bool owner = false;
                long now = FrameNowMs();
                lock (s_gate)
                {
                    if (s_entries.TryGetValue(manifestId, out Entry? hit) && Reusable(hit, now)) entry = hit;
                    else
                    {
                        if (s_entries.Count >= Capacity) EvictOldest();
                        entry = new Entry { AtMs = now };
                        s_entries[manifestId] = entry;
                        owner = true;
                    }
                }
                cached = !owner;
                if (!owner)
                {
                    try { return entry.Done.Task.GetAwaiter().GetResult(); }
                    catch { return null; }
                }

                VideoSource? source = null;
                try { source = Manifest.Resolve(manifestId, ct); }
                finally
                {
                    lock (s_gate)
                    {
                        entry.AtMs = FrameNowMs();
                        // A failure is never remembered: the next resolve asks again rather than serving "no video" for ten minutes.
                        if (source is null && s_entries.TryGetValue(manifestId, out Entry? current) && ReferenceEquals(current, entry))
                            s_entries.Remove(manifestId);
                    }
                    entry.Done.TrySetResult(source);
                }
                return source;
            }

            /// <summary>Is a remembered source for <paramref name="manifestId"/> still inside the TTL (the prefetch schedule's
            /// <c>ManifestFresh</c>)?</summary>
            public static bool IsFresh(string manifestId)
            {
                lock (s_gate)
                    return s_entries.TryGetValue(manifestId, out Entry? e) && e.Done.Task.IsCompletedSuccessfully
                        && e.Done.Task.Result is not null && PrefetchSchedule.IsFresh(FrameNowMs() - e.AtMs);
            }

            /// <summary>Forget one id — a source that proved dead must not be handed back for the rest of its TTL.</summary>
            public static void Invalidate(string manifestId) { lock (s_gate) s_entries.Remove(manifestId); }

            static bool Reusable(Entry e, long now)
                => !e.Done.Task.IsCompleted || (e.Done.Task.Result is not null && PrefetchSchedule.IsFresh(now - e.AtMs));

            static void EvictOldest()
            {
                string? oldest = null;
                long at = long.MaxValue;
                foreach (KeyValuePair<string, Entry> kv in s_entries)
                    if (kv.Value.Done.Task.IsCompleted && kv.Value.AtMs < at) { at = kv.Value.AtMs; oldest = kv.Key; }
                if (oldest is not null) s_entries.Remove(oldest);
            }
        }

        // ── 3. the wire tier: TrackV4 ∥ VIDEO_ASSOCIATIONS (0.2.9 tiers 1+2) ────────────────────────────────────────

        /// <summary>The manifest id of a track the catalogue has not given one for, asked of the wire. The two reads are
        /// PURE over a <c>BatchedExtensionResponse</c>, so the tier is testable without a socket.</summary>
        public static class Wire
        {
            /// <summary>ONE POST for the track's own TrackV4 and its kind-99 association; a second only when the association
            /// names a DIFFERENT track (the linked video), whose TrackV4 carries the gid. Blocks; "" for no video.</summary>
            public static string ManifestIdOf(string trackUri, CancellationToken ct)
            {
                ReadOnlySpan<string> uris = [trackUri, trackUri];
                ReadOnlySpan<Xm.ExtensionKind> kinds = [Xm.ExtensionKind.TrackV4, Xm.ExtensionKind.VideoAssociations];
                Spotify.Api.Result first = Spotify.Api.MetadataPost(Spotify.Api.MetadataBody(uris, kinds, Spotify.Api.Market, Spotify.Api.Catalogue), ct);
                if (!first.Ok) return "";
                string own = TrackV4Gid(first.Bytes, trackUri);
                if (own.Length > 0) return own;
                string linked = AssociatedUri(first.Bytes, trackUri);
                if (linked.Length == 0 || string.Equals(linked, trackUri, StringComparison.Ordinal)) return "";

                ReadOnlySpan<Xm.ExtensionKind> one = [Xm.ExtensionKind.TrackV4];
                Spotify.Api.Result second = Spotify.Api.MetadataPost(Spotify.Api.MetadataBody(linked, one, Spotify.Api.Market, Spotify.Api.Catalogue), ct);
                return second.Ok ? TrackV4Gid(second.Bytes, linked) : "";
            }

            /// <summary><c>Track.original_video[0].gid</c> (field 38 → 1) as lowercase hex, for <paramref name="uri"/>'s
            /// answered TrackV4 in <paramref name="response"/>; "" when absent.</summary>
            public static string TrackV4Gid(ReadOnlySpan<byte> response, string uri)
            {
                ReadOnlySpan<byte> track = Payload(response, (int)Xm.ExtensionKind.TrackV4, uri);
                if (track.IsEmpty) return "";
                var r = new Spotify.Decode.ProtoReader(track);
                while (r.Next())
                {
                    if (r.Field != 38 || r.Wire != 2) { r.Skip(); continue; }
                    ReadOnlySpan<byte> gid = r.Message().Bytes(1);
                    return gid.IsEmpty ? "" : Convert.ToHexStringLower(gid);
                }
                return "";
            }

            /// <summary><c>VideoAssociations.association.associated_uri</c> for <paramref name="uri"/>; "" when absent.</summary>
            public static string AssociatedUri(ReadOnlySpan<byte> response, string uri)
            {
                ReadOnlySpan<byte> assoc = Payload(response, (int)Xm.ExtensionKind.VideoAssociations, uri);
                if (assoc.IsEmpty) return "";
                ReadOnlySpan<byte> association = new Spotify.Decode.ProtoReader(assoc).Bytes(1);
                ReadOnlySpan<byte> linked = association.IsEmpty ? default : new Spotify.Decode.ProtoReader(association).Bytes(1);
                return linked.IsEmpty ? "" : Encoding.UTF8.GetString(linked);
            }

            /// <summary>The <c>extension_data.value</c> of the 2xx entity <paramref name="uri"/> under kind
            /// <paramref name="kind"/>, or empty. The kind is read before the entities whatever the wire order.</summary>
            static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> response, int kind, string uri)
            {
                var r = new Spotify.Decode.ProtoReader(response);
                while (r.Next())
                {
                    if (r.Field != 2 || r.Wire != 2) { r.Skip(); continue; }
                    ReadOnlySpan<byte> arrayBytes = r.Bytes();
                    if ((int)new Spotify.Decode.ProtoReader(arrayBytes).Varint(2, 0) != kind) continue;
                    var array = new Spotify.Decode.ProtoReader(arrayBytes);
                    while (array.Next())
                    {
                        if (array.Field != 3 || array.Wire != 2) { array.Skip(); continue; }
                        var entity = array.Message();
                        ReadOnlySpan<byte> entityUri = default, payload = default;
                        int status = 200;
                        bool answered = false;
                        while (entity.Next())
                        {
                            if (entity.Field == 1 && entity.Wire == 2) status = (int)entity.Message().Varint(1, 200);
                            else if (entity.Field == 2 && entity.Wire == 2) entityUri = entity.Bytes();
                            else if (entity.Field == 3 && entity.Wire == 2) { payload = entity.Message().Bytes(2); answered = true; }
                            else entity.Skip();
                        }
                        if (answered && status is >= 200 and < 300 && AsciiEquals(entityUri, uri)) return payload;
                    }
                }
                return default;
            }

            static bool AsciiEquals(ReadOnlySpan<byte> utf8, string s)
            {
                if (utf8.Length != s.Length) return false;
                for (int i = 0; i < utf8.Length; i++) if (utf8[i] != s[i]) return false;
                return true;
            }
        }

        // ── 4. Spotify's v9 video manifest → a PlayReady DASH descriptor ────────────────────────────────────────────

        /// <summary>Spotify's v9 video manifest: the fetch, the parse, the rung pick, and the DASH descriptor the native
        /// PlayReady runtime consumes. No MPD is synthesised — Spotify's manifest carries the templates directly and the
        /// segments are addressed by ABSOLUTE TIME, which is why the descriptor's stride is the segment length in
        /// seconds rather than 1.</summary>
        public static class Manifest
        {
            /// <summary>G-144: 0.2.9's request shape for the manifest route (`SpotifyVideoResolver`): the xpui Origin +
            /// Referer CORS fence and <c>Accept: */*</c>; bearer and client token are the runner's.</summary>
            public const Spotify.HeaderSet RequestHeaders = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken
                | Spotify.HeaderSet.Identity | Spotify.HeaderSet.AcceptAny | Spotify.HeaderSet.XpuiOrigin;

            /// <summary>The route. <c>supports_drm</c> is not optional: the clear variant is not served to a desktop client
            /// and asking for it answers 404.</summary>
            public static string Route(string manifestId)
                => "/manifests/v9/json/sources/" + manifestId + "/options/supports_drm";

            /// <summary>Fetch + parse + build, uncached (the memo above is the caller). Blocks. Null for every failure — a
            /// missing manifest is "this track has no video", not an error.</summary>
            public static VideoSource? Resolve(string manifestId, CancellationToken ct)
            {
                long t0 = FrameNowMs();
                Spotify.Api.Result result;
                try
                {
                    var args = new Spotify.RequestArgs
                    {
                        Path = Route(manifestId),
                        Host = Spotify.ApiHost.Spclient,
                        Verb = Spotify.Verb.Get,
                        Headers = RequestHeaders,
                    };
                    result = Spotify.Api.Send(Spotify.RequestKind.Custom, in args, ct);
                }
                catch (Exception ex) { Log.Warn("video", "manifest fetch failed", ex); return null; }

                if (!result.Ok || result.Body.Length == 0)
                {
                    Log.Info("video", $"[video] manifest.fail key={Tail(manifestId)} http={result.Status} ms={FrameNowMs() - t0}");
                    return null;
                }

                if (Parse(result.Bytes) is not { } m) return null;
                if (ToDescriptor(in m) is not { } descriptor) return null;
                License.Remember(m.DefaultKid, m.LicenseEndpoint);
                Log.Info("video", $"[video] manifest.ok key={Tail(manifestId)} ms={FrameNowMs() - t0} cached=false rungs={m.Video.Length} " +
                                  $"segLen={m.SegmentLengthSeconds} dur={m.DurationMs} natural={m.NaturalWidth}x{m.NaturalHeight} audio={(m.HasAudio ? "true" : "false")}");
                return VideoSource.PlayReady(manifestId, descriptor, License.Relay(m.LicenseEndpoint), m.LicenseEndpoint)
                    with { NaturalWidth = m.NaturalWidth, NaturalHeight = m.NaturalHeight };
            }

            /// <summary>One compatible video or audio rung. <see cref="KeyId"/> is the CENC key id as 32 lowercase hex.</summary>
            public readonly record struct Rung(string Id, string Codec, int Width, int Height, int Bitrate, string? KeyId);

            /// <summary>What the parse yields: the addressing templates, the PlayReady init data, and the two rung lists
            /// (H.264 only, and AAC only — Opus is advertised under the same PlayReady index and the protected MF pipeline
            /// cannot decode it).</summary>
            public readonly record struct Parsed(
                string BaseUrl, string InitTemplate, string SegmentTemplate,
                int SegmentLengthSeconds, long DurationMs,
                byte[] Pssh, string? LicenseEndpoint, string? DefaultKid,
                Rung[] Video, Rung Audio, bool HasAudio,
                int NaturalWidth, int NaturalHeight);

            /// <summary>Parse the v9 JSON. Both shapes are handled: templates at the root beside <c>contents[0]</c>, or
            /// everything nested under <c>sources[0]</c>.</summary>
            public static Parsed? Parse(ReadOnlySpan<byte> json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json.ToArray());
                    JsonElement root = doc.RootElement;
                    JsonElement content = root;
                    if (root.TryGetProperty("contents", out JsonElement contents) && contents.ValueKind == JsonValueKind.Array && contents.GetArrayLength() > 0)
                        content = contents[0];
                    else if (root.TryGetProperty("sources", out JsonElement sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
                        content = sources[0];

                    string baseUrl = FirstString(content, root, "base_urls");
                    string initTpl = Str(content, "initialization_template") ?? Str(root, "initialization_template") ?? "";
                    string segTpl = Str(content, "segment_template") ?? Str(root, "segment_template") ?? "";
                    if (initTpl.Length == 0 || segTpl.Length == 0) return null;

                    int segLen = Int(content, "segment_length") ?? Int(root, "segment_length") ?? 4;
                    if (segLen <= 0) segLen = 4;
                    long durMs = DurationOf(content, root);

                    // The PlayReady encryption info's INDEX gates rung selection: a profile is compatible only when it
                    // carries that index.
                    int prIndex = -1;
                    byte[] pssh = [];
                    string? license = null;
                    JsonElement encHost = Arr(content, "encryption_infos") is not null ? content : root;
                    if (Arr(encHost, "encryption_infos") is { } infos)
                    {
                        for (int i = 0; i < infos.GetArrayLength(); i++)
                        {
                            JsonElement info = infos[i];
                            if (!(Str(info, "key_system") ?? "").Equals("playready", StringComparison.OrdinalIgnoreCase)) continue;
                            prIndex = i;
                            if (Str(info, "encryption_data") is { Length: > 0 } b64)
                            {
                                try { pssh = Convert.FromBase64String(b64); } catch { pssh = []; }
                            }
                            license = Str(info, "license_server_endpoint");
                            break;
                        }
                    }
                    if (prIndex < 0) return null;      // no PlayReady rung ⇒ nothing this pipeline can play

                    var video = new List<Rung>(8);
                    Rung audio = default;
                    bool hasAudio = false;
                    string? defaultKid = null;
                    if (Arr(content, "profiles") is { } profiles)
                    {
                        for (int i = 0; i < profiles.GetArrayLength(); i++)
                        {
                            JsonElement pr = profiles[i];
                            if ((Str(pr, "file_type") ?? "") is not "mp4") continue;
                            if (!CarriesIndex(pr, prIndex)) continue;
                            // The id is a NUMBER on the real wire (the captured fixture) — a string-only read skipped every
                            // profile and turned every music video into "no video".
                            string id = IdOf(pr);
                            if (id.Length == 0) continue;
                            int bitrate = Int(pr, "max_bitrate") ?? Int(pr, "bandwidth_estimate")
                                ?? Int(pr, "video_bitrate") ?? Int(pr, "audio_bitrate") ?? 0;
                            string? kid = KeyIdHex(Str(pr, "key_id"));

                            string vcodec = Str(pr, "video_codec") ?? "";
                            if (vcodec.Length > 0)
                            {
                                if (!IsH264(vcodec)) continue;       // H.264 only: the protected path decodes nothing else
                                int w = Int(pr, "video_width") ?? Int(pr, "width") ?? 0;
                                int h = Int(pr, "video_height") ?? Int(pr, "height") ?? 0;
                                video.Add(new Rung(id, vcodec, w, h, bitrate, kid));
                                defaultKid ??= kid;
                                continue;
                            }
                            string acodec = Str(pr, "audio_codec") ?? "";
                            if (acodec.Length == 0 || !IsAac(acodec)) continue;
                            if (!hasAudio || bitrate > audio.Bitrate) { audio = new Rung(id, acodec, 0, 0, bitrate, kid); hasAudio = true; }
                        }
                    }
                    if (video.Count == 0) return null;

                    Rung[] rungs = [.. video];
                    Array.Sort(rungs, static (a, b) => a.Height != b.Height ? a.Height - b.Height : a.Bitrate - b.Bitrate);
                    Rung top = rungs[^1];

                    return new Parsed(baseUrl, initTpl, segTpl, segLen, durMs, pssh, license, defaultKid,
                        rungs, audio, hasAudio, top.Width, top.Height);
                }
                catch (Exception ex) { Log.Warn("video", "manifest parse failed", ex); return null; }
            }

            /// <summary>G-147, 0.2.9's fallback verbatim: <c>duration</c> on the content, else
            /// <c>end_time_millis − start_time_millis</c> read from the content OR the root, a missing start being 0.</summary>
            static long DurationOf(JsonElement content, JsonElement root)
            {
                long durMs = Long(content, "duration") ?? Long(root, "duration") ?? 0;
                if (durMs > 0) return durMs;
                long start = Long(content, "start_time_millis") ?? Long(root, "start_time_millis") ?? 0;
                long end = Long(content, "end_time_millis") ?? Long(root, "end_time_millis") ?? 0;
                return end > start ? end - start : 0;
            }

            /// <summary>A profile's CENC key id → 32 lowercase hex, the form the engine's licence cache and the native
            /// <c>FgPrLicenseAcquire</c> key by. The wire spells it BASE64 (16 bytes, big-endian GUID order — 0.2.9's
            /// <c>FormatCencKeyId</c>); a dashed GUID or bare hex is normalised. Anything else is null, and the engine then
            /// reads the KID from the PSSH instead of failing the licence on an unparseable id.</summary>
            public static string? KeyIdHex(string? keyId)
            {
                if (string.IsNullOrWhiteSpace(keyId)) return null;
                string k = keyId.Trim();
                if (k.Length == 36 && k[8] == '-') k = k.Replace("-", "", StringComparison.Ordinal);
                if (k.Length == 32 && IsHex(k)) return k.ToLowerInvariant();
                Span<byte> bytes = stackalloc byte[24];
                return Convert.TryFromBase64String(k, bytes, out int n) && n == 16 ? Convert.ToHexStringLower(bytes[..16]) : null;
            }

            static bool IsHex(string s)
            {
                foreach (char c in s) if (!char.IsAsciiHexDigit(c)) return false;
                return true;
            }

            /// <summary>Build the native descriptor. Null when the addressing cannot be resolved — the caller then reports
            /// "no video" rather than handing the native side a half-built open.</summary>
            public static DashSourceDescriptor? ToDescriptor(in Parsed m)
            {
                if (m.Video is not { Length: > 0 }) return null;
                Rung top = m.Video[^1];
                // The conservative INITIAL pick is ≤ 480p: a music video that opens at 1080p on a cold connection buffers
                // visibly, and the ABR controller climbs within seconds. Every compatible rung stays in the catalog.
                Rung initial = m.Video[0];
                for (int i = 0; i < m.Video.Length; i++)
                    if (m.Video[i].Height is > 0 and <= 480) initial = m.Video[i];

                if (Address(m, initial.Id) is not { } v) return null;
                Addressing? aa = m.HasAudio ? Address(m, m.Audio.Id) : null;

                int segmentCount = new SegmentGrid(m.SegmentLengthSeconds * 1000L).Count(m.DurationMs);
                if (segmentCount <= 0) return null;

                var reps = new ProtectedRepresentationDescriptor[m.Video.Length];
                int kept = 0;
                for (int i = 0; i < m.Video.Length; i++)
                {
                    Rung r = m.Video[i];
                    if (Address(m, r.Id) is not { } a) continue;
                    reps[kept++] = new ProtectedRepresentationDescriptor
                    {
                        Id = r.Id,
                        Quality = new QualityVariant(r.Id, r.Bitrate, new SizeI(r.Width, r.Height), 0,
                            new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None), Label: r.Height + "p"),
                        InitUrl = a.Init, SegmentBaseUrl = a.Base, SegmentPrefix = a.Prefix, SegmentSuffix = a.Suffix,
                        StartNumber = 0, SegmentCount = segmentCount, SegmentStride = m.SegmentLengthSeconds,
                        DefaultKid = r.KeyId,
                    };
                }
                if (kept == 0) return null;
                Array.Resize(ref reps, kept);

                var tracks = new List<ProtectedTrackDescriptor>(2)
                {
                    new() { Id = 1, Kind = TrackKind.Video, Label = top.Height + "p", IsDefault = true, Representations = reps },
                };
                if (aa is { } a2)
                {
                    tracks.Add(new ProtectedTrackDescriptor
                    {
                        Id = 2, Kind = TrackKind.Audio, Label = "audio", Role = TrackRole.Main, IsDefault = true,
                        Representations = new[]
                        {
                            new ProtectedRepresentationDescriptor
                            {
                                Id = m.Audio.Id,
                                Quality = new QualityVariant(m.Audio.Id, m.Audio.Bitrate, new SizeI(0, 0), 0,
                                    new MediaContentType(Container.Mp4, CodecId.None, CodecId.Aac)),
                                InitUrl = a2.Init, SegmentBaseUrl = a2.Base, SegmentPrefix = a2.Prefix, SegmentSuffix = a2.Suffix,
                                StartNumber = 0, SegmentCount = segmentCount, SegmentStride = m.SegmentLengthSeconds,
                                DefaultKid = m.Audio.KeyId,
                            },
                        },
                    });
                }

                return new DashSourceDescriptor
                {
                    Catalog = new ProtectedAdaptiveCatalog { Tracks = tracks },
                    InitUrl = v.Init,
                    SegmentBaseUrl = v.Base,
                    SegmentPrefix = v.Prefix,
                    SegmentSuffix = v.Suffix,
                    StartNumber = 0,
                    SegmentCount = segmentCount,
                    // Spotify names segments by ABSOLUTE TIME: segment i = start + i × (segment length in seconds).
                    SegmentStride = m.SegmentLengthSeconds,
                    // The seek index (G-149): segment i starts at i·SegmentLengthMs and every Spotify segment begins with an
                    // IDR, so the planner maps a seek to one segment with no index fetch.
                    SegmentLengthMs = m.SegmentLengthSeconds * 1000,
                    DurationMs = m.DurationMs,
                    SegmentsStartWithKeyframe = true,
                    Pssh = m.Pssh,
                    DefaultKid = m.DefaultKid,
                    RepresentationId = initial.Id,
                    Codecs = initial.Codec,
                    AudioInitUrl = aa?.Init,
                    AudioSegmentBaseUrl = aa?.Base,
                    AudioSegmentPrefix = aa?.Prefix,
                    AudioSegmentSuffix = aa?.Suffix,
                    AudioCodecs = m.HasAudio ? m.Audio.Codec : null,
                };
            }

            /// <summary>The four URL parts the native demuxer walks.</summary>
            public readonly record struct Addressing(string Init, string Base, string Prefix, string Suffix);

            /// <summary>Substitute the profile id and the file type, then split the media template at the literal
            /// <c>{{segment_timestamp}}</c> token — at the LAST '/' before it, so the base stays a directory and the signed
            /// query parameters survive byte for byte.</summary>
            public static Addressing? Address(in Parsed m, string profileId)
            {
                const string Token = "{{segment_timestamp}}";
                string init = Fill(m.BaseUrl, m.InitTemplate, profileId);
                string media = Fill(m.BaseUrl, m.SegmentTemplate, profileId);
                int at = media.IndexOf(Token, StringComparison.Ordinal);
                if (at < 0) return null;
                int slash = media.LastIndexOf('/', at);
                if (slash < 0) return null;
                return new Addressing(init, media[..(slash + 1)], media[(slash + 1)..at], media[(at + Token.Length)..]);
            }

            static string Fill(string baseUrl, string template, string profileId)
            {
                string s = template.Replace("{{profile_id}}", profileId, StringComparison.Ordinal)
                                   .Replace("{{file_type}}", "mp4", StringComparison.Ordinal);
                if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s;
                if (baseUrl.Length == 0) return s;
                return baseUrl.EndsWith('/') || s.StartsWith('/') ? baseUrl + s : baseUrl + "/" + s;
            }

            static string IdOf(JsonElement profile)
            {
                if (!profile.TryGetProperty("id", out JsonElement v)) return "";
                return v.ValueKind switch
                {
                    JsonValueKind.Number when v.TryGetInt64(out long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    JsonValueKind.String => v.GetString() ?? "",
                    _ => "",
                };
            }

            static bool IsH264(string codec)
                => codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("avc3", StringComparison.OrdinalIgnoreCase)
                || codec.Contains("h264", StringComparison.OrdinalIgnoreCase);

            static bool IsAac(string codec)
                => codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)
                || codec.StartsWith("aac", StringComparison.OrdinalIgnoreCase);

            static bool CarriesIndex(JsonElement profile, int index)
            {
                if (Arr(profile, "encryption_indices") is { } arr)
                {
                    for (int i = 0; i < arr.GetArrayLength(); i++)
                        if (arr[i].TryGetInt32(out int v) && v == index) return true;
                    return false;
                }
                return Int(profile, "encryption_index") is { } single ? single == index : true;
            }

            static string FirstString(JsonElement a, JsonElement b, string name)
            {
                if (Arr(a, name) is { } arr && arr.GetArrayLength() > 0) return arr[0].GetString() ?? "";
                if (Arr(b, name) is { } arr2 && arr2.GetArrayLength() > 0) return arr2[0].GetString() ?? "";
                return "";
            }

            static JsonElement? Arr(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                   && v.ValueKind == JsonValueKind.Array ? v : null;

            static string? Str(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                   && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            static int? Int(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                   && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

            static long? Long(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                   && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;
        }

        // ── 5. the PlayReady licence relay (G-144, G-145) ───────────────────────────────────────────────────────────

        /// <summary>The native CDM raises an opaque SOAP challenge; this POSTs it to the manifest's own endpoint over the
        /// authenticated session and hands the licence back. The content key never crosses into managed code.
        /// <para><b>Never on the CDM thread (G-145).</b> The runtime calls the relay on the KeyMessage thread and continues
        /// on the returned task; a relay that did the POST synchronously inside the call blocked the CDM for the whole round
        /// trip. The POST runs on an api thread (<see cref="Spotify.Api.RunAsync{T}"/>: named, bounded) and the task is
        /// handed back at once.</para></summary>
        public static class License
        {
            const string DefaultRoute = "/playready-license";

            /// <summary>G-144: 0.2.9's `SpotifyLicenseRelay` shape — xpui Origin + Referer, <c>Content-Type: text/xml;
            /// charset=utf-8</c> and the AcquireLicense <c>SOAPAction</c>.</summary>
            public const Spotify.HeaderSet RequestHeaders = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken
                | Spotify.HeaderSet.Identity | Spotify.HeaderSet.XpuiOrigin | Spotify.HeaderSet.SoapLicense;

            /// <summary>The relay for one manifest's endpoint.</summary>
            public static Func<LicenseRequest, ValueTask<LicenseResponse>> Relay(string? endpoint)
            {
                string route = Normalize(endpoint);
                return request => new ValueTask<LicenseResponse>(Spotify.Api.RunAsync(() => Acquire(route, request)));
            }

            /// <summary>The relay the process-lifetime protected backend is built with: it serves the PREPARE path (which has
            /// no per-open relay) and any re-acquisition after an expiry, finding the endpoint by the challenge's KID.</summary>
            public static readonly Func<LicenseRequest, ValueTask<LicenseResponse>> ByKeyId = static request =>
                new ValueTask<LicenseResponse>(Spotify.Api.RunAsync(() => Acquire(RouteFor(request.KeyId), request)));

            static readonly Dictionary<string, string> s_routes = new(StringComparer.Ordinal);
            static readonly Lock s_gate = new();

            /// <summary>Remember which endpoint a KID's manifest named (bounded; a session licenses a handful of videos).</summary>
            public static void Remember(string? kid, string? endpoint)
            {
                if (kid is not { Length: > 0 }) return;
                lock (s_gate)
                {
                    if (s_routes.Count >= 64 && !s_routes.ContainsKey(kid)) s_routes.Clear();
                    s_routes[kid] = Normalize(endpoint);
                }
            }

            static string RouteFor(string? kid)
            {
                if (kid is not { Length: > 0 }) return DefaultRoute;
                lock (s_gate) return s_routes.TryGetValue(kid.ToLowerInvariant(), out string? route) ? route : DefaultRoute;
            }

            static LicenseResponse Acquire(string route, LicenseRequest request)
            {
                byte[] challenge = MemoryMarshal.TryGetArray(request.Challenge, out ArraySegment<byte> seg)
                                   && seg.Offset == 0 && seg.Count == seg.Array!.Length
                    ? seg.Array
                    : request.Challenge.ToArray();
                var args = new Spotify.RequestArgs
                {
                    Path = route,
                    Host = Spotify.ApiHost.Spclient,
                    Verb = Spotify.Verb.Post,
                    Body = challenge,
                    Headers = RequestHeaders,
                };
                Spotify.Api.Result result = Spotify.Api.Send(Spotify.RequestKind.Custom, in args, CancellationToken.None);
                if (!result.Ok || result.Body.Length == 0)
                    throw new InvalidOperationException($"Spotify PlayReady licence POST to {route} failed (HTTP {result.Status}).");
                return new LicenseResponse(result.Body);
            }

            /// <summary>The manifest gives a path, an absolute URL or an <c>@webgate</c>-style prefix; <c>Spotify.Api</c>
            /// speaks paths on a named host (0.2.9's <c>ResolveRoute</c>).</summary>
            public static string Normalize(string? endpoint)
            {
                if (string.IsNullOrWhiteSpace(endpoint)) return DefaultRoute;
                string e = endpoint.Trim();
                if (e.StartsWith('/')) return e;
                int scheme = e.IndexOf("://", StringComparison.Ordinal);
                if (scheme < 0) return "/" + e.TrimStart('@', '/');
                int path = e.IndexOf('/', scheme + 3);
                return path >= 0 ? e[path..] : DefaultRoute;
            }
        }
    }
}
