// ── Spotify/Spotify.Decode.Profile.cs — user-profile-view/v3 → the user row and the four profile relations ─────────
// CORE: pure over a span (ProtoReader), no interner, no live table, zero allocation after warm-up. The identity is the uri
// the caller ASKED with, never field 1 (the kind-15 rule). Field numbers: docs/plans/wavee/profile-pages-api-research.md §2.
namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>A list past this many entries lands its head Partial with the true total (UI-thread commit budget; the
        /// list is unpaged and ServesEdge refuses offset &gt; 0, so nothing re-asks it).</summary>
        public const int ProfileListCap = 5000;

        /// <summary>The profile view → the row (Identity | Social | Follow at Full) + the public playlists (Partial when the
        /// total says there are more) + the recently played artists (EMPTY INCLUDED: absent field 7 is the owner's switch).</summary>
        public static void ProfileView(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> userUri, Staging s)
        {
            StagedId user = Identity(s, userUri);
            if (user.IsEmpty) return;
            ReadOnlySpan<byte> name = default, image = default;
            int followers = 0, following = 0, totalPlaylists = 0;
            uint color = 0, flags = 0;

            // Pass 1: header scalars + field 8. Pass 2 owns field 7, so each run is contiguous whatever order the
            // serializer interleaved the two repeated fields in.
            var playlists = s.Run(Relation.ProfilePlaylists);
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 2 when r.Wire == 2: name = r.Bytes(); break;
                    case 3 when r.Wire == 2: image = r.Bytes(); break;
                    case 4 when r.Wire == 0: followers = r.Int32(); break;
                    case 5 when r.Wire == 0: following = r.Int32(); break;
                    case 6 when r.Wire == 0: if (r.Bool()) flags |= (uint)UserFlags.Followed; break;
                    case 8 when r.Wire == 2: PublicPlaylistCard(r.Message(), s, ref playlists); break;
                    case 9 when r.Wire == 0: totalPlaylists = r.Int32(); break;
                    case 10 when r.Wire == 0: if (r.Bool()) flags |= (uint)UserFlags.CurrentUser; break;
                    case 16 when r.Wire == 0: color = RgbColor(r.Varint()); break;
                    case 23 when r.Wire == 0: if (r.Bool()) flags |= (uint)UserFlags.AllowFollows; break;
                    case 24 when r.Wire == 0: if (r.Bool()) flags |= (uint)UserFlags.ShowFollows; break;
                    default: r.Skip(); break;
                }
            }
            int cards = playlists.Count;
            int total = Math.Max(totalPlaylists, cards);
            if (cards == 0) playlists.EndEvenIfEmpty(user, total);
            else playlists.End(user, cards >= total ? EdgeState.Complete : EdgeState.Partial, total);

            var artists = s.Run(Relation.ProfileArtists);
            r = new ProtoReader(proto);
            while (r.Next())
            {
                if (r.Field == 7 && r.Wire == 2) RecentArtistCard(r.Message(), s, ref artists);
                else r.Skip();
            }
            artists.EndEvenIfEmpty(user);

            ref var row = ref s.Users.RowFor(user, Authority.Full,
                (uint)(UserFields.Identity | UserFields.Social | UserFields.Follow));
            row.Name = s.AddText(name);
            row.Image = StageImage(s, image);
            row.Color = color;
            row.Followers = followers;
            row.Following = following;
            row.PublicPlaylists = total;
            row.Flags = flags;
            s.Users.Settle();
        }

        /// <summary>The profile view's 404 → a KNOWN negative: Social | Follow with <see cref="UserFlags.Unavailable"/>
        /// and both riding shelves Complete-empty — never an omission the miss policy retries, never a failure.</summary>
        public static void ProfileUnavailable(ReadOnlySpan<byte> userUri, Staging s)
        {
            StagedId user = Identity(s, userUri);
            if (user.IsEmpty) return;
            ref var row = ref s.Users.RowFor(user, Authority.Full, (uint)(UserFields.Social | UserFields.Follow));
            row.Flags = (uint)UserFlags.Unavailable;
            s.Users.Settle();
            var playlists = s.Run(Relation.ProfilePlaylists);
            playlists.EndEvenIfEmpty(user);
            var artists = s.Run(Relation.ProfileArtists);
            artists.EndEvenIfEmpty(user);
        }

        /// <summary>…/followers or …/following → ONE whole-list run in wire order (Following: artists, then users). An
        /// empty body is an empty, Complete list. Past <paramref name="cap"/> entries the head lands Partial with the true total.</summary>
        public static void ProfileList(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> userUri, Relation relation, Staging s,
                                       int cap = ProfileListCap)
        {
            if (relation is not (Relation.ProfileFollowers or Relation.ProfileFollowing)) return;
            StagedId user = Identity(s, userUri);
            if (user.IsEmpty) return;
            var run = s.Run(relation);
            int seen = 0;
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                if (seen++ >= cap) { r.Skip(); continue; }
                ProfileEntry(r.Message(), s, ref run, relation);
            }
            if (seen > cap && run.Count > 0) run.End(user, EdgeState.Partial, seen);
            else run.EndEvenIfEmpty(user);
        }

        static void PublicPlaylistCard(ProtoReader card, Staging s, ref EdgeRun run)
        {
            ReadOnlySpan<byte> uri = default, name = default, image = default, ownerUri = default;
            int followers = 0;
            byte flags = 0;
            while (card.Next())
            {
                switch (card.Field)
                {
                    case 1 when card.Wire == 2: uri = card.Bytes(); break;
                    case 2 when card.Wire == 2: name = card.Bytes(); break;
                    case 3 when card.Wire == 2: image = card.Bytes(); break;
                    case 4 when card.Wire == 0: followers = card.Int32(); break;
                    case 6 when card.Wire == 2: ownerUri = card.Bytes(); break;
                    case 7 when card.Wire == 0: if (card.Bool()) flags |= (byte)ProfileCardFlags.ViewerFollows; break;
                    default: card.Skip(); break;             // owner_name (5) is NOT staged: it would seal the owner's
                }                                            // Identity with no avatar (the Pathfinder User arm's rule)
            }
            StagedId id = Identity(s, uri);
            if (id.IsEmpty || id.Kind(s) != EntityKind.Playlist) return;
            ref var row = ref s.Playlists.RowFor(id, Authority.Thin, name.IsEmpty ? 0u : (uint)PlaylistFields.Identity);
            row.Title = s.AddText(name);
            row.Image = StageImage(s, image);                // a mosaic token stays verbatim (CoverToken)
            row.OwnerUri = ownerUri.IsEmpty ? default : UserUri(s, ownerUri);
            s.Playlists.Settle();
            ref var edge = ref run.Add(id);
            edge.At = followers;
            edge.B0 = flags;
        }

        static void RecentArtistCard(ProtoReader card, Staging s, ref EdgeRun run)
        {
            ReadOnlySpan<byte> uri = default, name = default, image = default;
            int followers = 0;
            byte flags = 0;
            while (card.Next())
            {
                switch (card.Field)
                {
                    case 1 when card.Wire == 2: uri = card.Bytes(); break;
                    case 2 when card.Wire == 2: name = card.Bytes(); break;
                    case 3 when card.Wire == 2: image = card.Bytes(); break;
                    case 4 when card.Wire == 0: followers = card.Int32(); break;
                    case 5 when card.Wire == 0: if (card.Bool()) flags |= (byte)ProfileCardFlags.OwnerFollows; break;
                    default: card.Skip(); break;
                }
            }
            StagedId id = Identity(s, uri);
            if (id.IsEmpty || id.Kind(s) != EntityKind.Artist) return;
            StageThinArtist(s, in id, name, image);
            ref var edge = ref run.Add(id);
            edge.At = followers;
            edge.B0 = flags;
        }

        static void ProfileEntry(ProtoReader e, Staging s, ref EdgeRun run, Relation relation)
        {
            ReadOnlySpan<byte> uri = default, name = default, image = default;
            int followers = 0;
            uint color = 0;
            byte flags = 0;
            while (e.Next())
            {
                switch (e.Field)
                {
                    case 1 when e.Wire == 2: uri = e.Bytes(); break;
                    case 2 when e.Wire == 2: name = e.Bytes(); break;
                    case 3 when e.Wire == 2: image = e.Bytes(); break;
                    case 4 when e.Wire == 0: followers = e.Int32(); break;
                    case 6 when e.Wire == 0:
                    case 7 when e.Wire == 0: if (e.Bool()) flags |= (byte)ProfileCardFlags.ViewerFollows; break;
                    case 11 when e.Wire == 0: color = RgbColor(e.Varint()); break;
                    default: e.Skip(); break;
                }
            }
            StagedId id = Identity(s, uri);
            if (id.IsEmpty) return;
            EntityKind kind = id.Kind(s);
            if (!ProfileCardEdge.Admits(relation, kind)) return;
            if (kind == EntityKind.Artist) StageThinArtist(s, in id, name, image);
            else StageThinUser(s, in id, name, image, color);
            ref var edge = ref run.Add(id);
            edge.At = followers;
            edge.B0 = flags;
        }

        /// <summary>Name (+ portrait when stated) at Thin. Nameless → no row: ArtistFields' Identity commit writes Name
        /// unconditionally, so an Image-only stage would blank a known name.</summary>
        static void StageThinArtist(Staging s, in StagedId id, ReadOnlySpan<byte> name, ReadOnlySpan<byte> image)
        {
            if (name.IsEmpty) return;
            ref var a = ref s.Artists.RowFor(id, Authority.Thin,
                (uint)ArtistFields.Name | (image.IsEmpty ? 0u : (uint)ArtistFields.Image));
            a.Name = s.AddText(name);
            a.Image = StageImage(s, image);
            s.Artists.Settle();
        }

        /// <summary>A nameless mention must not seal Identity (the Pathfinder User arm's rule).</summary>
        static void StageThinUser(Staging s, in StagedId id, ReadOnlySpan<byte> name, ReadOnlySpan<byte> image, uint color)
        {
            if (name.IsEmpty) return;
            ref var u = ref s.Users.RowFor(id, Authority.Thin, (uint)UserFields.Identity);
            u.Name = s.AddText(name);
            u.Image = StageImage(s, image);
            u.Color = color;
            s.Users.Settle();
        }

        /// <summary>A wire cover → what an image column stores: a spotify:image: token becomes its CDN url; https, pickasso
        /// and spotify:mosaic: stay verbatim (CoverToken).</summary>
        static TextRef StageImage(Staging s, ReadOnlySpan<byte> wire)
        {
            if (wire.IsEmpty) return default;
            if (!CoverToken.IsImageToken(wire)) return s.AddText(wire);
            Span<byte> buf = stackalloc byte[CoverToken.MaxImageUrl];
            int n = CoverToken.ImageTokenUrl(wire, buf);
            return n > 0 ? s.AddText(buf[..n]) : default;
        }

        /// <summary>A wire colour (int32 0xRRGGBB, any sign) → the app's 0xFFRRGGBB; 0 stays 0 ("not stated").</summary>
        public static uint RgbColor(ulong wire)
        {
            uint rgb = (uint)(wire & 0xFFFFFFu);
            return rgb == 0 ? 0u : 0xFF000000u | rgb;
        }
    }
}
