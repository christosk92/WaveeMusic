// ── Spotify/Spotify.Egress.cs ─────────────────────────────────────────────────────────────────────────────────────────
// THE rule for what may leave this process for Spotify's servers: only `spotify:` identities. A local file's id is
// `wavee:local:file:<base64url(full path)>` — the user's folders and Windows account name — and a module, a session
// playlist, the synthetic podcast source and the demo catalog are no business of Spotify's either. 0.3.3 published a
// playing local file to connect-state verbatim (the service answered 422 and the path had already left).
//
// Role: CORE
// Owner: E
// Budget: 300 lines
//
// PURE: no clock, no table, no interner (an EntityId's provider is a field read). Every builder that puts an identity,
// a uri or a cover on a Spotify wire asks here — the protobuf writer (`Decode.ProtoWriter.Id` / `EntryId`), the
// put-state encoder (`ForConnect`), and the telemetry / history / lyrics / queue builders — so a new caller cannot
// leak by forgetting a check of its own.
//
// A playing LOCAL track is told to connect-state the way the official client tells it: `spotify:local:<artist>:<album>:
// <title>:<seconds>`, each part form-encoded — its tags, never its path. Anything else that is not Spotify's (a module
// stream, a session podcast) publishes the device with an empty player half: still the active device, nothing named.

using System.Text;

using FluentGpu.Foundation;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>What may be sent to Spotify. PURE and thread-safe.</summary>
    public static class Egress
    {
        /// <summary>A Spotify identity: <c>spotify:</c>-prefixed by construction (a gid formats as one; a text-form id is
        /// <see cref="EntityProvider.Spotify"/> only when its text starts with it). Empty answers false.</summary>
        public static bool Admits(EntityId id) => id.Provider == EntityProvider.Spotify;

        /// <inheritdoc cref="Admits(EntityId)"/>
        public static bool Admits(scoped ReadOnlySpan<byte> uri) => uri.StartsWith("spotify:"u8);

        /// <inheritdoc cref="Admits(EntityId)"/>
        public static bool Admits(scoped ReadOnlySpan<char> uri) => uri.StartsWith("spotify:", StringComparison.Ordinal);

        /// <summary><paramref name="id"/> when it may leave, else <c>default</c> (which every writer skips).</summary>
        public static EntityId Filter(EntityId id) => Admits(id) ? id : default;

        /// <summary><paramref name="uri"/> when it may leave, else "".</summary>
        public static string Filter(string? uri) => uri is not null && Admits(uri.AsSpan()) ? uri : "";

        /// <summary>Does a serialized body name a Wavee-only uri (<c>wavee:local:file:…</c> and friends)? The backstop
        /// for a body built from strings (a gabo event, a journal replayed from disk): such a body is not sent.</summary>
        public static bool NamesWaveeUri(scoped ReadOnlySpan<byte> body) => body.IndexOf("wavee:"u8) >= 0;

        /// <summary>A cover that may be named on the wire: a bare image id, a <c>spotify:image:</c> uri, or an https url
        /// on Spotify's own CDNs. A local file's art (a path, a <c>file:</c> or <c>data:</c> uri) and a module's
        /// thumbnail are not.</summary>
        public static bool AdmitsImage(scoped ReadOnlySpan<char> image)
        {
            if (image.IsEmpty) return false;
            if (image.StartsWith("spotify:", StringComparison.Ordinal)) return true;
            if (image.StartsWith("https://", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> rest = image["https://".Length..];
                int slash = rest.IndexOf('/');
                ReadOnlySpan<char> host = slash < 0 ? rest : rest[..slash];
                return host.EndsWith(".scdn.co", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".spotifycdn.com", StringComparison.OrdinalIgnoreCase);
            }
            foreach (char c in image)                          // a bare id: hex / base62, nothing path-shaped
                if (!char.IsAsciiLetterOrDigit(c)) return false;
            return true;
        }

        /// <summary>A video manifest gid that may be named (<c>associated_video_id</c>, <c>media.manifest_id</c>): hex
        /// only. A user's attached video file lives in its own column and never here; this keeps it that way.</summary>
        public static bool AdmitsVideoGid(scoped ReadOnlySpan<char> gid)
        {
            if (gid.IsEmpty || gid.Length > 64) return false;
            foreach (char c in gid) if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }

        // ── a local track, as the official client names it ──────────────────────────────────────────────────────────

        /// <summary>The longest <c>spotify:local:</c> uri written; a longer one is not written at all (the put-state then
        /// carries no player half rather than a truncated name).</summary>
        public const int MaxLocalUriBytes = 768;

        /// <summary><c>spotify:local:&lt;artist&gt;:&lt;album&gt;:&lt;title&gt;:&lt;seconds&gt;</c> into <paramref name="into"/>:
        /// each part UTF-8 and form-encoded (unreserved <c>A-Za-z0-9-_.*</c> kept, space as <c>+</c>, everything else
        /// <c>%XX</c>), so no part can contain a <c>:</c>. Returns the bytes written, or 0 when there is no title or it
        /// would not fit. Built from the row's TAGS only — the file's path is never an input.</summary>
        public static int LocalTrackUri(string? artist, string? album, string? title, long durationMs, Span<byte> into)
        {
            if (string.IsNullOrEmpty(title)) return 0;
            int n = 0;
            if (!Put("spotify:local:"u8, into, ref n)) return 0;
            if (!Encode(artist, into, ref n) || !Put(":"u8, into, ref n)) return 0;
            if (!Encode(album, into, ref n) || !Put(":"u8, into, ref n)) return 0;
            if (!Encode(title, into, ref n) || !Put(":"u8, into, ref n)) return 0;
            if (!System.Buffers.Text.Utf8Formatter.TryFormat(Math.Max(0L, durationMs) / 1000, into[n..], out int digits)) return 0;
            return n + digits;

            static bool Put(ReadOnlySpan<byte> bytes, Span<byte> into, ref int n)
            {
                if (bytes.Length > into.Length - n) return false;
                bytes.CopyTo(into[n..]);
                n += bytes.Length;
                return true;
            }

            static bool Encode(string? text, Span<byte> into, ref int n)
            {
                if (string.IsNullOrEmpty(text)) return true;
                Span<byte> utf8 = stackalloc byte[4];
                foreach (Rune rune in text.EnumerateRunes())
                {
                    int count = rune.EncodeToUtf8(utf8);
                    for (int i = 0; i < count; i++)
                    {
                        byte b = utf8[i];
                        if (n + 3 > into.Length) return false;
                        if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                            or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'*') into[n++] = b;
                        else if (b == (byte)' ') into[n++] = (byte)'+';
                        else
                        {
                            into[n++] = (byte)'%';
                            into[n++] = Hex(b >> 4);
                            into[n++] = Hex(b & 0xF);
                        }
                    }
                }
                return true;
            }

            static byte Hex(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
        }

        /// <summary>The deck's track uri as connect-state is told it, into <paramref name="into"/>: a Spotify id formatted,
        /// a local track as <see cref="LocalTrackUri"/> from the window's tags, anything else nothing (0).</summary>
        public static int TrackUri(in Playback.Snapshot snap, Span<byte> into)
        {
            if (!snap.HasTrack || snap.Track.IsEmpty) return 0;
            if (Admits(snap.Track))
                return snap.Track.Form != EntityForm.Gid && snap.Track.FormattedLength * 3 > into.Length ? 0 : snap.Track.Format(into);
            if (snap.Track.Provider != EntityProvider.Local || snap.Track.Kind != EntityKind.Track || snap.Wire.Window is not { } window)
                return 0;
            ref readonly Playback.WireRow row = ref window.Current;
            return LocalTrackUri(row.ArtistName, row.AlbumTitle, row.Title, snap.DurationMs, into);
        }

        // ── the put-state snapshot ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The snapshot a connect-state PUT may encode. Unchanged (no allocation) when everything in it is
        /// Spotify's — the common case. Otherwise: a non-Spotify context, its play origin, every non-Spotify
        /// prev/next row and every non-Spotify album / artist / cover are dropped; a local track on the deck stays,
        /// to be named by <see cref="TrackUri"/> (audio, no video offer, no cover); and a deck track that has no
        /// Spotify name at all (a module stream, a session podcast, a local file without a title) leaves the player
        /// half EMPTY — the device and its is_active as they were, so the cluster keeps us as the owner without
        /// naming anything. PURE.</summary>
        public static Playback.Snapshot ForConnect(in Playback.Snapshot s)
        {
            if (!s.HasTrack) return s;                         // the encoder writes no player half: nothing to name
            bool trackOk = Admits(s.Track);
            bool contextOk = s.Context.IsEmpty || Admits(s.Context);
            bool windowOk = s.Wire.Window is not { } held || Clean(held);
            bool videoOk = s.VideoGid.Length == 0 || AdmitsVideoGid(s.VideoGid);
            if (trackOk && contextOk && windowOk && videoOk) return s;

            bool local = !trackOk && TrackUri(in s, stackalloc byte[MaxLocalUriBytes]) > 0;
            Playback.WireWindow? window = !trackOk && !local ? null
                : s.Wire.Window is { } w ? Cleaned(w, dropCover: local) : null;
            var wire = new Playback.WireExtras(window, window is null ? 0 : s.Wire.QueueRevision, window is null ? default : s.Wire.Ids,
                s.Wire.PrivateSession, s.Wire.OutputDevice, s.Wire.CommandSender, s.Wire.PlaybackRate,
                contextOk && window is not null ? s.Wire.Origin : null, s.Wire.OutputType);
            if (!trackOk && !local)
                return new Playback.Snapshot(in s.Device, s.IsActive, s.Reason, s.MessageId, s.Volume, s.ClientTimestampMs,
                    s.StartedPlayingAtMs, s.HasBeenPlayingForMs, false, default, default, "", 0, s.TimestampMs, 0,
                    false, false, false, false, Decode.RepeatMode.Off, Playback.PlayableKind.Audio,
                    false, s.LastCommandMessageId, null, in wire);
            return new Playback.Snapshot(in s.Device, s.IsActive, s.Reason, s.MessageId, s.Volume, s.ClientTimestampMs,
                s.StartedPlayingAtMs, s.HasBeenPlayingForMs, true, s.Track, contextOk ? s.Context : default, s.Uid,
                s.PositionAsOfMs, s.TimestampMs, s.DurationMs, s.IsPlaying, s.IsPaused, s.IsBuffering, s.Shuffling, s.Repeat,
                local ? Playback.PlayableKind.Audio : s.Kind, !local && s.HasVideo, s.LastCommandMessageId,
                local || !videoOk ? null : s.VideoGid, in wire);
        }

        /// <summary>Is every row of <paramref name="window"/> already fit to leave?</summary>
        static bool Clean(Playback.WireWindow window)
        {
            if (!Clean(in window.Current)) return false;
            foreach (ref readonly Playback.WireRow row in window.Prev) if (!Admits(row.Id) || !Clean(in row)) return false;
            foreach (ref readonly Playback.WireRow row in window.Next) if (!Admits(row.Id) || !Clean(in row)) return false;
            return true;
        }

        static bool Clean(in Playback.WireRow row)
            => (row.AlbumId.IsEmpty || Admits(row.AlbumId)) && (row.ArtistId.IsEmpty || Admits(row.ArtistId))
               && (row.Image.Length == 0 || AdmitsImage(row.Image));

        /// <summary>The window again with its non-Spotify rows dropped and its rows' non-Spotify ids and covers cleared.</summary>
        static Playback.WireWindow Cleaned(Playback.WireWindow window, bool dropCover)
        {
            Playback.WireRow current = Scrub(in window.Current, dropCover);
            return new Playback.WireWindow(in current, Kept(window.Prev), Kept(window.Next), window.ContextIndex,
                Filter(window.AutoplayStation));

            static Playback.WireRow[] Kept(ReadOnlySpan<Playback.WireRow> rows)
            {
                int n = 0;
                foreach (ref readonly Playback.WireRow row in rows) if (Admits(row.Id)) n++;
                var kept = new Playback.WireRow[n];
                n = 0;
                foreach (ref readonly Playback.WireRow row in rows) if (Admits(row.Id)) kept[n++] = Scrub(in row, dropCover: false);
                return kept;
            }
        }

        static Playback.WireRow Scrub(in Playback.WireRow row, bool dropCover)
            => new(Filter(row.Id), row.ItemId, row.UidText, row.Provider, row.Title, row.ArtistName, row.AlbumTitle,
                dropCover || !AdmitsImage(row.Image) ? null : row.Image, Filter(row.AlbumId), Filter(row.ArtistId),
                row.DecisionId, row.QueuedBy);
    }
}
