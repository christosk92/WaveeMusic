// ── Spotify/Spotify.Api.Profile.cs — the profile view and its two lists (profile pages plan, D2) ─────────────────────
// SHELL (blocks; api threads only, C9). The wire is docs/plans/wavee/profile-pages-api-research.md §2: three parallel
// GETs on spclient.WG, protobuf. The decoders are Spotify.Decode.Profile.cs; the follow ops are Spotify.Api.Profile.Follow.cs.
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        const string ProfilePath = "/user-profile-view/v3/profile/";

        /// <summary>The profile view — the captured spelling: spclient.WG (never the resolved spclient), protobuf, ten cards
        /// per shelf. PURE.</summary>
        public static Route ProfileViewRoute(string username)
            => new(Verb.Get, ApiHost.SpclientWg,
                   ProfilePath + Escaped(username) + "?playlist_limit=10&artist_limit=10&episode_limit=10&market=from_token",
                   CommonProtobuf, RequestKind.Profile);

        /// <summary>The whole, unpaged followers list. PURE.</summary>
        public static Route ProfileFollowersRoute(string username)
            => new(Verb.Get, ApiHost.SpclientWg, ProfilePath + Escaped(username) + "/followers?market=from_token",
                   CommonProtobuf, RequestKind.Profile);

        /// <summary>The whole, unpaged following list (artists, then users). PURE.</summary>
        public static Route ProfileFollowingRoute(string username)
            => new(Verb.Get, ApiHost.SpclientWg, ProfilePath + Escaped(username) + "/following?market=from_token",
                   CommonProtobuf, RequestKind.Profile);

        public static Result ProfileView(string username, CancellationToken ct) => Send(ProfileViewRoute(username), [], ct);
        public static Result ProfileFollowers(string username, CancellationToken ct) => Send(ProfileFollowersRoute(username), [], ct);
        public static Result ProfileFollowing(string username, CancellationToken ct) => Send(ProfileFollowingRoute(username), [], ct);

        /// <summary>SpclientRoute.ProfileView for one user (a row batch's Social, or the kind-15 fallback).</summary>
        static void ProfileViewAnswer(string uri, Staging s, ref FetchOutcome outcome, uint groups)
        {
            Result result = ProfileView(UsernameOf(uri), CancellationToken.None);
            outcome.Note(in result, groups);
            StageProfileView(in result, uri, s);
        }

        /// <summary>200 → the view (an empty body is a valid, empty view); 404 → the known negative. Anything else stages
        /// nothing: the outcome says why.</summary>
        static void StageProfileView(in Result result, string uri, Staging s)
        {
            if (result.Ok) Decode.ProfileView(result.Bytes, Encoding.UTF8.GetBytes(uri), s);
            else if (result.Status == 404) Decode.ProfileUnavailable(Encoding.UTF8.GetBytes(uri), s);
        }

        /// <summary>The followers / following edge for one parent. 404 is "no list" — an empty, Complete answer.</summary>
        static void ProfileListAnswer(string uri, Relation relation, Staging s, ref FetchOutcome outcome, uint groups)
        {
            string username = UsernameOf(uri);
            Result result = relation == Relation.ProfileFollowers
                ? ProfileFollowers(username, CancellationToken.None)
                : ProfileFollowing(username, CancellationToken.None);
            outcome.Note(in result, groups);
            if (result.Ok || result.Status == 404)
                Decode.ProfileList(result.Ok ? result.Bytes : default, Encoding.UTF8.GetBytes(uri), relation, s);
        }
    }
}
