// ── Playback/Playback.Wire.cs ───────────────────────────────────────────────────────────────────────────────────────
// The value a connect-state PUT encodes: this device's identity, the snapshot, and its parity half — the prev/next
// window with per-row metadata, the session/playback ids, the queue revision, the private-session flag, the output
// device and the controller command the PUT answers (gap batch R4-1)
//
// Role: CORE
// Owner: G
// Wave: gap batch R4-1
// Budget: 400 lines
// Spec: gap register G-240 (thin PutState), G-246 (command ageing); 0.2.9 `SpotifyLive/ConnectStateBuilder.cs`,
//       `Backend/DeviceStatePublisher.cs` (the snapshot it built and the 10 s attribution window)
//
// A NAMED PARTIAL OF `Playback.cs` — its §10 moved here whole, declared when the reducer would have passed 30 % over its
// 1,790-line budget with the parity half added. The same CORE contract: no clock (the caller stamps), no I/O, no
// interner, no table read. `Playback.Host.Wire.cs` (SHELL) captures the parity half on the UI thread; the encoder
// (`Spotify.Decode.PutState.cs`) reads it on an api thread — which is why everything a snapshot references is IMMUTABLE
// once built: a `WireWindow` is never written after its constructor returns, so ten coalesced captures and the one PUT
// that goes out can share it with no lock and no copy.
//
//   Snapshot ── us: DeviceIdentity · is_active · reason · volume · started_playing_at · has_been_playing_for
//            ├─ the deck: track · uid · context · position · duration · playing/paused/buffering · options · kind · video
//            └─ Wire: WireWindow (≤ 50 prev · current · ≤ 50 next, per-row metadata) · queue_revision · WireIds ·
//                     private session · output device · last command sender

using FluentGpu.Foundation;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Playback
{
    // ── 10. the snapshot the PUT body encodes ───────────────────────────────────────────────────────────────────────

    /// <summary>This device's Connect identity — the constants a PUT body carries about US, filled once at boot and
    /// never derived from state. Kept out of <see cref="State"/> because none of it ever changes and a reducer that
    /// copies six strings per Step is a reducer nobody will keep pure.</summary>
    /// <param name="DeviceId">Our persisted, launch-stable device id.</param>
    /// <param name="DeviceName">What the picker shows for us.</param>
    /// <param name="ClientId">The keymaster client id we authenticated with.</param>
    /// <param name="Platform">`PrivateDeviceInfo.platform`.</param>
    /// <param name="SoftwareVersion">`DeviceInfo.device_software_version`.</param>
    /// <param name="SpircVersion">`DeviceInfo.spirc_version`.</param>
    public readonly record struct DeviceIdentity(
        string DeviceId,
        string DeviceName,
        string ClientId,
        string Platform,
        string SoftwareVersion,
        string SpircVersion);

    /// <summary>The per-session ids the player state names (0.2.9 <c>DeviceStatePublisher</c>): <c>session_id</c> and
    /// <c>session_command_id</c> turn over with a new session (a new play, or a new context), <c>playback_id</c> with every
    /// row's registration (it IS the registration's playback id, as 0.2.9's was), and the interaction / page-instance ids
    /// ride every row's metadata. Zero = not minted yet, and the encoder writes nothing for it.</summary>
    public readonly record struct WireIds(UInt128 SessionId, UInt128 PlaybackId, UInt128 SessionCommandId,
        UInt128 InteractionId, UInt128 PageInstanceId);

    /// <summary>One queue row as the PUT names it (<c>ProvidedTrack</c>): its identity, its server uid, its provenance and
    /// the display picks a controller paints before it fetches anything. Strings are the catalog's INTERNED ones, resolved
    /// on the UI thread when the window was built — the encoder formats, it never resolves.</summary>
    public readonly struct WireRow
    {
        public readonly EntityId Id;
        /// <summary>The queue edge's item id: a 16-hex uid packed, a booked uid's key, or 0 (no uid).</summary>
        public readonly ulong ItemId;
        public readonly QueueProvider Provider;
        public readonly EntityId AlbumId, ArtistId;
        readonly string? _uidText, _title, _artistName, _albumTitle, _image;

        /// <summary>A uid kept as TEXT (20 hex, <c>q2</c> — <see cref="UidBook"/>); "" when <see cref="ItemId"/> formats itself.</summary>
        public string UidText => _uidText ?? "";
        public string Title => _title ?? "";
        public string ArtistName => _artistName ?? "";
        public string AlbumTitle => _albumTitle ?? "";
        /// <summary>The cover as the catalog holds it: a bare image id or a url (the encoder writes <c>spotify:image:</c>).</summary>
        public string Image => _image ?? "";

        public WireRow(EntityId id, ulong itemId, string? uidText, QueueProvider provider, string? title = null,
            string? artistName = null, string? albumTitle = null, string? image = null, EntityId albumId = default,
            EntityId artistId = default)
        {
            Id = id; ItemId = itemId; _uidText = uidText; Provider = provider;
            _title = title; _artistName = artistName; _albumTitle = albumTitle; _image = image;
            AlbumId = albumId; ArtistId = artistId;
        }
    }

    /// <summary>The queue around the deck as the PUT carries it: up to <see cref="MaxPrev"/> already-played rows (the
    /// newest), the deck row's metadata, and up to <see cref="MaxNext"/> rows that follow. IMMUTABLE — the arrays are
    /// copied in and never written again, so a captured window can cross to the encoder's thread and be shared by every
    /// snapshot that coalesces onto one PUT (the file header).</summary>
    public sealed class WireWindow
    {
        /// <summary>0.2.9's <c>MaxWirePrevTracks</c> / <c>MaxWireNextTracks</c>.</summary>
        public const int MaxPrev = 50, MaxNext = 50;

        readonly WireRow[] _prev, _next;

        /// <summary>The deck row: its provenance and display picks (its uri and uid are the snapshot's own).</summary>
        public readonly WireRow Current;
        /// <summary>The deck row's position among the CONTEXT's rows (<c>index.track</c>): the context-provided rows before it.</summary>
        public readonly int ContextIndex;

        public ReadOnlySpan<WireRow> Prev => _prev;
        public ReadOnlySpan<WireRow> Next => _next;

        /// <summary>Copies the NEWEST <see cref="MaxPrev"/> of <paramref name="prev"/> and the FIRST <see cref="MaxNext"/>
        /// of <paramref name="next"/>, in reading order.</summary>
        public WireWindow(in WireRow current, ReadOnlySpan<WireRow> prev, ReadOnlySpan<WireRow> next, int contextIndex)
        {
            Current = current;
            _prev = (prev.Length > MaxPrev ? prev[(prev.Length - MaxPrev)..] : prev).ToArray();
            _next = (next.Length > MaxNext ? next[..MaxNext] : next).ToArray();
            ContextIndex = Math.Max(0, contextIndex);
        }
    }

    /// <summary>The PUT's parity half beside the deck: what 0.2.9 sent and 0.3's first encoder did not (G-240). A value;
    /// its one reference (<see cref="Window"/>) is immutable.</summary>
    public readonly struct WireExtras
    {
        /// <summary>Null while we are not the active device, or have nothing on the deck.</summary>
        public readonly WireWindow? Window;
        /// <summary><c>queue_revision</c>: moves whenever the queue, the deck row or the options did. 0 = not stated.</summary>
        public readonly ulong QueueRevision;
        public readonly WireIds Ids;
        /// <summary><c>DeviceInfo.is_private_session</c> — the setting, honoured on the wire.</summary>
        public readonly bool PrivateSession;
        readonly string? _outputDevice, _commandSender;

        /// <summary><c>audio_output_device_info.device_name</c>: the OS endpoint we render to, "" when unknown.</summary>
        public string OutputDevice => _outputDevice ?? "";
        /// <summary><c>last_command_sent_by_device_id</c>: the controller the PUT answers, "" outside the attribution window.</summary>
        public string CommandSender => _commandSender ?? "";

        public WireExtras(WireWindow? window, ulong queueRevision, in WireIds ids, bool privateSession, string? outputDevice,
            string? commandSender)
        {
            Window = window; QueueRevision = queueRevision; Ids = ids; PrivateSession = privateSession;
            _outputDevice = outputDevice; _commandSender = commandSender;
        }
    }

    /// <summary>How long a controller's command owns the PUTs that follow it (G-246). 0.2.9's publisher credited a PUT
    /// to the last command only within 10 s: an attribution older than that belongs to a different change (a purely local
    /// edit minutes later, the inactive announce) and crediting it blames the wrong sender. PURE.</summary>
    public static class CommandAttribution
    {
        public const long WindowMs = 10_000;

        /// <summary>Is a command stamped at <paramref name="stampedAtMs"/> still the one a PUT at <paramref name="nowMs"/>
        /// answers? Both on the host's frame clock.</summary>
        public static bool Fresh(long stampedAtMs, long nowMs) => nowMs - stampedAtMs < WindowMs;

        /// <summary><paramref name="messageId"/> inside the window, else 0 (not written).</summary>
        public static uint MessageId(uint messageId, long stampedAtMs, long nowMs)
            => messageId != 0 && Fresh(stampedAtMs, nowMs) ? messageId : 0u;
    }

    /// <summary>THE value <c>Spotify.Decode.PutState</c> encodes — our player state as one flat, self-contained
    /// struct.
    ///
    /// <para><b>Why a snapshot and not <see cref="State"/> itself.</b> The PUT runs on an api thread and
    /// <see cref="State"/> is the UI thread's (C1); and half of what the body needs (the device identity, the wire
    /// volume scale, the message id) is not playback state at all. The host captures this ON the UI thread, inside
    /// the drain, and hands the value across — so the encoder can never read a table, a signal or the reducer.</para>
    ///
    /// <para><b>The identities travel packed.</b> <see cref="Track"/> and <see cref="Context"/> are
    /// <see cref="EntityId"/>s, written to the wire through <c>EntityId.Format(Span&lt;byte&gt;)</c> — the same call
    /// the staged uri uses — so the encoder allocates nothing.</para></summary>
    public readonly struct Snapshot
    {
        // ── us ──
        public readonly DeviceIdentity Device;
        /// <summary>The wire's is_active. Its ONE writer is <see cref="Ownership.IsActiveOnWire"/>.</summary>
        public readonly bool IsActive;
        public readonly PublishReason Reason;
        public readonly uint MessageId;
        /// <summary>OUR device's volume on the wire scale, 0..<see cref="MaxWireVolume"/>. Always ours, even while a
        /// foreign device owns playback — a non-active Wavee still reports its real volume.</summary>
        public readonly int Volume;
        public readonly long ClientTimestampMs;
        public readonly long StartedPlayingAtMs;
        public readonly long HasBeenPlayingForMs;

        // ── the player state ──
        public readonly bool HasTrack;
        public readonly EntityId Track;
        public readonly EntityId Context;
        /// <summary>The queue item id for the current row ("" when we minted the session ourselves).</summary>
        public readonly string Uid;
        public readonly long PositionAsOfMs;
        public readonly long TimestampMs;
        public readonly long DurationMs;
        /// <summary>Audible. The WIRE's is_playing is this OR paused OR buffering — desktop keeps it true while paused
        /// (the transport is engaged, the audio frozen), and 0.3 read a pause as a stop until G-240.</summary>
        public readonly bool IsPlaying;
        public readonly bool IsPaused;
        /// <summary>Refilling, or still opening the row (a load is the transport engaged before its first frame).</summary>
        public readonly bool IsBuffering;
        public readonly bool Shuffling;
        public readonly RepeatMode Repeat;
        /// <summary>Decides `track_player` — <see cref="MediaSwitch.TrackPlayer"/>.</summary>
        public readonly PlayableKind Kind;
        /// <summary>The current row carries a music video (G-143). False while we are not the active device.</summary>
        public readonly bool HasVideo;
        /// <summary>The current row's video manifest gid, 32 hex characters — what the encoder writes as the offer's
        /// <c>associated_video_id</c> and the switch-to-video signal. "" whenever <see cref="HasVideo"/> is false (and on a
        /// <c>default</c> snapshot).</summary>
        public string VideoGid => _videoGid ?? "";
        readonly string? _videoGid;
        /// <summary>The controller command this PUT answers (<c>last_command_message_id</c>, G-073), already aged by
        /// <see cref="CommandAttribution"/> (G-246); 0 = none.</summary>
        public readonly uint LastCommandMessageId;
        /// <summary>The parity half (G-240): the queue window, the ids, the revision, the device facts.</summary>
        public readonly WireExtras Wire;

        public Snapshot(in DeviceIdentity device, bool isActive, PublishReason reason, uint messageId, int volume,
            long clientTimestampMs, long startedPlayingAtMs, long hasBeenPlayingForMs,
            bool hasTrack, EntityId track, EntityId context, string uid,
            long positionAsOfMs, long timestampMs, long durationMs,
            bool isPlaying, bool isPaused, bool isBuffering, bool shuffling, RepeatMode repeat, PlayableKind kind,
            bool hasVideo = false, uint lastCommandMessageId = 0, string? videoGid = null, in WireExtras wire = default)
        {
            Device = device; IsActive = isActive; Reason = reason; MessageId = messageId; Volume = volume;
            ClientTimestampMs = clientTimestampMs; StartedPlayingAtMs = startedPlayingAtMs;
            HasBeenPlayingForMs = hasBeenPlayingForMs;
            HasTrack = hasTrack; Track = track; Context = context; Uid = uid;
            PositionAsOfMs = positionAsOfMs; TimestampMs = timestampMs; DurationMs = durationMs;
            IsPlaying = isPlaying; IsPaused = isPaused; IsBuffering = isBuffering;
            Shuffling = shuffling; Repeat = repeat; Kind = kind;
            HasVideo = hasVideo; LastCommandMessageId = lastCommandMessageId;
            _videoGid = hasVideo ? videoGid : null;
            Wire = wire;
        }

        /// <summary>The same snapshot with the message id the glue actually SENT it under. The id is minted inside
        /// the debounce (ten captures in one window become one PUT), so the capture cannot know it — and the ownership
        /// fold needs the number that went on the wire, because the response quoting it is the verdict (C5).</summary>
        public Snapshot WithMessageId(uint messageId)
            => new(in Device, IsActive, Reason, messageId, Volume, ClientTimestampMs, StartedPlayingAtMs,
                HasBeenPlayingForMs, HasTrack, Track, Context, Uid, PositionAsOfMs, TimestampMs, DurationMs,
                IsPlaying, IsPaused, IsBuffering, Shuffling, Repeat, Kind, HasVideo, LastCommandMessageId, VideoGid, in Wire);

        /// <summary>Capture the current state for an announce. PURE — the caller supplies the clock (a unix-ms stamp)
        /// and the message id, so a unit test pins the exact body a given state produces.
        ///
        /// <para>While we are NOT the active device the PLAYER half is empty but the DEVICE half is not: librespot
        /// publishes an idle player_state plus our own volume rather than mirroring the foreign device's row, and
        /// mirroring it is how 0.2.9 announced a phone's track as ours.</para></summary>
        /// <param name="hasVideo">The current row's video association, read off its table by the host (the core's
        /// snapshot capture reads no table of its own).</param>
        /// <param name="videoGid">That row's 32-hex video gid, read off the same table; kept only with
        /// <paramref name="hasVideo"/> while we are the active device.</param>
        /// <param name="wire">The parity half the host captured (<c>Playback.Host.Wire.cs</c>).</param>
        public static Snapshot Of(in State s, in DeviceIdentity device, PublishReason reason, uint messageId,
            long unixMs, long frameNowMs, string uid = "", bool hasVideo = false, string videoGid = "",
            in WireExtras wire = default)
        {
            bool active = Ownership.IsActiveOnWire(in s.Own);
            int volume = (int)Math.Round(Math.Clamp(s.Volume, 0f, 1f) * MaxWireVolume);
            uint command = CommandAttribution.MessageId(s.LastCommandMessageId, s.LastCommandAtMs, frameNowMs);
            if (!active)
                return new Snapshot(in device, false, reason, messageId, volume, unixMs, 0, 0,
                    false, default, default, "", 0, unixMs, 0, false, false, false, false, RepeatMode.Off,
                    PlayableKind.Audio, false, command, null, in wire);

            return new Snapshot(in device, true, reason, messageId, volume, unixMs,
                s.StartedPlayingAtMs, s.HasBeenPlayingForMs,
                s.HasCurrent, s.CurrentId, s.Context, uid,
                s.Position(frameNowMs), unixMs, s.DurationMs,
                s.Phase == Phase.Playing, s.Phase == Phase.Paused, s.Buffering || s.Phase == Phase.Loading,
                s.Shuffle, s.Repeat, s.Kind, hasVideo && s.HasCurrent, command, videoGid, in wire);
        }

        /// <summary>The BecameInactive PUT a sign-out sends while playback is still ours (G-036): the device half and our
        /// volume, is_active false, no player half — the same body the reducer's own inactive announce would carry, built
        /// before the session that could send it is torn down. PURE.</summary>
        public static Snapshot Retiring(in State s, in DeviceIdentity device, long unixMs, in WireExtras wire = default)
            => new(in device, false, PublishReason.BecameInactive, 0,
                (int)Math.Round(Math.Clamp(s.Volume, 0f, 1f) * MaxWireVolume), unixMs, 0, 0,
                false, default, default, "", 0, unixMs, 0, false, false, false, false, RepeatMode.Off, PlayableKind.Audio,
                wire: in wire);
    }
}
