// ── Spotify/Spotify.Decode.Friends.cs — the friend-activity answers → staged rows and one friends batch (#277) ─────────
//
//   seed  : presence-view/v2/init-friend-feed/<connection id>  → { "friends": [ { timestamp, user{}, track{} } ] }
//   delta : presence-view/v1/user/<id>                          → ONE friend object, same shape
//
// A friend object: { "timestamp": 1732270000000 | "1732270000000",
//                    "user":  { "uri", "name", "imageUrl" | "image_url" },
//                    "track": { "uri", "name", "imageUrl", "album": {uri,name}, "artist": {uri,name}, "context": {uri,name,index} } }
// An entry without user.uri or track.uri stages nothing (0.2.9's rule). Every row it names is staged THIN with only the
// words it carries, so a fuller row the scope already holds keeps its own (Authority.Thin). Socket thread: no interner,
// no live table (C1); `Entities.CommitFriends` resolves and lands the run.

using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary><c>init-friend-feed</c>'s answer → a REPLACE batch. False when the body is not a JSON object (nothing
        /// staged; the host keeps what it held).</summary>
        public static bool FriendFeed(ReadOnlySpan<byte> json, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            s.FriendsBatch(FriendsOp.Replace);
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("friends"u8)) { SkipValue(ref r); continue; }
                if (!EnterArray(ref r)) continue;
                for (int list = r.CurrentDepth; Element(ref r, list);)
                    Friend(ref r, s);
            }
            return true;
        }

        /// <summary><c>presence-view/v1/user/&lt;id&gt;</c>'s answer for <paramref name="userUri"/> → an UPSERT of that
        /// friend's row, or a REMOVE when the body names no usable friend.</summary>
        public static void FriendOne(ReadOnlySpan<byte> json, ReadOnlySpan<byte> userUri, Staging s)
        {
            var user = new StagedId(s.AddText(userUri));
            int before = s.Friends.Count;
            var r = new Utf8JsonReader(json);
            if (r.Read() && r.TokenType == JsonTokenType.StartObject) Friend(ref r, s);
            s.FriendsBatch(s.Friends.Count > before ? FriendsOp.Upsert : FriendsOp.Remove, in user);
        }

        /// <summary>One friend object; the reader sits ON its StartObject and leaves on its EndObject.</summary>
        static void Friend(ref Utf8JsonReader r, Staging s)
        {
            TextRef userUri = default, userName = default, userImage = default;
            TextRef trackUri = default, trackName = default, trackImage = default;
            TextRef albumUri = default, albumName = default, artistUri = default, artistName = default;
            TextRef contextUri = default, contextName = default;
            long ts = 0;
            for (int o = r.CurrentDepth; Next(ref r, o);)
            {
                if (r.ValueTextEquals("timestamp"u8)) { r.Read(); ts = Num(ref r); }
                else if (r.ValueTextEquals("user"u8)) FriendNamed(ref r, s, out userUri, out userName, out userImage);
                else if (r.ValueTextEquals("track"u8))
                {
                    for (int t = Fields(ref r); Next(ref r, t);)
                    {
                        if (r.ValueTextEquals("uri"u8)) { r.Read(); trackUri = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("name"u8)) { r.Read(); trackName = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("imageUrl"u8) || r.ValueTextEquals("image_url"u8)) { r.Read(); trackImage = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("album"u8)) FriendNamed(ref r, s, out albumUri, out albumName, out _);
                        else if (r.ValueTextEquals("artist"u8)) FriendNamed(ref r, s, out artistUri, out artistName, out _);
                        else if (r.ValueTextEquals("context"u8)) FriendNamed(ref r, s, out contextUri, out contextName, out _);
                        else SkipValue(ref r);
                    }
                }
                else SkipValue(ref r);
            }
            if (userUri.IsEmpty || trackUri.IsEmpty) return;

            var user = new StagedId(userUri);
            ref var u = ref s.Users.RowFor(user, Authority.Thin, userName.IsEmpty && userImage.IsEmpty ? 0u : (uint)UserFields.Identity);
            u.Name = userName;
            u.Image = userImage;

            var album = albumUri.IsEmpty ? default : new StagedId(albumUri);
            if (!album.IsEmpty && !albumName.IsEmpty)
                s.Albums.RowFor(album, Authority.Thin, (uint)AlbumFields.Title).Title = albumName;
            var artist = artistUri.IsEmpty ? default : new StagedId(artistUri);
            if (!artist.IsEmpty && !artistName.IsEmpty)
                s.Artists.RowFor(artist, Authority.Thin, (uint)ArtistFields.Name).Name = artistName;
            var context = contextUri.IsEmpty ? default : new StagedId(contextUri);
            if (!context.IsEmpty && !contextName.IsEmpty && context.Kind(s) == EntityKind.Playlist)
                s.Playlists.RowFor(context, Authority.Thin, (uint)PlaylistFields.Identity).Title = contextName;

            var track = new StagedId(trackUri);
            uint trackKnown = (trackName.IsEmpty ? 0u : (uint)TrackFields.Title) | (trackImage.IsEmpty ? 0u : (uint)TrackFields.Image);
            ref var t2 = ref s.Tracks.RowFor(track, Authority.Thin, trackKnown);
            t2.Title = trackName;
            t2.Image = trackImage;
            t2.AlbumUri = album;

            ref var f = ref s.Friends.Add();
            f.User = user;
            f.Track = track;
            f.Album = album;
            f.Artist = artist;
            f.Context = context;
            f.TimestampMs = ts;
        }

        /// <summary><c>{ uri, name, imageUrl | image_url }</c>; the reader sits on the property name.</summary>
        static void FriendNamed(ref Utf8JsonReader r, Staging s, out TextRef uri, out TextRef name, out TextRef image)
        {
            uri = name = image = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); uri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                else if (r.ValueTextEquals("imageUrl"u8) || r.ValueTextEquals("image_url"u8)) { r.Read(); image = s.AddJson(ref r); }
                else SkipValue(ref r);
            }
        }
    }
}
