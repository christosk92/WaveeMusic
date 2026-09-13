// ── Spotify/Spotify.Telemetry.cs ───────────────────────────────────────────────────────────────────────────────────
// Gabo, Herodotus
//
// Role: SHELL
// Owner: F
// Wave: 2
// Budget: 600 lines
// Spec: plan
//
// WHAT THIS IS FOR, HONESTLY. Two services, and neither is analytics about the user for us — we keep nothing and send
// nothing anywhere but Spotify:
//   GABO      `/gabo-receiver-service/v3/events/` — PLAY REGISTRATION. A stream Spotify does not receive is a stream
//             that did not happen: no royalty, no "recently played", no daylist, no Wrapped. A client that plays
//             without registering is a client that quietly steals from the artist, which is why this file exists at
//             all and why its failures are logged rather than swallowed.
//   HERODOTUS `/herodotus/…/ResumePointRevisionService/*` — the play-history head and episode resume points, so
//             "continue listening" and a podcast's remembered position work across devices.
//
// TWO BATCHERS, TWO NAMED TIMERS (P10). Gabo flushes on a 100-event cap, a 125 KB cap, or its 300 s heartbeat;
// Herodotus flushes on a 2 s tick. Every queue here is BOUNDED (C8) and every drop is counted, never silent — a
// telemetry backlog must not be the thing that grows without limit during an outage. The two ends drop differently
// and on purpose: the PRODUCER side refuses the newest (a full intake queue means the worker is 512 events behind,
// and stalling the audio pump to make room is worse than losing one row), while the FLUSH side drops the oldest
// (the newest plays are the ones still worth registering).
//
// THREADS. `Enqueue` is callable from anywhere and does not block: it writes into a bounded channel. One named thread
// `wavee-spotify-gabo` drains it and does the blocking POST. Herodotus flushes onto an api thread. Nothing here
// touches a table, a signal or `Entities.Strings` (C1).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Ev = Wavee.Protocol.EventSender;
using Evt = Wavee.Protocol.EventSender.Events;
using Rs = Wavee.Protocol.Resumption;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>Play registration (gabo) and resume points (herodotus). SHELL.</summary>
    public static class Telemetry
    {
        // ── 1. the context every envelope carries ────────────────────────────────────────────────────────────────────

        /// <summary>The client identity gabo stamps on every event. Built once per process: the installation id is
        /// PERSISTED (a fresh one per launch makes the account look like a new device every time) and the app-session
        /// id is not (it identifies this run, which is exactly what it is for).</summary>
        public sealed record Context(
            byte[] ClientId, byte[] InstallationId, byte[] AppSessionId,
            string AppVersionString, long AppVersionCode,
            string PlatformType, string DeviceManufacturer, string DeviceModel, string DeviceId, string OsVersion);

        static Context? s_context;

        /// <summary>The process's context. `App.cs` may set it before boot; otherwise it is derived here.</summary>
        public static Context Client
        {
            get => s_context ??= Build();
            set => s_context = value;
        }

        static Context Build()
        {
            var version = Environment.OSVersion.Version;
            return new Context(
                ClientId: Convert.FromHexString(Identity.ClientId),
                InstallationId: InstallationId(),
                AppSessionId: RandomNumberGenerator.GetBytes(16),
                AppVersionString: Identity.ClientVersion,
                AppVersionCode: long.TryParse(Identity.AppVersion, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long code) ? code : 0,
                PlatformType: "windows",
                DeviceManufacturer: "Microsoft Corporation",
                DeviceModel: "PC laptop",
                DeviceId: MachineSid() ?? "S-1-5-21-0-0-0-0",
                OsVersion: version.Major + "." + version.Minor + "." + version.Build);
        }

        /// <summary>The installation id, persisted beside the app's other state — the one piece of this file that
        /// survives a restart, and the reason the account's device list does not grow by one entry per launch. It is a
        /// FILE and not a setting because it is not a preference: nothing in the app may offer to change it, and the
        /// settings registry is the list of things that may be changed (0.2.9 kept it the same way).</summary>
        static byte[] InstallationId()
        {
            try
            {
                string path = Path.Combine(Platform.LocalFolder, "gabo_installation_id");
                if (File.Exists(path))
                {
                    string hex = File.ReadAllText(path).Trim();
                    if (hex.Length == 32) return Convert.FromHexString(hex);
                }
                byte[] fresh = RandomNumberGenerator.GetBytes(16);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Convert.ToHexStringLower(fresh));
                return fresh;
            }
            catch (Exception ex)
            {
                Log.Warn("spotify", "gabo installation id unavailable — using a per-run one", ex);
                return RandomNumberGenerator.GetBytes(16);
            }
        }

        // ── 2. the envelope ──────────────────────────────────────────────────────────────────────────────────────────

        // The sdk token the desktop client sends. It is a STRING the service parses for its own bookkeeping, and the
        // numbers in it must agree with the caps below (125 kB payload, 100-event batch, 300 s / 30 s heartbeats) —
        // a mismatch is the one thing in this file the service has been observed to notice.
        const string SdkVersionName =
            "0.9.4-rl-essopt-loginsend-onlinesend-bcdsend-heartbeat300.0s/30.0s-modern-payload125kB-batch100";
        const string SdkType = "cpp";
        const long MonotonicClockId = 9;

        static readonly Stopwatch Monotonic = Stopwatch.StartNew();
        static readonly byte[] BatchSequenceId = RandomNumberGenerator.GetBytes(20);

        static Ev.EventEnvelope Envelope(string eventName, byte[] message, long sequenceNumber)
        {
            Context context = Client;
            var envelope = new Ev.EventEnvelope
            {
                EventName = eventName,
                SequenceId = ByteString.CopyFrom(BatchSequenceId),
                SequenceNumber = sequenceNumber,
            };
            envelope.EventFragment.Add(Fragment("message", message));
            envelope.EventFragment.Add(Fragment("context_client_id",
                new Ev.ClientId { Value = ByteString.CopyFrom(context.ClientId) }.ToByteArray()));
            envelope.EventFragment.Add(Fragment("context_installation_id",
                new Ev.InstallationId { Value = ByteString.CopyFrom(context.InstallationId) }.ToByteArray()));
            envelope.EventFragment.Add(Fragment("context_application_desktop", new Ev.ApplicationDesktop
            {
                VersionString = context.AppVersionString,
                VersionCode = context.AppVersionCode,
                SessionId = ByteString.CopyFrom(context.AppSessionId),
            }.ToByteArray()));
            envelope.EventFragment.Add(Fragment("context_device_desktop", new Ev.DeviceDesktop
            {
                PlatformType = context.PlatformType,
                DeviceManufacturer = context.DeviceManufacturer,
                DeviceModel = context.DeviceModel,
                DeviceId = context.DeviceId,
                OsVersion = context.OsVersion,
            }.ToByteArray()));
            envelope.EventFragment.Add(Fragment("context_time",
                new Ev.Time { Value = NowMs() }.ToByteArray()));
            envelope.EventFragment.Add(Fragment("context_monotonic_clock",
                new Ev.MonotonicClock { Id = MonotonicClockId, Value = Monotonic.ElapsedMilliseconds }.ToByteArray()));
            envelope.EventFragment.Add(Fragment("context_sdk",
                new Ev.Sdk { VersionName = SdkVersionName, Type = SdkType }.ToByteArray()));
            envelope.EventFragment.Add(new Ev.EventEnvelope.Types.EventFragment
            {
                Name = "context_client_context_id",
                Data = ByteString.Empty,
            });
            return envelope;
        }

        static Ev.EventEnvelope.Types.EventFragment Fragment(string name, byte[] data)
            => new() { Name = name, Data = ByteString.CopyFrom(data) };

        // ── 3. the gabo batcher ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The caps the sdk token above advertises. Changing one without changing that string is how a batch
        /// stops being accepted for a reason nothing in the response explains.</summary>
        public const int GaboMaxEvents = 100;
        public const int GaboMaxUncompressedBytes = 125 * 1024;
        /// <summary>The heartbeat flush (P10, named). Five minutes: long enough that an idle app is silent, short
        /// enough that a crash loses at most one window of plays.</summary>
        public const int GaboFlushIntervalMs = 300_000;
        /// <summary>How many events survive a persistent outage. Beyond this the OLDEST are dropped and counted.</summary>
        public const int GaboBacklogCap = 500;
        const int GaboQueueDepth = 512;
        const string GaboRoute = "/gabo-receiver-service/v3/events/";

        /// <summary>WHEN to flush, as a pure function of what is pending. Separate from the worker because the caps
        /// are advertised to the service in <c>SdkVersionName</c> above, and a cap that drifts from that string is a
        /// batch the service can refuse for a reason nothing in the response explains.</summary>
        public static bool ShouldFlush(int pendingCount, int pendingBytes)
            => pendingCount >= GaboMaxEvents || pendingBytes >= GaboMaxUncompressedBytes;

        /// <summary>WHAT to drop when a persistent outage has grown the backlog past its cap (C8). The excess, and the
        /// excess is taken off the FRONT — the newest plays are the ones still worth registering.</summary>
        public static int Overflow(int pendingCount) => Math.Max(0, pendingCount - GaboBacklogCap);

        static readonly BlockingCollection<Ev.EventEnvelope?> GaboQueue = new(GaboQueueDepth);
        static readonly List<Ev.EventEnvelope> Pending = new(GaboMaxEvents);
        static Timer? s_gaboHeartbeat;
        static Timer? s_resumeTicker;
        static long s_sequence;
        static int s_pendingBytes;
        static int s_gaboDropped;
        static int s_booted;

        /// <summary>The monotonic per-event sequence the service dedupes on. Persisted so a restart does not replay
        /// numbers the service has already seen.</summary>
        public static long Sequence => Interlocked.Read(ref s_sequence);

        /// <summary>Events the batcher had to drop — an outage longer than the backlog, or a producer faster than the
        /// queue. Non-zero means plays went unregistered, which is worth a diagnostics row.</summary>
        public static int Dropped => Volatile.Read(ref s_gaboDropped);

        /// <summary>Start the worker and the two timers. Idempotent; `App.cs` calls it once the session is online.</summary>
        public static void Boot()
        {
            if (Interlocked.CompareExchange(ref s_booted, 1, 0) != 0) return;
            try { Interlocked.Exchange(ref s_sequence, Platform.Settings.Get(Platform.Keys.GaboGlobalSequence)); }
            catch (Exception ex) { Log.Warn("spotify", "gabo sequence unreadable — starting at 0", ex); }

            new Thread(GaboLoop) { IsBackground = true, Name = "wavee-spotify-gabo" }.Start();
            s_gaboHeartbeat = new Timer(static _ => GaboQueue.TryAdd(null), null,
                GaboFlushIntervalMs, GaboFlushIntervalMs);
            s_resumeTicker = new Timer(static _ => Api.Run(FlushResumePoints), null,
                ResumeFlushMs, ResumeFlushMs);
        }

        /// <summary>Queue one play-registration event. Never blocks and never throws. A full queue (512 deep, C8)
        /// REFUSES this event and counts it: the worker is 512 events behind, which means the service is down, and
        /// the honest answer is a number in <see cref="Dropped"/> rather than a producer that stalls the audio pump
        /// to make room. The BACKLOG's overflow — the flush side — drops the oldest instead; see
        /// <see cref="Overflow"/> for why the two ends choose differently.</summary>
        public static void Enqueue(string eventName, byte[] payload)
        {
            if (Volatile.Read(ref s_booted) == 0) Boot();
            long sequence = Interlocked.Increment(ref s_sequence);
            try { Platform.Settings.Set(Platform.Keys.GaboGlobalSequence, sequence); }
            catch (Exception ex) { Log.Warn("spotify", "gabo sequence not persisted", ex); }
            if (!GaboQueue.TryAdd(Envelope(eventName, payload, sequence)))
                Interlocked.Increment(ref s_gaboDropped);
        }

        /// <summary>Ask for a flush now — the app is closing, or the user signed out.</summary>
        public static void Flush() => GaboQueue.TryAdd(null);

        /// <summary>Stop the two timers and flush both batchers. `App.cs` calls it on shutdown: a play registered
        /// nowhere is a play that did not happen, so the last window is worth the milliseconds it costs.</summary>
        public static void Shutdown()
        {
            s_gaboHeartbeat?.Dispose();
            s_gaboHeartbeat = null;
            s_resumeTicker?.Dispose();
            s_resumeTicker = null;
            Flush();
            FlushResumePoints();
        }

        static void GaboLoop()
        {
            foreach (Ev.EventEnvelope? item in GaboQueue.GetConsumingEnumerable())
            {
                try
                {
                    if (item is null) { FlushGabo(); continue; }
                    Pending.Add(item);
                    s_pendingBytes += item.CalculateSize();
                    if (ShouldFlush(Pending.Count, s_pendingBytes)) FlushGabo();
                }
                catch (Exception ex) { Log.Error("spotify", "gabo worker faulted", ex); }
            }
        }

        static void FlushGabo()
        {
            if (Pending.Count == 0) return;
            var request = new Ev.PublishEventsRequest();
            request.Event.AddRange(Pending);
            byte[] body = Api.Gzip(request.ToByteArray());

            for (int attempt = 0; attempt < 3; attempt++)
            {
                Api.Result result = Api.PostEncoded(GaboRoute, ApiHost.SpclientWg,
                    HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.ContentProtobuf,
                    body, "application/x-protobuf", "gzip", CancellationToken.None);
                if (result.Ok)
                {
                    Log.Info("spotify", "gabo flushed " + Pending.Count + " event(s)");
                    Pending.Clear();
                    s_pendingBytes = 0;
                    return;
                }
                Log.Warn("spotify", "gabo flush failed status=" + result.Status + " attempt=" + (attempt + 1));
            }

            // Three refusals: keep what we have for the next trigger, but BOUND it. Dropping the oldest is the honest
            // choice — the newest plays are the ones still worth registering — and it is counted, never silent.
            int drop = Overflow(Pending.Count);
            if (drop == 0)
            {
                Log.Warn("spotify", "gabo retained " + Pending.Count + " event(s) for the next flush");
                return;
            }
            Pending.RemoveRange(0, drop);
            Interlocked.Add(ref s_gaboDropped, drop);
            s_pendingBytes = 0;
            for (int i = 0; i < Pending.Count; i++) s_pendingBytes += Pending[i].CalculateSize();
            Log.Error("spotify", "gabo backlog capped — dropped " + drop + " event(s)");
        }

        // ── 4. the play-registration projection (the event shapes, as values) ────────────────────────────────────────
        //
        // 0.2.9's `RawCoreStreamProjection` was a class with sixteen mutable fields and a `PlaybackEvent` subscription.
        // Here it is a STRUCT the caller owns (Wave 3's `Playback.Host` keeps one) and five functions over it, so the
        // state of a registration is inspectable, copyable, and testable without a player.

        const long CoreVersion = 6_004_800_000_000_000L;
        const string PlaybackStack = "boombox";
        const string OrchestrationStack = "context-player";

        /// <summary>The four ids a registration is keyed by. Minted by the player at the moment a track starts.</summary>
        public readonly record struct PlaybackIds(
            byte[] PlaybackId, byte[] StreamId, string PlaybackIdHex, string PageInstanceId, string InteractionId,
            string CommandId);

        /// <summary>One track's registration, as it accumulates. A VALUE — the caller holds it and hands it back.</summary>
        public struct Registration
        {
            public PlaybackIds Ids;
            public string ContentUri, PlayContext, Provider, ReasonStart, SourceStart, AudioFormatName;
            public byte[]? MediaId, FileId;
            public int BitrateKbps, MsPlayed, SegmentStartPositionMs;
            public long TrackStartMs, SegmentStartMs, DurationMs, SegmentSequence, SegmentSequenceInternal;
            public bool Open;
        }

        static bool s_firstPlayInSession = true;

        /// <summary>A track started. Emits the five open-time events the desktop client sends before the first
        /// sample is heard, and returns the registration the caller keeps until the track ends.</summary>
        public static Registration Started(in PlaybackIds ids, string contentUri, string contextUri, string provider,
            string reasonStart, byte[]? mediaId, byte[]? fileId, int bitrateKbps, string audioFormatName,
            long durationMs, long positionMs)
        {
            long now = NowMs();
            var registration = new Registration
            {
                Ids = ids,
                ContentUri = contentUri,
                PlayContext = contextUri.Length > 0 ? contextUri : contentUri,
                Provider = provider.Length > 0 ? provider : "context",
                ReasonStart = reasonStart.Length > 0 ? reasonStart : "clickrow",
                SourceStart = ContextKind(contextUri.Length > 0 ? contextUri : contentUri),
                AudioFormatName = audioFormatName,
                MediaId = mediaId,
                FileId = fileId,
                BitrateKbps = bitrateKbps,
                DurationMs = durationMs,
                TrackStartMs = now,
                SegmentStartMs = now,
                SegmentStartPositionMs = (int)Math.Min(positionMs, int.MaxValue),
                Open = true,
            };

            Enqueue("CorePlaybackCommandCorrelation", new Evt.CorePlaybackCommandCorrelation
            {
                PlaybackId = ByteString.CopyFrom(ids.PlaybackId),
                CommandId = ids.CommandId,
            }.ToByteArray());
            Enqueue("AudioResolve", new Evt.AudioResolve
            {
                PlaybackId = ByteString.CopyFrom(ids.PlaybackId),
                ResolveMs = 120,
                ContentUri = contentUri,
                CommandId = ids.CommandId,
            }.ToByteArray());
            int bitrate = bitrateKbps > 0 ? bitrateKbps * 1000 : 160_000;
            Enqueue("AudioFileSelection", new Evt.AudioFileSelection
            {
                PlaybackId = ByteString.CopyFrom(ids.PlaybackId),
                Reason = "best matching bitrate",
                SelectedBitrate = bitrate,
                Quality = "high",
                TargetBitrate = bitrate,
            }.ToByteArray());
            Session("open", ids.PlaybackId, "", 0);
            Enqueue("BoomboxPlaybackSession", new Evt.BoomboxPlaybackSession
            {
                PlaybackId = ByteString.CopyFrom(ids.PlaybackId),
                AudioKeyMs = 200,
                ResolveMs = 120,
                TotalSetupMs = 400,
                BufferingMs = 200,
                DurationMs = durationMs,
                Preset = "default",
                FirstPlay = s_firstPlayInSession,
            }.ToByteArray());
            s_firstPlayInSession = false;
            if (fileId is { Length: > 0 })
                Enqueue("HeadFileDownload", new Evt.HeadFileDownload
                {
                    FileId = ByteString.CopyFrom(fileId),
                    PlaybackId = ByteString.CopyFrom(ids.PlaybackId),
                    CdnDomain = "heads-fa-tls13.spotifycdn.com",
                    HeadFileSize = 131072,
                    HttpResult = 200,
                    RequestType = "interactive",
                }.ToByteArray());
            return registration;
        }

        /// <summary>Paused. Closes the open segment and opens nothing — a resume starts the next one.</summary>
        public static void Paused(ref Registration r, long positionMs)
        {
            if (!r.Open) return;
            CloseSegment(ref r, (int)Math.Min(positionMs, int.MaxValue), isPause: true, isLast: false, "pause");
            Session("pause", r.Ids.PlaybackId, "", 0);
        }

        /// <summary>Resumed. The next segment's reason-start is the play button, not whatever started the track.</summary>
        public static void Resumed(ref Registration r, long positionMs)
        {
            if (!r.Open) return;
            r.ReasonStart = "playbtn";
            r.SegmentStartMs = NowMs();
            r.SegmentStartPositionMs = (int)Math.Min(positionMs, int.MaxValue);
            Session("resume", r.Ids.PlaybackId, "", 0);
        }

        /// <summary>Seeked. <paramref name="fromMs"/> is the PRE-seek playhead (that is what closes the segment) and
        /// <paramref name="toMs"/> is where it landed (that is what opens the next one).</summary>
        public static void Seeked(ref Registration r, long fromMs, long toMs)
        {
            if (!r.Open) return;
            int landed = (int)Math.Min(toMs, int.MaxValue);
            CloseSegment(ref r, (int)Math.Min(fromMs, int.MaxValue), isPause: false, isLast: false, "seek");
            r.ReasonStart = "playbtn";
            r.SegmentStartMs = NowMs();
            r.SegmentStartPositionMs = landed;
            Session("seek", r.Ids.PlaybackId, "", landed);
        }

        /// <summary>The track is over. Emits the closing segment, the RawCoreStream itself — THE royalty event — and
        /// the four trailers the desktop client sends with it.</summary>
        public static void Ended(ref Registration r, long positionMs, string reasonEnd)
        {
            if (!r.Open) return;
            int endPosition = (int)Math.Min(positionMs, int.MaxValue);
            string reason = reasonEnd.Length > 0 ? reasonEnd : "endplay";
            CloseSegment(ref r, endPosition, isPause: false, isLast: true, reason);
            Session("close", r.Ids.PlaybackId, reason, 0);

            int bitrate = r.BitrateKbps > 0 ? r.BitrateKbps * 1000 : 160_000;
            if (r.FileId is { Length: > 0 } fileId)
                Enqueue("Download", new Evt.Download
                {
                    FileId = ByteString.CopyFrom(fileId),
                    PlaybackId = ByteString.CopyFrom(r.Ids.PlaybackId),
                    FileSize = long.MaxValue,
                    BytesDownloaded = long.MaxValue,
                    Realm = "music",
                    CdnUriScheme = "https",
                    CdnDomain = "audio-fa.scdn.co",
                    RequestType = "interactive",
                    Bitrate = bitrate,
                }.ToByteArray());

            var raw = new Evt.RawCoreStream
            {
                PlaybackId = ByteString.CopyFrom(r.Ids.PlaybackId),
                ParentPlaybackId = ByteString.CopyFrom(new byte[16]),
                MediaId = r.MediaId is { Length: > 0 } media ? ByteString.CopyFrom(media) : ByteString.Empty,
                MediaType = "audio",
                SourceStart = r.SourceStart,
                ReasonStart = r.ReasonStart,
                SourceEnd = r.SourceStart,
                ReasonEnd = reason,
                PlaybackStartTime = r.TrackStartMs,
                MsPlayed = r.MsPlayed,
                MsPlayedNominal = r.MsPlayed,
                AudioFormat = FormatName(in r),
                PlayContext = r.PlayContext,
                ContentUri = r.ContentUri,
                Provider = r.Provider,
                Referrer = r.SourceStart,
                CoreVersion = CoreVersion,
                PlayType = "full",
                IsAssumedPremium = Current.Tier == Tier.Premium,
                CoreBundle = "local",
                PlaybackStack = PlaybackStack,
                DecisionId = "",
                PlayContextDecisionId = "",
                StreamId = ByteString.CopyFrom(r.Ids.StreamId),
                CommandId = r.Ids.CommandId,
                PlaybackStackSecondary = PlaybackStack,
                OrchestrationStack = OrchestrationStack,
                DeviceBrand = "spotify",
                DeviceModelName = "PC laptop",
                DeviceTypeName = "computer",
            };
            Enqueue("RawCoreStream", raw.ToByteArray());

            Enqueue("ContentIntegrity", new Evt.ContentIntegrity
            {
                PlaybackId = ByteString.CopyFrom(r.Ids.PlaybackId),
                RippingCategories = 0,
                IsRippingFasterThanRt = false,
            }.ToByteArray());
            Enqueue("AudioRouteSegmentEnd", new Evt.AudioRouteSegmentEnd
            {
                PlaybackId = ByteString.CopyFrom(r.Ids.PlaybackId),
            }.ToByteArray());
            Enqueue("AdOpportunityEvent", new Evt.AdOpportunityEvent
            {
                TriggerState = "PASS",
                ContentUri = r.ContentUri,
                PlaybackId = r.Ids.PlaybackIdHex,
            }.ToByteArray());

            r.Open = false;
        }

        static void CloseSegment(ref Registration r, int endPositionMs, bool isPause, bool isLast, string reasonEnd)
        {
            int played = Math.Max(0, endPositionMs - r.SegmentStartPositionMs);
            r.MsPlayed += played;
            long endTimestamp = NowMs();
            r.SegmentSequence++;
            r.SegmentSequenceInternal++;

            Enqueue("RawCoreStreamSegment", new Evt.RawCoreStreamSegment
            {
                PlaybackId = ByteString.CopyFrom(r.Ids.PlaybackId),
                StartPosition = r.SegmentStartPositionMs,
                EndPosition = endPositionMs,
                MsPlayed = played,
                ReasonStart = r.ReasonStart,
                ReasonEnd = reasonEnd,
                PlaybackSpeed = 1.0,
                StartTimestamp = r.SegmentStartMs,
                EndTimestamp = endTimestamp,
                IsPause = isPause,
                IsLast = isLast,
                SequenceId = r.SegmentSequence,
                MediaType = "audio",
                ContentUri = r.ContentUri,
                Provider = r.Provider,
                PlaybackStack = PlaybackStack,
                StreamId = ByteString.CopyFrom(r.Ids.StreamId),
                PageInstanceId = r.Ids.PageInstanceId,
                InteractionId = r.Ids.InteractionId,
                PlayContext = r.PlayContext,
                SequenceIdInternal = r.SegmentSequenceInternal,
                DeviceBrand = "spotify",
                DeviceModelName = "PC laptop",
                DeviceTypeName = "computer",
            }.ToByteArray());

            if (isLast) return;
            r.SegmentStartMs = endTimestamp;
            r.SegmentStartPositionMs = endPositionMs;
        }

        static void Session(string eventName, byte[] playbackId, string reason, int seekPosition)
            => Enqueue("AudioSessionEvent", new Evt.AudioSessionEvent
            {
                Event = eventName,
                PlaybackId = ByteString.CopyFrom(playbackId),
                Reason = reason,
                FeatureIdentifier = "boombox",
                SeekPosition = seekPosition,
                Paused = eventName == "pause",
                Speed = eventName == "open" ? long.MaxValue : 1,
            }.ToByteArray());

        static string FormatName(in Registration r)
        {
            if (r.AudioFormatName.Length > 0) return r.AudioFormatName;
            return "Vorbis " + (r.BitrateKbps > 0 ? r.BitrateKbps : 160) + " kbps";
        }

        /// <summary>The middle segment of a uri — `playlist`, `album`, `artist` — which is what the service calls the
        /// "source". Unknown for anything that is not a uri at all.</summary>
        public static string ContextKind(string contextUri)
        {
            if (contextUri.Length == 0) return "unknown";
            int first = contextUri.IndexOf(':');
            if (first < 0) return "unknown";
            int second = contextUri.IndexOf(':', first + 1);
            return second < 0 ? "unknown" : contextUri[(first + 1)..second];
        }

        // ── 5. herodotus: the play-history head and episode resume points ────────────────────────────────────────────

        const string PlayHistoryUri = "spotify:list:play-history:v1";
        const string CreateRoute = "/herodotus/spotify.resumption.v1.ResumePointRevisionService/CreateResumePointRevision";
        const string BatchRoute = "/herodotus/spotify.resumption.v1.ResumePointRevisionService/BatchCreateResumePointRevisions";
        const string ListRoute = "/herodotus/spotify.resumption.v1.ResumePointRevisionService/ListResumePointRevisions";

        /// <summary>The resume-point flush tick (P10, named). Two seconds: a scrub storm coalesces into one write and
        /// a closed app has lost at most two seconds of position.</summary>
        public const int ResumeFlushMs = 2000;
        /// <summary>How many failed flushes before a batch is dropped. Revisions are idempotent (the service dedupes
        /// on create-time), so retrying is safe — but not forever.</summary>
        public const int ResumeMaxAttempts = 5;
        const int ResumeQueueCap = 256;

        static readonly Lock ResumeGate = new();
        static readonly List<Rs.CreateResumePointRevisionRequest> ResumeQueue = new(32);
        static int s_resumeFailStreak;
        static int s_resumeFlushing;

        /// <summary>Note that <paramref name="itemUri"/> became the current item. The head of the play history is what
        /// "recently played" reads on every one of the user's devices.</summary>
        public static void PlayHistory(string itemUri, long createTimeMs)
            => QueueRevision(new Rs.CreateResumePointRevisionRequest
            {
                EntityUri = PlayHistoryUri,
                Revision = new Rs.CurrentStateRevision
                {
                    Value = new Rs.CurrentStateValue { ItemUri = itemUri },
                    CreateTime = Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(createTimeMs)),
                },
            });

        /// <summary>Remember where an episode was left. Micros, not millis — the service's unit.</summary>
        public static void ResumePoint(string episodeUri, long positionMs, long createTimeMs)
            => QueueRevision(new Rs.CreateResumePointRevisionRequest
            {
                EntityUri = episodeUri,
                Revision = new Rs.CurrentStateRevision
                {
                    Value = new Rs.CurrentStateValue
                    {
                        EntityUri = episodeUri,
                        ResumePoint = new Rs.ResumePoint { Position = Math.Max(0, positionMs) * 1000L },
                    },
                    CreateTime = Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(createTimeMs)),
                },
            });

        static void QueueRevision(Rs.CreateResumePointRevisionRequest revision)
        {
            if (Volatile.Read(ref s_booted) == 0) Boot();
            lock (ResumeGate)
            {
                ResumeQueue.Add(revision);
                if (ResumeQueue.Count > ResumeQueueCap) ResumeQueue.RemoveRange(0, ResumeQueue.Count - ResumeQueueCap);
            }
        }

        static void FlushResumePoints()
        {
            if (Interlocked.Exchange(ref s_resumeFlushing, 1) == 1) return;   // a slow write must not overlap the tick
            try
            {
                Rs.CreateResumePointRevisionRequest[] batch;
                lock (ResumeGate)
                {
                    if (ResumeQueue.Count == 0) return;
                    batch = ResumeQueue.ToArray();
                    ResumeQueue.Clear();
                }

                // One revision uses the singular route (capture parity); two or more use the batch route, whose
                // minimum is two items.
                byte[] body;
                string route;
                if (batch.Length == 1) { body = batch[0].ToByteArray(); route = CreateRoute; }
                else
                {
                    var request = new Rs.BatchCreateResumePointRevisionsRequest();
                    request.Requests.AddRange(batch);
                    body = request.ToByteArray();
                    route = BatchRoute;
                }

                Api.Result result = Api.PostEncoded(route, ApiHost.Spclient,
                    HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.ContentProtobuf,
                    body, "application/x-protobuf", null, CancellationToken.None);
                if (result.Ok)
                {
                    Volatile.Write(ref s_resumeFailStreak, 0);
                    Log.Info("spotify", "herodotus wrote " + batch.Length + " revision(s)");
                    return;
                }

                int attempt = Interlocked.Increment(ref s_resumeFailStreak);
                if (attempt > ResumeMaxAttempts)
                {
                    Volatile.Write(ref s_resumeFailStreak, 0);
                    Log.Error("spotify", "herodotus refused " + attempt + " times — dropping " + batch.Length + " revision(s)");
                    return;
                }
                lock (ResumeGate) ResumeQueue.InsertRange(0, batch);
                Log.Warn("spotify", "herodotus write failed (" + result.Status + ") — requeued, attempt " + attempt);
            }
            catch (Exception ex) { Log.Error("spotify", "herodotus flush faulted", ex); }
            finally { Interlocked.Exchange(ref s_resumeFlushing, 0); }
        }

        /// <summary>Where an episode was left, in MILLISECONDS, or 0. Blocks; api threads only.</summary>
        public static long ResumeMs(string episodeUri, CancellationToken ct)
        {
            var request = new Rs.ListResumePointRevisionsRequest { EntityUri = episodeUri, Limit = 1 };
            Api.Result result = Api.PostEncoded(ListRoute, ApiHost.Spclient,
                HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.ContentProtobuf,
                request.ToByteArray(), "application/x-protobuf", null, ct);
            if (!result.Ok || result.Body.Length == 0) return 0;
            try
            {
                var parsed = Rs.ListResumePointRevisionsResponse.Parser.ParseFrom(result.Bytes);
                if (parsed.Revisions.Count == 0) return 0;
                long micros = parsed.Revisions[0].Value?.ResumePoint?.Position ?? 0;
                return micros / 1000L;
            }
            catch (InvalidProtocolBufferException) { return 0; }
        }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
