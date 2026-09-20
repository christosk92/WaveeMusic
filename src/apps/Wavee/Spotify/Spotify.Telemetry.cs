// ── Spotify/Spotify.Telemetry.cs ───────────────────────────────────────────────────────────────────────────────────
// Gabo, Herodotus
//
// Role: SHELL
// Owner: F (§6, the progress hydrate: T, podcast wave P2)
// Wave: 2 · P2
// Budget: 600 lines — over before P2 (756); §6 adds ~290. Its natural split is a `Spotify.Telemetry.Progress.cs` once
//   `Telemetry` is made a partial class (reported, not done here: one owner per file per wave).
// Spec: plan · docs/plans/wavee/podcast-show-rework-implementation.md §5.8
//
// WHAT THIS IS FOR, HONESTLY. Two services, and neither is analytics about the user for us — we keep nothing and send
// nothing anywhere but Spotify:
//   GABO      `/gabo-receiver-service/v3/events/` — PLAY REGISTRATION. A stream Spotify does not receive is a stream
//             that did not happen: no royalty, no "recently played", no daylist, no Wrapped. A client that plays
//             without registering is a client that quietly steals from the artist, which is why this file exists at
//             all and why its failures are logged rather than swallowed.
//   HERODOTUS `/herodotus/…/ResumePointRevisionService/*` — the play-history head and episode resume points, so
//             "continue listening" and a podcast's remembered position work across devices; and
//             `CurrentStateService/ListCurrentStates` — the login sync's ONE read of every resume point the account
//             touched, folded into the episode rows (§6, podcast plan §5.8).
//
// TWO BATCHERS, TWO NAMED TIMERS (P10). Gabo flushes on a 100-event cap, a 125 KB cap, or its 300 s heartbeat;
// Herodotus flushes on a 2 s tick. Every queue here is BOUNDED (C8) and every drop is counted, never silent — a
// telemetry backlog must not be the thing that grows without limit during an outage. The two ends drop differently
// and on purpose: the PRODUCER side refuses the newest (a full intake queue means the worker is 512 events behind,
// and stalling the audio pump to make room is worse than losing one row), while the FLUSH side drops the oldest
// (the newest plays are the ones still worth registering).
//
// THREADS. `Enqueue` is callable from anywhere and does not block: it writes into a bounded channel. One named thread
// `wavee-spotify-gabo` drains it and does the blocking POST. Herodotus flushes onto an api thread. Nothing off the UI
// thread touches a table, a signal or `Entities.Strings` (C1): the progress hydrate STAGES on an api thread and its
// posted landing (`LandProgress`, UI thread) is the one place this file commits rows and writes its two signals.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using FluentGpu.Signals;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Ev = Wavee.Protocol.EventSender;
using Evt = Wavee.Protocol.EventSender.Events;
using Rs = Wavee.Protocol.Resumption;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>Play registration (gabo) and resume points (herodotus). SHELL.</summary>
    public static partial class Telemetry
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
                AppVersionString: string.Join(".", Identity.ClientVersion.Split('.').Take(4)),
                AppVersionCode: long.TryParse(Identity.AppVersion, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long code) ? code : 0,
                PlatformType: "windows",
                DeviceManufacturer: "Microsoft Corporation",
                DeviceModel: DeviceModel(),
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

        static readonly Stopwatch Monotonic = Stopwatch.StartNew();

        static Ev.EventEnvelope Envelope(string eventName, byte[] message, long sequenceNumber)
        {
            Context context = Client;
            var envelope = new Ev.EventEnvelope
            {
                EventName = eventName,
                SequenceId = ByteString.CopyFrom(s_sequenceId),
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
                new Ev.MonotonicClock { Id = s_monotonicClockId, Value = Monotonic.ElapsedMilliseconds }.ToByteArray()));
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

        /// <summary>The gabo POST, swappable — the same seam <see cref="Spotify.Post"/> and `Library.Net` already
        /// use for a real socket. A test flushes without one by replacing this; nothing else about the batcher
        /// (caps, retries, the backlog drop, the sequence persist) changes shape when it does.</summary>
        public static Func<byte[], Api.Result> PostGabo { get; set; } = static body =>
            Api.PostEncoded(GaboRoute, ApiHost.SpclientWg,
                HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.ContentProtobuf,
                body, "application/x-protobuf", "gzip", CancellationToken.None);

        static readonly BlockingCollection<GaboWork> GaboQueue = new(GaboQueueDepth);
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

        /// <summary>How far ahead of the last PERSISTED sequence a fresh boot starts. The number is persisted only on
        /// a flush (every 100 events at most, or the 300 s heartbeat) and not on every `Enqueue`, so a crash between
        /// two flushes can strand up to <see cref="GaboMaxEvents"/> increments that never reached disk. Resuming at
        /// exactly the persisted value would then replay numbers the service has already seen; resuming a full
        /// batch ahead never does, at the cost of a small, harmless gap in the sequence the service does not mind
        /// (it only rejects a number going backwards, never one skipping forward).</summary>
        public const long GaboSequenceResumeMargin = GaboMaxEvents;

        /// <summary>Start the worker and the two timers. Idempotent; `App.cs` calls it once the session is online.</summary>
        public static void Boot()
        {
            if (Interlocked.CompareExchange(ref s_booted, 1, 0) != 0) return;
            try
            {
                long persisted = Platform.Settings.Get(Platform.Keys.GaboGlobalSequence);
                Interlocked.Exchange(ref s_sequence, persisted + GaboSequenceResumeMargin);
            }
            catch (Exception ex) { Log.Warn("spotify", "gabo sequence unreadable — starting at 0", ex); }

            LoadTelemetryIdentity();
            new Thread(GaboLoop) { IsBackground = true, Name = "wavee-spotify-gabo" }.Start();
            s_gaboHeartbeat = new Timer(static _ => { RestoreGabo(s_outboxAccount); GaboQueue.TryAdd(new GaboWork(null)); }, null,
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
            Interlocked.Increment(ref s_sequence);
            long sequence = NextEventSequence(eventName);
            var envelope = Envelope(eventName, payload, sequence);
            string account = s_outboxAccount;
            JournalGabo(account, envelope);
            if (!GaboQueue.TryAdd(new GaboWork(envelope, account)))
                Log.Warn("spotify", "gabo worker queue full; durable event retained");
        }

        /// <summary>Ask for a flush now — the app is closing, or the user signed out.</summary>
        public static void Flush() => GaboQueue.TryAdd(new GaboWork(null));

        /// <summary>Stop the two timers and flush both batchers. `App.cs` calls it on shutdown: a play registered
        /// nowhere is a play that did not happen, so the last window is worth the milliseconds it costs.</summary>
        public static void Shutdown()
        {
            s_gaboHeartbeat?.Dispose();
            s_gaboHeartbeat = null;
            s_resumeTicker?.Dispose();
            s_resumeTicker = null;
            ShutdownBounded();
        }

        static void GaboLoop()
        {
            foreach (GaboWork work in GaboQueue.GetConsumingEnumerable())
            {
                try
                {
                    if (work.Event is null) { FlushGabo(); continue; }
                    if (s_pendingAccount != work.Account)
                    {
                        FlushGabo();
                        // Unsent events remain in their account's journal; never transmit them as the new account.
                        Pending.Clear(); s_pendingBytes = 0; s_pendingAccount = work.Account;
                    }
                    if (!Pending.Any(x => SameEvent(x, work.Event)))
                    {
                        Pending.Add(work.Event);
                        s_pendingBytes += work.Event.CalculateSize();
                    }
                    if (ShouldFlush(Pending.Count, s_pendingBytes)) FlushGabo();
                }
                catch (Exception ex) { Log.Error("spotify", "gabo worker faulted", ex); }
                finally { work.Completion?.TrySetResult(); }
            }
        }

        static void FlushGabo()
        {
            if (Pending.Count == 0 || s_pendingAccount != s_outboxAccount
                || (s_pendingAccount.Length > 0 && !OutboxAccountIsCurrent(s_pendingAccount))) return;
            using var accountRequest = Api.ForAccount(s_pendingAccount);
            try
            {
                var request = new Ev.PublishEventsRequest();
                request.Event.AddRange(Pending);
                byte[] body = Api.Gzip(request.ToByteArray());

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (s_pendingAccount.Length > 0 && !OutboxAccountIsCurrent(s_pendingAccount)) return;
                    Api.Result result = PostGabo(body);
                    if (result.Ok)
                    {
                        var response = result.Bytes.Length == 0 ? new Ev.PublishEventsResponse()
                            : Ev.PublishEventsResponse.Parser.ParseFrom(result.Bytes);
                        int[] retry = RetryGaboIndices(response, Pending.Count, out int rejected);
                        AcknowledgeGabo(s_pendingAccount, Pending, retry);
                        var retained = retry.Select(index => Pending[index]).ToArray();
                        Log.Info("spotify", "gabo settled " + Pending.Count + " event(s), retry=" + retained.Length + " rejected=" + rejected);
                        if (rejected > 0) Interlocked.Add(ref s_gaboDropped, rejected);
                        Pending.Clear(); Pending.AddRange(retained);
                        s_pendingBytes = Pending.Sum(x => x.CalculateSize());
                        return;
                    }
                    Log.Warn("spotify", "gabo flush failed status=" + result.Status + " attempt=" + (attempt + 1));
                }

                // Three refusals: keep what we have for the next trigger, but BOUND it. Dropping the oldest is the
                // honest choice — the newest plays are the ones still worth registering — and it is counted, never
                // silent.
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
            finally
            {
                // ONE settings write per FLUSH, not per event (G-077) — and posted through `Spotify.Post` so it lands
                // on the UI thread like every other write, whatever thread the gabo worker happens to be. Runs
                // whether the POST above succeeded, failed-and-retained or failed-and-dropped: the sequence numbers
                // were already minted (in `Enqueue`, in memory) for whatever is in `Pending` either way, and what
                // this persists is "how far the in-memory counter has gotten", not "what the service has accepted".
                SaveTelemetryIdentity();
                PersistSequence();
            }
        }

        /// <summary>Persist the current sequence off the gabo worker thread, through <see cref="Spotify.Post"/>
        /// (C1: settings are written on the UI thread only). One write per flush call — not one per event.</summary>
        static void PersistSequence()
        {
            long sequence = Sequence;
            Post(() =>
            {
                try { Platform.Settings.Set(Platform.Keys.GaboGlobalSequence, sequence); }
                catch (Exception ex) { Log.Warn("spotify", "gabo sequence not persisted", ex); }
            });
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
            public bool Open, SegmentActive;
            public double PlaybackSpeed;
            public long SegmentStartTick;
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
                Open = true, SegmentActive = true, PlaybackSpeed = 1,
                SegmentStartTick = Stopwatch.GetTimestamp(),
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
            if (!r.Open || !r.SegmentActive) return;
            CloseSegment(ref r, (int)Math.Min(positionMs, int.MaxValue), isPause: true, isLast: false, "pause");
            r.SegmentActive = false;
            Session("pause", r.Ids.PlaybackId, "", 0);
        }

        /// <summary>Resumed. The next segment's reason-start is the play button, not whatever started the track.</summary>
        public static void Resumed(ref Registration r, long positionMs)
        {
            if (!r.Open) return;
            r.ReasonStart = "playbtn";
            r.SegmentStartMs = NowMs();
            r.SegmentStartTick = Stopwatch.GetTimestamp();
            r.SegmentActive = true;
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
            r.SegmentStartTick = Stopwatch.GetTimestamp();
            r.SegmentActive = true;
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
                DeviceModelName = Client.DeviceModel,
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
                PlaybackId = r.Ids.PlaybackIdHex, Reason = "end_song",
                RouteId = s_endpointId, EndpointName = s_endpointName, RouteType = s_routeType,
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
            int played = r.SegmentActive ? (int)Math.Clamp(Stopwatch.GetElapsedTime(r.SegmentStartTick).TotalMilliseconds, 0, int.MaxValue) : 0;
            r.MsPlayed += played;
            long endTimestamp = NowMs();

            Enqueue("RawCoreStreamSegment", new Evt.RawCoreStreamSegment
            {
                PlaybackId = ByteString.CopyFrom(r.Ids.PlaybackId),
                StartPosition = r.SegmentStartPositionMs,
                EndPosition = endPositionMs,
                MsPlayed = played,
                ReasonStart = r.ReasonStart,
                ReasonEnd = reasonEnd,
                PlaybackSpeed = r.PlaybackSpeed > 0 ? r.PlaybackSpeed : 1.0,
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
                DeviceModelName = Client.DeviceModel,
                DeviceTypeName = "computer",
            }.ToByteArray());

            r.SegmentSequence++;
            r.SegmentSequenceInternal++;
            if (isLast) return;
            r.SegmentStartTick = Stopwatch.GetTimestamp();
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

        /// <summary>Nanoseconds per millisecond. THE UNIT, settled by the 2026-09-19 capture of the official client
        /// (findings-podcast-wire.md §3.2): the resume point is a <c>google.protobuf.Duration</c> — <c>{seconds = 1,
        /// nanos = 2}</c>, nanos always a whole number of ms — proven by five exact matches against the official
        /// <c>showItemsPlayedState</c> (<c>{141, 122000000}</c> = 141 122 ms) and by its own video writes. The retired
        /// "microseconds in field 2" (<c>ms × 1000</c>) wrote NANOS: the server held 4 699 ms as 4.7 ms, 998 943 ms as
        /// 0.999 s, and anything past 1000 s as an invalid Duration — on every device of the account.</summary>
        public const int NanosPerMs = 1_000_000;
        const int NanosPerSecond = 1_000_000_000;

        /// <summary>The resume-point flush tick (P10, named). Two seconds: a scrub storm coalesces into one write and
        /// a closed app has lost at most two seconds of position.</summary>
        public const int ResumeFlushMs = 2000;
        /// <summary>How many failed flushes before a batch is dropped. Revisions are idempotent (the service dedupes
        /// on create-time), so retrying is safe — but not forever.</summary>
        public const int ResumeMaxAttempts = 5;
        const int ResumeQueueCap = 256;

        static readonly Lock ResumeGate = new();
        static readonly List<Rs.CreateResumePointRevisionRequest> ResumeQueue = new(32);
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

        /// <summary>Remember where an episode was left: a pause, an end, a skip — or a mark, which states the whole
        /// duration ("played") or 0 ("unplayed"; <see cref="EpisodeProgress.TryMarkedResumePoint"/>). Queued for the 2 s
        /// flush; see <see cref="ResumeRevision"/>.</summary>
        public static void ResumePoint(string episodeUri, long positionMs, long createTimeMs)
            => QueueRevision(ResumeRevision(episodeUri, positionMs, createTimeMs));

        /// <summary>The revision <see cref="ResumePoint"/> queues, as a value. PURE — the wire shape is pinned by a test to
        /// the official client's own bytes (vc2 #308: <c>{2 uri, 4 {2 {2 Duration}, 3 create_time}}</c>, the value's
        /// entity uri left out as that client leaves it; the server fills it in). The position goes out as a Duration
        /// (<see cref="ResumeDurationOf"/>). There is no "completed" spelling: the official client wrote none for an
        /// episode in either capture, and the empty markers (<see cref="Episode.Rules.ResumeArm"/>) are too thinly proven
        /// to write. <paramref name="createTimeMs"/> is the client's instant — the one the local mirror stamps
        /// <c>PlayedAt</c> with, so the hydrate can tell this device's own revision from a newer one
        /// (<see cref="EpisodeProgress.Landing"/>).</summary>
        public static Rs.CreateResumePointRevisionRequest ResumeRevision(string episodeUri, long positionMs, long createTimeMs)
            => new()
            {
                EntityUri = episodeUri,
                Revision = new Rs.CurrentStateRevision
                {
                    Value = new Rs.CurrentStateValue { ResumePoint = ResumeDurationOf(positionMs) },
                    CreateTime = Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(createTimeMs)),
                },
            };

        /// <summary>A position in ms as herodotus's Duration: <c>seconds = ms / 1000</c>, <c>nanos = (ms % 1000) ×</c>
        /// <see cref="NanosPerMs"/> — nanos a whole number of ms that never reaches a second, at any position. A negative
        /// position clamps to 0 (<c>{}</c> on the wire, the official "not started"). PURE.</summary>
        public static Duration ResumeDurationOf(long positionMs)
        {
            long ms = Math.Max(0L, positionMs);
            return new Duration { Seconds = ms / 1000, Nanos = (int)(ms % 1000) * NanosPerMs };
        }

        /// <summary>Herodotus's Duration as ms: <c>seconds × 1000 + nanos / </c><see cref="NanosPerMs"/>. Saturating, never
        /// throwing, on whatever an older client left behind (Wavee's own µs writes among them): a negative part reads 0,
        /// nanos past a second read 999 ms, seconds clamp to the int range. A corrupted value reads as what the server
        /// HOLDS (<c>{2: 4699000}</c> = 4 ms, as every other client reads it) — the next write replaces it; nothing here
        /// guesses the old unit back. PURE.</summary>
        public static long PositionMsOf(Duration duration)
        {
            long seconds = Math.Clamp(duration.Seconds, 0L, int.MaxValue);
            int nanos = Math.Clamp(duration.Nanos, 0, NanosPerSecond - 1);
            return seconds * 1000 + nanos / NanosPerMs;
        }

        static void QueueRevision(Rs.CreateResumePointRevisionRequest revision)
        {
            if (Volatile.Read(ref s_booted) == 0) Boot();
            lock (ResumeGate)
            {
                // Coalesce unsent positions for one episode, but preserve history and explicit markers.
                if (revision.Revision?.Value?.ResumePoint is not null)
                    ResumeQueue.RemoveAll(x => x.EntityUri == revision.EntityUri && x.Revision?.Value?.ResumePoint is not null);
                if (ResumeQueue.Count >= ResumeQueueCap)
                {
                    Log.Error("spotify", "progress journal full; refusing revision");
                    return;
                }
                ResumeQueue.Add(revision);
                PersistResumeJournal();
            }
        }

        static void FlushResumePoints()
        {
            if (Interlocked.Exchange(ref s_resumeFlushing, 1) == 1) return;
            try
            {
                Rs.CreateResumePointRevisionRequest[] batch;
                string account;
                lock (ResumeGate) { batch = ResumeQueue.ToArray(); account = s_outboxAccount; }
                if (!OutboxAccountIsCurrent(account)) return;
                using var accountRequest = Api.ForAccount(account);
                if (batch.Length == 0) { FlushCompletion(account); return; }
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
                if (!OutboxAccountIsCurrent(account)) return;
                Api.Result result = SendRevision(route, body);
                if (!result.Ok)
                {
                    Log.Warn("spotify", "herodotus write failed status=" + result.Status + "; durable revisions retained");
                    return;
                }
                lock (ResumeGate)
                {
                    if (account != s_outboxAccount) return;
                    foreach (var revision in batch) ResumeQueue.Remove(revision);
                    PersistResumeJournal();
                }
                Log.Info("spotify", "herodotus wrote " + batch.Length + " revision(s)");
                FlushCompletion(account);
            }
            catch (Exception ex) { Log.Error("spotify", "herodotus flush faulted; durable revisions retained", ex); }
            finally { Interlocked.Exchange(ref s_resumeFlushing, 0); }
        }

        // ── 6. the progress hydrate: every resume point the account touched, in ONE call (podcast plan §5.8) ──────────
        //
        // WHY. Until P2 nothing wrote `EpisodeTable.ProgressMs` on real data (plan §1.2, defect 1): pause/end positions
        // went to herodotus and nowhere else, and the one read was a per-load `ListResumePointRevisions` round trip that
        // SEEKED the player after its audio had begun. Now the login sync asks `ListCurrentStates` once, the answer is
        // folded into episode rows at Full, the player mirrors its own positions at Local (`Entities.MirrorEpisodeProgress`,
        // Episode.Progress.cs) — and a load reads the row, never the wire (D-8: the per-load read is deleted).
        //
        // THE WINDOW IS THE WHOLE 180 DAYS, EVERY SYNC (0d0429a0's behaviour). A "since the last landed sync" stamp was
        // built and removed: it promised that the cache still held every row an earlier hydrate had landed, and three
        // things break that promise without telling the stamp — the 30-day sweep, "Clear metadata" (`Store.DropCatalog`)
        // and a schema change (a new DDL fingerprint names a new, empty file). Each left old progress reading "unplayed"
        // until the account happened to play that episode again. One call of at most 1000 states per sync is cheap; a
        // silently wrong row is not.
        //
        // THE OFFICIAL HYDRATE, AND WHY THIS ONE DIFFERS (2026-09-19 capture, findings-podcast-wire.md §3.2). The official
        // client asks `{limit: 1000}` with NO filter on a cold first sync, then filters on the max `update_time` of its
        // previous answer — a watermark over a cache it trusts. The limit is matched; the watermark is not (the paragraph
        // above). The 180-day filter stays: vc2 proves the server answers a filtered ask exactly as the official one, so it
        // does not NEED the filter-less form, and an answer is ordered by kind then uri — never by recency — so a cut at
        // the limit drops arbitrary rows, which the window keeps rarer. The cost: an episode last touched more than 180
        // days ago stays UNKNOWN here where the official cold sync would carry it (the captured account held nothing
        // older than 141 days, so the difference was not observable).
        //
        // THREADS (C1/C9). `HydrateProgress`, `LandProgress` and `SettleProgressUnreachable` are UI-thread;
        // `CurrentStates` runs on an api thread and only STAGES. The landing reconciles against the resident rows,
        // commits, publishes and writes behind.
        //
        // AUTHORITY (plan §6.2: Seed < Thin < Full(wire) < Local(player)). The fold stages Full, so a stale server
        // position never rewinds what this device just played. ONE refinement, decided by `EpisodeProgress.Landing`: a
        // resident Local row whose wire revision is STRICTLY NEWER (another device played on since) is promoted to Local —
        // Local persists (`progress_auth` is a max on disk), so without it the first local pause would shut every later
        // cross-device position out of that row for good. A row whose local write is as new or newer is WITHDRAWN from the
        // batch, so neither the commit nor the write-behind (which coalesces values under a max authority) touches it.
        //
        // SETTLED WITHOUT AN ANSWER (plan §6.1). A session that cannot reach Spotify never syncs, so no hydrate is ever
        // asked and the show page's visit head would wait on `ProgressSettled` for good. The session's own verdict
        // (`ProgressUnreachable`: Reconnecting or Failed) settles it FAILED instead, for the current scope; that scope's
        // next landed hydrate clears `ProgressFailed` without ever dropping `ProgressSettled` on the way.

        const string CurrentStatesRoute = "/herodotus/spotify.resumption.v1.CurrentStateService/ListCurrentStates";
        const string EpisodePrefix = "spotify:episode:";

        /// <summary>The official client's page size (1.2.96.518, captured 2026-09-19: <c>{limit: 1000}</c> on both its cold
        /// and its filtered sync; the WinUI app's 1021 is retired) — one call, never paged, and the answer carries no page
        /// token. An answer this long may have been cut short (the landing's line says <c>truncated=1</c>); what it
        /// carried still lands, and the next sync asks the whole window again.</summary>
        public const int CurrentStatesLimit = 1000;

        /// <summary>Every hydrate's lookback (0d0429a0 <c>PodcastProgressLookback</c>): 180 days, the whole window on
        /// EVERY sync — the section header says why there is no "since the last sync", and why the official client's
        /// filter-less cold sync is not copied.</summary>
        public const long ProgressLookbackMs = 180L * 24 * 60 * 60 * 1000;

        /// <summary>Where a hydrate asked at <paramref name="nowMs"/> looks back from: <see cref="ProgressLookbackMs"/>
        /// before it, whatever an earlier sync landed. PURE.</summary>
        public static long HydrateSince(long nowMs) => nowMs - ProgressLookbackMs;

        /// <summary>Has the hydrate answered — landed or failed — for the current account scope, or has the session said
        /// it cannot reach Spotify (<see cref="SettleProgressUnreachable"/>)? The show page's visit head waits for it
        /// (plan §6.1: never flash New at a Returning listener). True at once under <c>--fake</c> (plan §9): there is
        /// nothing to hydrate, and the seed's progress is the answer. UI thread only (C1). Read once at the type's first
        /// touch, which <c>App.Main</c> orders after <c>Platform.Boot</c> parses argv.</summary>
        public static readonly Signal<bool> ProgressSettled = new(Platform.Args.Fake);

        /// <summary>The hydrate settled WITHOUT an answer for this account scope — it failed, or the session could not reach
        /// Spotify to ask — and none has landed since: the ledger reads "progress unavailable" and rows show no state
        /// rather than a false "unplayed" (plan §6.1). A reconnect's failed re-sync after a landed one does not raise it —
        /// the rows it landed are still true — and the next landing clears it. UI thread only.</summary>
        public static readonly Signal<bool> ProgressFailed = new(false);

        static Scope? s_hydrating;           // the scope whose hydrate is on the wire (UI thread)
        static Scope? s_progressScope;       // the scope the two signals speak for
        static bool s_progressLanded;        // an answer has landed for it

        /// <summary>The CEL filter <c>ListCurrentStates</c> takes (0d0429a0 <c>SpClient.cs:1418-1426</c>, verbatim): every
        /// state with a revision updated after <paramref name="sinceUnixMs"/>, as an ISO-8601 UTC instant with millis.
        /// PURE.</summary>
        public static string CurrentStatesFilter(long sinceUnixMs)
            => "cs.resume_point_revisions.exists(revision, revision.update_time > timestamp('"
               + DateTimeOffset.FromUnixTimeMilliseconds(sinceUnixMs).UtcDateTime
                     .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
               + "'))";

        /// <summary>Every resume point the account has touched since <paramref name="sinceUnixMs"/>, in ONE call, staged
        /// into <paramref name="s"/> (<see cref="FoldCurrentStates"/>). Returns how many states the answer carried (what
        /// <see cref="CurrentStatesLimit"/> bounds — play-history heads included), or -1 when there is no answer (a
        /// refusal, a transport failure, an unparsable body). A 200 with an EMPTY body is an answer: nothing changed.
        /// Same route shape, host and headers as the resume-point write. API THREAD — blocks, stages, touches no table.</summary>
        public static int CurrentStates(long sinceUnixMs, Staging s, CancellationToken ct)
        {
            var request = new Rs.ListCurrentStatesRequest { Limit = CurrentStatesLimit, Filter = CurrentStatesFilter(sinceUnixMs) };
            Api.Result result = Api.PostEncoded(CurrentStatesRoute, ApiHost.Spclient,
                HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.ContentProtobuf | HeaderSet.AcceptLanguage,
                request.ToByteArray(), "application/x-protobuf", null, ct);
            if (!result.Ok)
            {
                Log.Warn("podcast", "podcast.progress.hydrate refused status=" + result.Status);
                return -1;
            }
            Rs.ListCurrentStatesResponse response;
            try { response = Rs.ListCurrentStatesResponse.Parser.ParseFrom(result.Bytes); }
            catch (InvalidProtocolBufferException ex)
            {
                Log.Warn("podcast", "podcast.progress.hydrate unparsable bytes=" + result.Body.Length, ex);
                return -1;
            }
            FoldCurrentStates(response, s);
            return response.States.Count;
        }

        /// <summary>The fold, PURE over a parsed answer (<c>SpotifyTelemetryTests</c> drives it with answers framed from the
        /// captured bytes). Per state: its CURRENT revision (<see cref="CurrentRevisionOf"/>); the uri is the entry's own,
        /// else its value's (0d0429a0 <c>MapCurrentStateProgress</c>); anything but a <c>spotify:episode:</c> gid — the
        /// play-history head, an album or playlist context, a malformed uri — is skipped, and so is a state with no
        /// revision or no value. The value's oneof arm (<see cref="ArmOf"/>) folds through
        /// <see cref="Episode.Rules.ProgressOf"/> over the Duration in ms (<see cref="PositionMsOf"/>): a position stages
        /// it, marker 3 stages 0, marker 4 stages COMPLETED — <see cref="int.MaxValue"/>, because an api thread may not
        /// read the resident duration (<c>Pct</c> clamps it to 1) — and no arm (or a context arm) stages NOTHING: the row
        /// keeps whatever it knew, never a guessed "completed". A staged row is <see cref="Authority.Full"/> for
        /// <see cref="EpisodeFields.Progress"/> only, in the gid form (no arena text, P14), <c>PlayedAt</c> =
        /// <see cref="PlayedAtOf"/>. Returns the rows staged.</summary>
        public static int FoldCurrentStates(Rs.ListCurrentStatesResponse response, Staging s)
        {
            var states = response.States;
            int staged = 0;
            for (int i = 0; i < states.Count; i++)
            {
                Rs.CurrentStateEntry state = states[i];
                Rs.CurrentStateRevision? revision = CurrentRevisionOf(state);
                Rs.CurrentStateValue? value = revision?.Value;
                if (revision is null || value is null) continue;
                string uri = state.EntityUri.Length > 0 ? state.EntityUri : value.EntityUri;
                if (!uri.StartsWith(EpisodePrefix, StringComparison.Ordinal)
                    || !EntityId.TryParseGid(uri.AsSpan(), out EntityId id) || id.Kind != EntityKind.Episode)
                    continue;
                long positionMs = value.ResumePoint is { } at ? PositionMsOf(at) : 0;
                if (!Episode.Rules.ProgressOf(ArmOf(value), positionMs, durationMs: 0, out int progressMs)) continue;
                ref StagedEpisode row = ref s.Episodes.RowFor(id, Authority.Full, (uint)EpisodeFields.Progress);
                row.ProgressMs = progressMs;
                row.PlayedAt = PlayedAtOf(revision);
                row.RevisionUpdateSeconds = revision.UpdateTime?.Seconds ?? 0;
                row.RevisionUpdateNanos = revision.UpdateTime?.Nanos ?? 0;
                row.RevisionCreateSeconds = revision.CreateTime?.Seconds ?? 0;
                row.RevisionCreateNanos = revision.CreateTime?.Nanos ?? 0;
                staged++;
            }
            return staged;
        }

        /// <summary>A state's CURRENT revision: the newest of its revisions by the server's <c>update_time</c> (the order
        /// herodotus committed them in), else by <c>create_time</c>; a tie keeps the earlier on the wire, which lists the
        /// newest first. A state carries more than one — the captured cold answer's 4FQr held a 131 s position (May 7)
        /// over an older marker 4 (May 1) — and the newest is what the listener did last. Null when it carries none. PURE.</summary>
        public static Rs.CurrentStateRevision? CurrentRevisionOf(Rs.CurrentStateEntry state)
        {
            Rs.CurrentStateRevision? newest = null;
            foreach (Rs.CurrentStateRevision revision in state.Revisions)
                if (newest is null || CompareRevisions(revision, newest) >= 0) newest = revision;
            return newest;
        }

        public static int CompareRevisions(Rs.CurrentStateRevision left, Rs.CurrentStateRevision right)
        {
            int update = CompareTimestamp(left.UpdateTime ?? left.CreateTime, right.UpdateTime ?? right.CreateTime);
            return update != 0 ? update : CompareTimestamp(left.CreateTime, right.CreateTime);
        }

        static int CompareTimestamp(Timestamp? left, Timestamp? right)
        {
            int seconds = (left?.Seconds ?? long.MinValue).CompareTo(right?.Seconds ?? long.MinValue);
            return seconds != 0 ? seconds : (left?.Nanos ?? 0).CompareTo(right?.Nanos ?? 0);
        }

        /// <summary>Which arm of the value's <c>state</c> oneof is set, as <see cref="Episode.Rules.ResumeArm"/> (whose
        /// numbers are the wire's). An arm this proto does not declare is an unknown field, so it reads
        /// <see cref="Episode.Rules.ResumeArm.None"/>. PURE.</summary>
        public static Episode.Rules.ResumeArm ArmOf(Rs.CurrentStateValue value) => value.StateCase switch
        {
            Rs.CurrentStateValue.StateOneofCase.ResumePoint => Episode.Rules.ResumeArm.Position,
            Rs.CurrentStateValue.StateOneofCase.Marker3 => Episode.Rules.ResumeArm.Marker3,
            Rs.CurrentStateValue.StateOneofCase.Marker4 => Episode.Rules.ResumeArm.Marker4,
            Rs.CurrentStateValue.StateOneofCase.Context => Episode.Rules.ResumeArm.Context,
            _ => Episode.Rules.ResumeArm.None,
        };

        /// <summary>When the listener last played the episode, unix SECONDS: the revision's <c>create_time</c> — the
        /// writing client's own instant, which for this device IS the <c>PlayedAt</c> its local mirror stamped, so the
        /// landing compares like with like — else the server's <c>update_time</c>, else 0. <c>create_time</c> first also
        /// makes "new since you were here" honest for a play synced late (an offline phone). PURE.</summary>
        public static int PlayedAtOf(Rs.CurrentStateRevision revision)
        {
            Timestamp? at = revision.CreateTime is { Seconds: > 0 } created ? created : revision.UpdateTime;
            return at is null ? 0 : (int)Math.Clamp(at.Seconds, 0L, int.MaxValue);
        }

        /// <summary>Hydrate the account's podcast progress. UI THREAD; the login sync (<c>Spotify.Library.SyncNow</c>)
        /// calls it on every sync of an account scope — the first after sign-in and each reconnect's. One in flight per
        /// scope; a new scope's is never held behind an old one's (whose landing is dropped, C7). A NEW scope drops
        /// <see cref="ProgressSettled"/> first (what it said was about another account); a reconnect keeps it — as does
        /// the recovery after <see cref="SettleProgressUnreachable"/>, which took this scope as its own — so a page never
        /// falls back to its skeleton mid-visit. The window is <see cref="HydrateSince"/>'s: the whole 180 days.</summary>
        public static void HydrateProgress(Scope scope)
        {
            if (ReferenceEquals(s_hydrating, scope)) return;
            s_hydrating = scope;
            if (!ReferenceEquals(scope, s_progressScope))
            {
                s_progressScope = scope;
                s_progressLanded = false;
                ProgressSettled.Value = false;
                ProgressFailed.Value = false;
            }
            ActivateOutbox(scope.Key.Account);
            SyncCompletion(scope);
            HydrateRecentEpisodes(scope);
            long since = HydrateSince(NowMs());
            uint epoch = scope.Epoch;
            bool queued = Api.Run(() =>
            {
                Staging s = Staging.Rent();
                s.Epoch = epoch;
                long started = Stopwatch.GetTimestamp();
                int states;
                using var accountRequest = Api.ForAccount(scope.Key.Account);
                try { states = CurrentStates(since, s, CancellationToken.None); }
                catch (Exception ex)
                {
                    Log.Warn("podcast", "podcast.progress.hydrate faulted", ex);
                    states = -1;
                }
                long ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Post(() => LandProgress(scope, s, states, since, ms));
            });
            if (queued) return;
            s_hydrating = null;
            SettleProgress(landed: false);
            Log.Warn("podcast", "podcast.progress.hydrate failed: the api queue refused it — progress unavailable");
        }

        /// <summary>UI THREAD: the hydrate's answer lands (the <c>Spotify.Api.Concert.cs</c> host-door shape — commit,
        /// publish, write behind). An answer for a scope that is no longer current is dropped whole (C7): the new scope's
        /// own sync asks again. A failure settles as FAILED; an answer settles as landed, which clears a failure this scope
        /// was left with (a failed sync, or an unreachable session).</summary>
        static void LandProgress(Scope scope, Staging s, int states, long since, long ms)
        {
            if (ReferenceEquals(s_hydrating, scope)) s_hydrating = null;
            bool owned = false;
            try
            {
                if (!ReferenceEquals(scope, Entities.Current) || scope.Epoch != s.Epoch)
                {
                    Log.Info("podcast", "podcast.progress.hydrate dropped: the scope moved on while it was on the wire");
                    return;
                }
                string sinceText = DateTimeOffset.FromUnixTimeMilliseconds(since).UtcDateTime
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                if (states < 0)
                {
                    SettleProgress(landed: false);
                    Log.Warn("podcast", "podcast.progress.hydrate states=-1 ms=" + ms + " since=" + sinceText
                        + " failed=1 — " + (s_progressLanded ? "the landed rows stand" : "progress unavailable"));
                    return;
                }
                int episodes = s.Episodes.Count;
                int promoted = ReconcileProgress(s, scope.Episodes, out int kept);
                Entities.Commit(s);
                Entities.Publish();
                owned = Store.WriteBehind(s);
                SettleProgress(landed: true);
                Log.Info("podcast", "podcast.progress.hydrate states=" + states + " ms=" + ms + " since=" + sinceText
                    + " episodes=" + episodes + " promoted=" + promoted + " kept=" + kept
                    + (states >= CurrentStatesLimit ? " truncated=1" : ""));
            }
            finally { if (!owned) Staging.Return(s); }
        }

        /// <summary>UI THREAD, before the commit: each staged row against its RESIDENT twin, by
        /// <see cref="EpisodeProgress.Landing"/>. A row not in memory lands as staged (Full). Promote raises the row to
        /// Local; Skip withdraws its group (<c>Known = 0</c>: the commit writes nothing and the write-behind carries no
        /// value). Returns the rows promoted; <paramref name="kept"/> counts those this device's own write kept. Public for
        /// the facts (this assembly has no <c>InternalsVisibleTo</c>).</summary>
        public static int ReconcileProgress(Staging s, EpisodeTable t, out int kept)
        {
            kept = 0;
            int promoted = 0;
            Span<StagedEpisode> rows = s.Episodes.Span;
            for (int i = 0; i < rows.Length; i++)
            {
                ref StagedEpisode row = ref rows[i];
                if (!t.TryGetSlot(row.Id.Packed, out int slot)) continue;          // the fold stages the gid form only
                var incoming = new EpisodeRevisionStamp(row.RevisionUpdateSeconds, row.RevisionUpdateNanos,
                    row.RevisionCreateSeconds != 0 ? row.RevisionCreateSeconds : row.PlayedAt, row.RevisionCreateNanos);
                var resident = new EpisodeRevisionStamp(t.RevisionUpdateSeconds[slot], t.RevisionUpdateNanos[slot],
                    t.RevisionCreateSeconds[slot] != 0 ? t.RevisionCreateSeconds[slot] : t.PlayedAt[slot], t.RevisionCreateNanos[slot]);
                bool knows = t.Knows(slot, (uint)EpisodeFields.Progress);
                var landing = !knows ? EpisodeProgress.HydrateLanding.Land
                    : incoming.CompareForLanding(resident) <= 0 ? EpisodeProgress.HydrateLanding.Skip
                    : (Authority)t.ProgressAuthority[slot] == Authority.Local ? EpisodeProgress.HydrateLanding.Promote
                    : EpisodeProgress.HydrateLanding.Land;
                switch (landing)
                {
                    case EpisodeProgress.HydrateLanding.Promote:
                        row.Authority = Authority.Local;
                        promoted++;
                        break;
                    case EpisodeProgress.HydrateLanding.Skip:
                        row.Known &= ~(uint)EpisodeFields.Progress;
                        kept++;
                        break;
                }
            }
            return promoted;
        }

        /// <summary>Settle the two signals for <see cref="s_progressScope"/>: settled either way; failed only while no
        /// answer has landed for this scope.</summary>
        static void SettleProgress(bool landed)
        {
            if (landed) s_progressLanded = true;
            ProgressFailed.Value = !s_progressLanded;
            ProgressSettled.Value = true;
        }

        /// <summary>Does the session's phase say this device cannot reach Spotify — the one fact that settles the hydrate
        /// FAILED without an answer (plan §6.1)? PURE, and exactly two phases, each the session's own verdict (no timer):
        /// <list type="bullet">
        /// <item><see cref="SessionPhase.Failed"/> — terminal for this credential: none stored, rejected, not Premium, a
        /// refused login. Only a new login leaves it.</item>
        /// <item><see cref="SessionPhase.Reconnecting"/> — a transport the session needs dropped and is serving its backoff.
        /// Before this login's first Online that is THE OFFLINE LAUNCH: the AP ladder could not reach an access point
        /// (apresolve or the socket failed, <c>ApDropped</c> with <see cref="SessionFault.Network"/>) and retries on
        /// 3/6/12/24/30 s forever. After it, a dealer drop — whose Online edge already asked the hydrate, so
        /// <see cref="SettlesUnreachable"/> finds it landed, on the wire or already failed, and says nothing.</item>
        /// </list>
        /// <see cref="SessionPhase.Offline"/> is deliberately NOT one: it is the boot state until <c>App.Main</c>'s posted
        /// login starts the ladder, so settling on it would read "progress unavailable" for the first second of every
        /// launch; in the app, only a sign-out returns to it, and that moves the scope. A transient first failure of the
        /// ladder does settle FAILED — honestly, for those seconds — and the landing that follows the recovery clears it.</summary>
        public static bool ProgressUnreachable(SessionPhase phase) => phase is SessionPhase.Reconnecting or SessionPhase.Failed;

        /// <summary>THE settle-without-an-answer rule, PURE: FAILED when the phase is unreachable
        /// (<see cref="ProgressUnreachable"/>), no hydrate for the scope is on the wire (its own landing settles it), none
        /// has landed for it (the rows it landed are still true — a later dealer drop never raises "progress unavailable"
        /// over them), and the scope is not already settled FAILED (the ladder re-enters Reconnecting on every retry, and
        /// the verdict is said once).</summary>
        public static bool SettlesUnreachable(SessionPhase phase, bool onTheWire, bool landed, bool alreadyFailed)
            => ProgressUnreachable(phase) && !onTheWire && !landed && !alreadyFailed;

        /// <summary>UI THREAD: settle the two signals FAILED for <paramref name="scope"/> when the session cannot reach
        /// Spotify (<see cref="SettlesUnreachable"/>). The library host posts it on every session transition into a phase
        /// <see cref="ProgressUnreachable"/> names (<c>Spotify.Library.WatchSession</c>). The scope becomes the one the
        /// signals speak for, so the sync that follows the session's recovery is that scope's reconnect: it keeps
        /// <see cref="ProgressSettled"/> (no skeleton mid-visit) and its landing clears <see cref="ProgressFailed"/>.
        /// One always-on line per settle.</summary>
        public static void SettleProgressUnreachable(Scope scope)
        {
            var session = Current;                         // Spotify.Current (the session value), not the scope
            bool same = ReferenceEquals(scope, s_progressScope);
            if (!SettlesUnreachable(session.Phase, ReferenceEquals(s_hydrating, scope), same && s_progressLanded,
                    same && ProgressSettled.Peek() && ProgressFailed.Peek()))
                return;
            if (!same)
            {
                s_progressScope = scope;
                s_progressLanded = false;
            }
            SettleProgress(landed: false);
            Log.Warn("podcast", "podcast.progress.hydrate unreachable phase=" + session.Phase + " fault=" + session.Fault
                + " — progress unavailable until a sync lands");
        }

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
