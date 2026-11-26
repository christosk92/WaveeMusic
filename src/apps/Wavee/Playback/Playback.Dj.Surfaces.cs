// ── Playback/Playback.Dj.Surfaces.cs ─────────────────────────────────────────────────────────────────────────────────
// AI DJ (plan-dj D5, §2.8): what the surfaces read of the DJ that the playback signals do not already say: the words the DJ is
// saying now (here or on the device that owns playback), whether it talks at all, and another device's DJ as the bar draws it.
//
// Role: SHELL (UI thread: the signals, the staged row, the remote facts)
// Spec: the AI DJ plan (plan-dj) §2.7 ("read a remote's DJ state from its cluster"), §2.8 (the caption, the bar); the protocol
// (dj-protocol) §2.3 (the row keys), §4 (f33 `jump`, the narration item)
//
// A NAMED PARTIAL OF `Playback.Dj`:
//
//   the caption   local: Dj.Caption (the narration on the deck)  ┐
//                 remote: Dj.RemoteNarration.Text (the owner's)   ┴▶ CaptionNow(): ONE read for every caption lane (the stage's
//                                                                    caption and lyrics face, the now-playing hero, the mini player)
//   the voice     Playback.ClipCurrent (here: the clip is the current item, also while its talk-over's track already plays under it)
//   is current    or RemoteNarration.Active (the owner's narration item is current) ─▶ VoiceCurrent(): the caption shows, no times,
//                 no seek fill, no heart. The narrower Playback.Narrating (the clip talks and its track has not started under it) is
//                 the move lock: the device picker, a transfer, the Connect restrictions (Playback.Dj.TalkOver.cs).
//   the owner's   FoldCluster ─▶ NoteRemoteFace (before the fold): the owner's narration item, the display keys it carries
//   narration     EnsureRow(spotify:media:<id>) ─▶ RemoteNarrationRow: a `spotify:media:` item has no catalog entity (no table answers
//   row           its kind), so its row is STAGED here as the local narration rows are (Dj.StageRow): a track row of provider Tts
//                 (`wavee:tts:remote:<id>`), at Authority.Full with its title, the DJ's name and its picture, which nothing fetches.
//                 The bar, the stage and the mini player then show "Up next · DJ X" instead of "Loading…", and every narration
//                 rule keyed on IsNarrationRow (no heart, an inert credit) holds for it too.
//   the owner's   FoldCluster ─▶ NoteRemoteDj (after the fold): the context's jump label and colours, the playing row's segment label
//   DJ            (a narration's: its target row's), whether "Let DJ pick" may be sent: the owner's signals offer `jump` (and its
//                 disallow_signals do not refuse it); an owner that states no signals at all is judged by its next rows (a later
//                 segment's jump target listed). FactsOf reads it for a foreign deck.

using System.Text;

using FluentGpu.Foundation;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using ClusterTrack = Wavee.Spotify.Decode.ClusterTrack;

namespace Wavee;

public static partial class Playback
{
    public static partial class Dj
    {
        // ── the caption, one read ────────────────────────────────────────────────────────────────────────────────

        static string s_remoteSsml = "", s_remoteText = "";

        /// <summary>The words the DJ says now, as plain text (the <c>&lt;entity&gt;</c> names kept): the narration on this deck
        /// (<see cref="Caption"/>), else the one on the deck of the device that owns playback (<see cref="RemoteNarration"/>); "" while
        /// the DJ is quiet. Reactive (a render that calls it re-renders when either changes); the remote text is converted once per item.</summary>
        public static string CaptionNow()
        {
            string local = Caption.Value;
            if (local.Length > 0) return local;
            NarrationFacts remote = RemoteNarration.Value;
            return remote.Active ? RemoteText(remote.Ssml ?? "") : "";
        }

        /// <summary><see cref="CaptionNow"/> without subscribing.</summary>
        public static string CaptionPeek()
        {
            string local = Caption.Peek();
            if (local.Length > 0) return local;
            NarrationFacts remote = RemoteNarration.Peek();
            return remote.Active ? RemoteText(remote.Ssml ?? "") : "";
        }

        static string RemoteText(string ssml)
        {
            if (!ReferenceEquals(ssml, s_remoteSsml)) { s_remoteSsml = ssml; s_remoteText = PlainText(ssml); }
            return s_remoteText;
        }

        /// <summary>The DJ's voice is the current item, here (<see cref="Playback.ClipCurrent"/>: also while its talk-over's track already
        /// plays under it) or on the device that owns playback (its narration item is current): the surfaces show no times and no progress
        /// (dj-protocol §4: "the seek bar hides time and duration while <c>is_narration</c> is true"). Not the move lock: that is
        /// <see cref="Playback.Narrating"/> (the voice talks and its track has not started). Reactive.</summary>
        public static bool VoiceCurrent() => ClipCurrent.Value || RemoteNarration.Value.Active;

        // ── the owner's narration item as a row ─────────────────────────────────────────────────────────────────

        static EntityId s_remoteMedia;
        static string s_remoteTitle = "", s_remoteArtist = "", s_remoteImage = "", s_remoteSlot = "";
        static EntityId s_remoteStagedFor;
        static EntityRef s_remoteRow;

        /// <summary>The prefix of a staged remote narration row's identity (a Tts track: no fetch answers it, nothing reports it).</summary>
        public const string RemoteRowPrefix = "wavee:tts:remote:";

        /// <summary>Before a cluster folds: note the owner's narration item (<c>is_narration</c>) and what it shows (its <c>title</c>,
        /// <c>artist_name</c>, its picture, its provider's slot), so the fold's row ask (<see cref="RemoteNarrationRow"/>) can stage it.
        /// UI thread (the drain).</summary>
        internal static void NoteRemoteFace(ClusterBuffer buffer, in Spotify.Decode.ClusterDelta d)
        {
            if (!d.HasTrack || !d.Track.Narration) { s_remoteMedia = default; return; }
            ReadOnlySpan<byte> uri = buffer.Utf8(d.Track.Uri);
            if (!uri.StartsWith("spotify:media:"u8)) { s_remoteMedia = default; return; }
            EntityId media = EntityId.Parse(uri);
            if (media.Equals(s_remoteMedia)) return;
            s_remoteMedia = media;
            ReadOnlySpan<byte> provider = buffer.Utf8(d.Track.Provider);
            s_remoteSlot = provider.StartsWith("narration/"u8) ? Encoding.UTF8.GetString(provider["narration/"u8.Length..]) : "";
            s_remoteTitle = Encoding.UTF8.GetString(buffer.Utf8(d.Track.Title));
            s_remoteArtist = Encoding.UTF8.GetString(buffer.Utf8(d.Track.ArtistName));
            s_remoteImage = Encoding.UTF8.GetString(buffer.Utf8(d.Track.Image));
        }

        /// <summary>The row of the owner's narration item <paramref name="media"/> (<c>spotify:media:&lt;id&gt;</c>): staged once per item
        /// (see the banner), else none (not the noted narration item, or no scope). UI thread.</summary>
        internal static EntityRef RemoteNarrationRow(EntityId media)
        {
            if (media.IsEmpty || !media.Equals(s_remoteMedia) || Entities.Current is not { } scope) return default;
            if (s_remoteStagedFor.Equals(media) && !s_remoteRow.IsNone && (uint)s_remoteRow.Slot < (uint)scope.Tracks.Count
                && scope.Tracks.Id[s_remoteRow.Slot].Provider == EntityProvider.Tts)
                return s_remoteRow;
            string text = media.Text;
            EntityId rowId = EntityId.Parse(RemoteRowPrefix + text[(text.LastIndexOf(':') + 1)..]);
            var staging = Staging.Rent();
            staging.Epoch = scope.Epoch;
            ref var row = ref staging.Tracks.RowFor(new StagedId(staging.AddText(Encoding.UTF8.GetBytes(rowId.Text))), Authority.Full,
                (uint)(TrackFields.Identity | TrackFields.Availability | TrackFields.Video));
            // The words the official player shows ("Welcome", "Up next"), the session's own when it named them (D2's StageRow rule).
            string title = s_remoteTitle.Length > 0 ? s_remoteTitle : s_remoteSlot == "welcome" ? "Welcome" : "Up next";
            row.Title = staging.AddText(Encoding.UTF8.GetBytes(title));
            if (s_remoteArtist.Length > 0) row.ArtistLine = staging.AddText(Encoding.UTF8.GetBytes(s_remoteArtist));
            if (s_remoteImage.Length > 0) row.Image = staging.AddText(Encoding.UTF8.GetBytes(s_remoteImage));
            row.DurationMs = 0;
            row.Flags = 0;
            Entities.Commit(staging);
            Staging.Return(staging);
            Entities.Publish();
            if (!scope.Tracks.TryGetSlot(rowId, out int slot)) return default;
            s_remoteStagedFor = media;
            s_remoteRow = new EntityRef(EntityKind.Track, slot);
            Log.Info("dj", "the owner's narration item " + text + " shows as a staged row (slot=" + (s_remoteSlot.Length > 0 ? s_remoteSlot : "?") + ")");
            return s_remoteRow;
        }

        // ── the owner's DJ, as the bar draws it ─────────────────────────────────────────────────────────────────

        /// <summary>Another device's DJ as a controller draws it (<see cref="FactsOf"/> for a foreign deck): the jump button's label and
        /// colours from the context's metadata, the product's name, the playing row's segment label, whether "Let DJ pick" may be sent
        /// now. Default while no foreign DJ plays.</summary>
        public readonly record struct RemoteDj(string Pick, string Name, string Segment, bool CanJump, uint PickFill, uint PickBorder, uint PickInk);

        static RemoteDj s_remoteDj;

        /// <summary>What <see cref="NoteRemoteDj"/> read last (the inspector's mirror readout).</summary>
        public static RemoteDj RemoteNow => s_remoteDj;

        /// <summary>After a cluster folded on a foreign deck (null: no foreign owner any more): its DJ facts (see the banner). One pass over
        /// the context metadata and the next rows per push, which is edge-paced; UI thread.</summary>
        internal static void NoteRemoteDj(ClusterBuffer? buffer, in Spotify.Decode.ClusterDelta d)
        {
            if (buffer is null || !d.HasTrack) { s_remoteDj = default; return; }
            string pick = "", name = "";
            uint fill = 0, border = 0, ink = 0;
            foreach (ref readonly Spotify.Decode.ClusterMeta m in buffer.Meta(d.MetaStart, d.MetaCount))
            {
                ReadOnlySpan<byte> key = buffer.Utf8(m.Key), value = buffer.Utf8(m.Value);
                if (key.SequenceEqual("dj.interactivity.localized_jump_button"u8)) pick = Encoding.UTF8.GetString(value);
                else if (key.SequenceEqual("context_description"u8)) name = Encoding.UTF8.GetString(value);
                else if (key.SequenceEqual("narration.jump.background.color"u8)) fill = DjCardColors.Argb(value);
                else if (key.SequenceEqual("narration.jump.border.color"u8)) border = DjCardColors.Argb(value);
                else if (key.SequenceEqual("narration.jump.icon.color"u8)) ink = DjCardColors.Argb(value);
            }
            // The playing row's segment: a track's own; a narration's is its target's (the next row whose id is the item's, §1.5).
            ReadOnlySpan<ClusterTrack> next = buffer.Tracks(d.NextStart, d.NextCount);
            ReadOnlySpan<byte> segment = buffer.Utf8(d.Track.Segment), subtitle = buffer.Utf8(d.Track.Subtitle);
            if (d.Track.Narration)
            {
                ReadOnlySpan<byte> uri = buffer.Utf8(d.Track.Uri), id = uri[(uri.LastIndexOf((byte)':') + 1)..];
                foreach (ref readonly ClusterTrack row in next)
                {
                    ReadOnlySpan<byte> rowUri = buffer.Utf8(row.Uri);
                    if (row.Narration || !rowUri[(rowUri.LastIndexOf((byte)':') + 1)..].SequenceEqual(id)) continue;
                    segment = buffer.Utf8(row.Segment);
                    subtitle = buffer.Utf8(row.Subtitle);
                    break;
                }
            }
            bool canJump;
            if (d.SignalsStated) canJump = d.JumpOffered && !d.JumpDisallowed;
            else
            {
                canJump = false;
                foreach (ref readonly ClusterTrack row in next)
                    if (row.JumpTarget && !row.Segment.IsEmpty && !buffer.Utf8(row.Segment).SequenceEqual(segment)) { canJump = true; break; }
            }
            var facts = new RemoteDj(pick, name, Encoding.UTF8.GetString(subtitle), canJump, fill, border, ink);
            if (facts != s_remoteDj)
                Log.Info("dj", "the owner's DJ: segment=" + (facts.Segment.Length > 0 ? facts.Segment : "?") + " canJump=" + canJump
                    + " (" + (d.SignalsStated ? (d.JumpOffered ? "its signals offer jump" : "its signals do not offer jump") + (d.JumpDisallowed ? ", disallowed" : "")
                        : "no signals stated: a later segment's target " + (canJump ? "is" : "is not") + " listed") + ")");
            s_remoteDj = facts;
        }
    }
}
