// ── Spotify/Spotify.Api.Artist.cs ───────────────────────────────────────────────────────────────────────────────────
// the artist page's provider answers: the whole overview, one discography facet page, and the thin NPV artist
//
// Role: SHELL (each answer BLOCKS on an api thread) + CORE (the facet request body, pure)
// Owner: N (stream N-B)
// Wave: 5
// Budget: ~900 lines (a new api file, WP-5.N contract §0)
// Spec: WP-5.N contract §3 (the two signatures the coordinator's `Api.AnswerQuery` arms call); ch 08 §7; G-045
//
// THE COORDINATOR'S ROUTING PATCH CALLS THESE, and nothing else in this file is on a routing path:
//
//     PathfinderOp.ArtistOverview                              → ArtistPageAnswer(uri, s)
//     PathfinderOp.Discography{Albums,Singles,Compilations}    → DiscographyFacetAnswer(uri, facet, offset, s)
//
// Each performs its request through the existing runner (`Pathfinder`, `Vars`, `ArtistOverviewQuery`), decodes into the
// batch's staging on the same api thread, and returns the request's `Result` — the provider folds its status into the
// batch verdict (`FetchOutcome.Note`). A non-2xx answer decodes nothing: the edge door then records the vacancy or the
// failure, never an empty list that looks like an answer.
//
// THE FACET OPERATIONS. One persisted discography document hosts the facet operation names beside `…All`
// (`Queries.Discography`'s own note), so the NAME selects the facet and the hash is shared; the variables are the
// captured `{uri, offset, limit, order: "DATE_DESC"}` (`DiscographyBody`), on the desktop identity. The three names are
// not in B1b's capture-backed table — they ride the `…All` hash on that note's authority (reported as a compile-free
// runtime risk: a 400 here is logged by name by `Pathfinder`).

using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>The three facet operations over the shared discography document.</summary>
        public static class ArtistQueries
        {
            public static readonly Query DiscographyAlbums =
                new("queryArtistDiscographyAlbums", Queries.Discography.Hash, Queries.Discography.Web);
            public static readonly Query DiscographySingles =
                new("queryArtistDiscographySingles", Queries.Discography.Hash, Queries.Discography.Web);
            public static readonly Query DiscographyCompilations =
                new("queryArtistDiscographyCompilations", Queries.Discography.Hash, Queries.Discography.Web);

            /// <summary>The operation a facet pages through.</summary>
            public static Query For(DiscoFacet facet) => facet switch
            {
                DiscoFacet.Singles => DiscographySingles,
                DiscoFacet.Compilations => DiscographyCompilations,
                _ => DiscographyAlbums,
            };
        }

        /// <summary>One facet page's body: the captured variables <c>{uri, offset, limit, order: "DATE_DESC"}</c> under the
        /// facet's own operation name. PURE.</summary>
        public static byte[] DiscographyFacetBody(string artistUri, DiscoFacet facet, int offset, int limit)
        {
            var query = ArtistQueries.For(facet);
            var vars = new Vars(query);
            vars.W.WriteString("uri", artistUri);
            vars.W.WriteNumber("offset", Math.Max(0, offset));
            vars.W.WriteNumber("limit", Math.Max(1, limit));
            vars.W.WriteString("order", "DATE_DESC");
            return vars.Finish();
        }

        /// <summary>One facet page. BLOCKS.</summary>
        public static Result DiscographyFacetQuery(string artistUri, DiscoFacet facet, int offset, int limit, CancellationToken ct)
            => Pathfinder(ArtistQueries.For(facet), DiscographyFacetBody(artistUri, facet, offset, limit), ct);

        /// <summary>THE ARTIST PAGE (contract §3): <c>queryArtistOverview</c> for <paramref name="artistUri"/>, decoded WHOLE
        /// by <see cref="Decode.ArtistPage"/> into <paramref name="s"/> — identity, stats, bio, pick, upcoming, latest, the
        /// chart seed, the facets' first pages with their totals, and every shelf. Replaces the <c>Decode.Export</c> call the
        /// provider made, which lumped every facet into <c>ArtistAlbums</c> and staged none of the extras. BLOCKS.</summary>
        public static Result ArtistPageAnswer(string artistUri, Staging s)
        {
            Result result = ArtistOverviewQuery(artistUri, CancellationToken.None);
            if (result.Ok && result.Body.Length > 0) Decode.ArtistPage(result.Bytes, Encoding.UTF8.GetBytes(artistUri), s);
            return result;
        }

        /// <summary>ONE DISCOGRAPHY FACET PAGE (contract §3): the page at <paramref name="offset"/> of
        /// <paramref name="facet"/> (<see cref="DiscographyPageSize"/> releases), landed by
        /// <see cref="Decode.DiscographyFacet"/> as a <c>ReplacePage</c> with the server total. BLOCKS.</summary>
        public static Result DiscographyFacetAnswer(string artistUri, DiscoFacet facet, int offset, Staging s)
        {
            Result result = DiscographyFacetQuery(artistUri, facet, offset, DiscographyPageSize, CancellationToken.None);
            if (result.Ok && result.Body.Length > 0)
                Decode.DiscographyFacet(result.Bytes, Encoding.UTF8.GetBytes(artistUri), facet, offset, s);
            return result;
        }

        /// <summary>The now-playing "About the artist" panel's artist half (G-045): <c>queryNpvArtist</c> decoded by
        /// <see cref="Decode.NpvArtist"/>. Not routed — nothing in the planner names it yet (reported). BLOCKS.</summary>
        public static Result NpvArtistAnswer(string artistUri, string trackUri, Staging s)
        {
            Result result = NpvArtist(artistUri, trackUri, CancellationToken.None);
            if (result.Ok && result.Body.Length > 0) Decode.NpvArtist(result.Bytes, Encoding.UTF8.GetBytes(artistUri), s);
            return result;
        }
    }
}
