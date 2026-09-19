// ── Spotify/Spotify.Decode.Artist.cs ────────────────────────────────────────────────────────────────────────────────
// the artist page's pathfinder folds: queryArtistOverview (the WHOLE answer), one page of a discography facet, and the
// thin queryNpvArtist
//
// Role: CORE (pure over spans, into a Staging; no interner, no live table — C1/C10)
// Owner: N (stream N-B)
// Wave: 5
// Budget: ~900 lines (a new decoder file, WP-5.N contract §0)
// Spec: ch 08 §7 + DATA GAPS 2-18; G-045 (queryNpvArtist); WP-5.N contract §3, §6; field paths ported from 0.2.9
//       SpotifyExportMapper.MapArtist / MapRelease / MapPinned / MapPreRelease / MapMusicVideos / MapMerch /
//       MapPlaylistRefs / MapLinks / MapGallery / MapTopCities / MapConcerts / ArtistFromNpv
//
// ONE FORWARD PASS PER OPERATION, the Spotify.Decode.Pathfinder.cs idiom (`Fields` + `Next`). What is different here:
//
//  · THE ARTIST IS THE CALLER'S URI, known before the walk — `artistUnion.uri` is the LAST property of a real answer, so
//    every list can be closed the moment its own walk ends, and no two lists ever sit on the pending stack at once.
//  · EVERY GROUP THE ANSWER SPEAKS FOR IS MARKED KNOWN, absent or not: no pick, no upcoming release, no latest release
//    and "the tour is whatever the concerts say" are ANSWERS, so `Knows(ArtistFields.Overview)` becomes true on live
//    data. The six payload relations, related and appears-on land Complete-and-empty when the answer carried none.
//  · THE DISCOGRAPHY IS WALKED HERE, not through `Collect`: its release groups wrap `releases.items[]` (Collect's element
//    reader popped them — reported, with the one-line patch), and a release's date is `{year, month, day, precision}`,
//    not the `isoString` the node reader knows — so the card group (`AlbumFields.Card`) is built by `ArtistRelease`.
//  · THE CHART is staged as the SEED list (`Staging.EndPopular`), never as an edge run: the commit merges it with the
//    extended list (Artist.Rules.cs), whichever lands first.

using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── the three entry points ───────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>queryArtistOverview</c> → the whole artist page for <paramref name="artistUri"/>: identity, the
        /// header (+ the provider's extracted accent), stats, the bio and its lead sentence, the pick, the upcoming
        /// release, the latest release, the chart SEED, the three facets' first pages with their server totals,
        /// appears-on, related, gallery, playlists (the profile's + featuring + discovered on), music videos, merch, top
        /// cities, external links, and the upcoming concerts when the answer carries the whole list.</summary>
        public static void ArtistPage(ReadOnlySpan<byte> json, ReadOnlySpan<byte> artistUri, Staging s)
        {
            s.ClearCredit();
            var artist = Identity(s, artistUri);
            if (artist.IsEmpty) return;
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "artistUnion"u8)) return;
            ArtistUnion(ref r, s, in artist, overview: true);
        }

        /// <summary><c>queryArtistDiscography{Albums,Singles,Compilations}</c> → one PAGE of the facet at
        /// <paramref name="offset"/> with the server's total. A group is ONE row, its first release (0.2.9 AddReleases).
        /// Only the relation and its album rows land — the artist row is untouched (this answer carries no profile).</summary>
        public static void DiscographyFacet(ReadOnlySpan<byte> json, ReadOnlySpan<byte> artistUri, DiscoFacet facet, int offset,
                                            Staging s)
        {
            s.ClearCredit();
            var artist = Identity(s, artistUri);
            if (artist.IsEmpty) return;
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "artistUnion"u8)) return;
            var name = ArtistFacetName(facet);
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("discography"u8)) { SkipValue(ref r); continue; }
                for (int g = Fields(ref r); Next(ref r, g);)
                {
                    if (r.ValueTextEquals(name)) ArtistFacetList(ref r, s, in artist, facet, Math.Max(0, offset));
                    else SkipValue(ref r);
                }
            }
        }

        /// <summary><c>queryNpvArtist</c> (G-045) → the thinner "About the artist" answer: identity, header, stats, bio
        /// and verified, plus the merch, top cities, links and gallery it carries. It does NOT speak for the pick, the
        /// upcoming or latest release, the tour or the chart, so those groups stay as they were (0.2.9 ArtistFromNpv:
        /// "reads only fields that NPV owns"). Not routed by the provider yet (reported).</summary>
        public static void NpvArtist(ReadOnlySpan<byte> json, ReadOnlySpan<byte> artistUri, Staging s)
        {
            s.ClearCredit();
            var artist = Identity(s, artistUri);
            if (artist.IsEmpty) return;
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "artistUnion"u8)) return;
            ArtistUnion(ref r, s, in artist, overview: false);
        }

        // ── the artistUnion fold ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Which of the lists the answer spoke for — the Complete-and-empty rule at the end of the walk.</summary>
        [Flags]
        enum ArtistSpoke : uint
        {
            None = 0,
            Gallery = 1 << 0, Playlists = 1 << 1, Videos = 1 << 2, Merch = 1 << 3, Cities = 1 << 4, Links = 1 << 5,
            Related = 1 << 6, AppearsOn = 1 << 7, Popular = 1 << 8,
            Albums = 1 << 9, Singles = 1 << 10, Compilations = 1 << 11,
        }

        /// <summary>The fold's scratch: the artist row as a LOCAL (nested stages grow <c>s.Artists</c>, so a ref into it
        /// would dangle) and the two header candidates the mapper prefers in order (visuals.headerImage, then the legacy
        /// headerImage.data).</summary>
        struct ArtistUnionState
        {
            public StagedArtist Row;
            public TextRef VisualsHeader, LegacyHeader, HeaderHex, AvatarHex;
            public ArtistSpoke Spoke;
            public bool Verified, SawStats, SawBio;
        }

        static void ArtistUnion(ref Utf8JsonReader r, Staging s, in StagedId artist, bool overview)
        {
            var st = default(ArtistUnionState);
            st.Row.Id = artist;
            st.Row.Authority = Authority.Full;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("profile"u8)) ArtistProfile(ref r, s, in artist, ref st, overview);
                else if (r.ValueTextEquals("visuals"u8)) ArtistVisuals(ref r, s, in artist, ref st);
                else if (r.ValueTextEquals("headerImage"u8)) st.LegacyHeader = ImageNode(ref r, s, ref st.HeaderHex);
                else if (r.ValueTextEquals("stats"u8)) { st.SawStats = true; ArtistStats(ref r, s, in artist, ref st); }
                else if (r.ValueTextEquals("onPlatformReputationTrait"u8)) st.Verified |= ArtistVerified(ref r);
                else if (overview && r.ValueTextEquals("discography"u8)) ArtistDiscography(ref r, s, in artist, ref st);
                else if (overview && r.ValueTextEquals("relatedContent"u8)) ArtistRelatedContent(ref r, s, in artist, ref st);
                else if (r.ValueTextEquals("goods"u8)) ArtistGoods(ref r, s, in artist, ref st, overview);
                else if (overview && (r.ValueTextEquals("relatedMusicVideos"u8) || r.ValueTextEquals("unmappedMusicVideosV2"u8)))
                {
                    ArtistVideos(ref r, s, in artist);
                    st.Spoke |= ArtistSpoke.Videos;
                }
                else if (overview && r.ValueTextEquals("preReleaseV2"u8)) ArtistPreRelease(ref r, s, ref st.Row);
                else SkipValue(ref r);
            }

            ref var row = ref st.Row;
            // `Name` is a bare presence check, same as every other producer in the app. `Image` (the avatar) is NOT:
            // unlike Header/Stats/Bio below, it used to be claimed unconditionally off `Name` alone — even on an NPV
            // (`overview: false`) thin answer, which never reads `visuals` at all and so never carries an avatar in
            // the first place. That is the chip-rail class of bug (2026-09-15, `Entities/Artist.cs`'s `CommitArtistRows`
            // fix, `ArtistV4`'s identical fix in Spotify.Decode.cs): a name-only mention sealed the WHOLE `Identity`
            // group, and `Fetch.NeedOf` never asked for the portrait again. `Image` now follows exactly the rule
            // `Header` already follows one line down: the WHOLE overview speaks for it, absent or not (file header:
            // "no pick … is an ANSWER"), but a thin/NPV answer only claims what it actually carried.
            if (!row.Name.IsEmpty) row.Known |= (uint)ArtistFields.Name;
            row.Header = st.VisualsHeader.IsEmpty ? st.LegacyHeader : st.VisualsHeader;
            row.HeaderAccent = HexColor(s, st.HeaderHex.IsEmpty ? st.AvatarHex : st.HeaderHex);
            // The overview speaks for every group; NPV only for what it carried — a thin answer must not blank a bio or
            // zero the stats the overview already gave (same authority rung, so it WOULD overwrite).
            if (overview || !row.Image.IsEmpty) row.Known |= (uint)ArtistFields.Image;
            if (overview || !row.Header.IsEmpty) row.Known |= (uint)ArtistFields.Header;
            if (overview || st.SawStats) row.Known |= (uint)ArtistFields.Stats;
            if (overview || (st.SawBio && !row.Bio.IsEmpty)) row.Known |= (uint)ArtistFields.Bio;
            if (st.Verified) row.Flags |= (uint)ArtistFlags.Verified;
            if (overview)
            {
                // The answer SPOKE for all four whether or not it carried one (file header): none is an answer.
                row.Known |= (uint)(ArtistFields.Pick | ArtistFields.PreRelease | ArtistFields.Latest | ArtistFields.Tour);
                EndUnspoken(s, in artist, st.Spoke);
            }
            s.Artists.Add() = row;
        }

        /// <summary>Land every list the overview is answerable for and did not carry as Complete-and-empty, so an artist
        /// with no gallery renders "no gallery" rather than a skeleton that never resolves.</summary>
        static void EndUnspoken(Staging s, in StagedId artist, ArtistSpoke spoke)
        {
            if ((spoke & ArtistSpoke.Gallery) == 0) s.RunArtistExtra(ArtistExtraKind.Gallery).End(in artist);
            if ((spoke & ArtistSpoke.Playlists) == 0) s.RunArtistExtra(ArtistExtraKind.Playlists).End(in artist);
            if ((spoke & ArtistSpoke.Videos) == 0) s.RunArtistExtra(ArtistExtraKind.Videos).End(in artist);
            if ((spoke & ArtistSpoke.Merch) == 0) s.RunArtistExtra(ArtistExtraKind.Merch).End(in artist);
            if ((spoke & ArtistSpoke.Cities) == 0) s.RunArtistExtra(ArtistExtraKind.Cities).End(in artist);
            if ((spoke & ArtistSpoke.Links) == 0) s.RunArtistExtra(ArtistExtraKind.Links).End(in artist);
            if ((spoke & ArtistSpoke.Related) == 0) s.Run(Relation.ArtistRelated).EndEvenIfEmpty(in artist);
            if ((spoke & ArtistSpoke.AppearsOn) == 0) s.Run(Relation.ArtistAppearsOn).EndEvenIfEmpty(in artist);
            if ((spoke & ArtistSpoke.Popular) == 0) s.EndPopular(in artist, s.PopularMark, extension: false);
            if ((spoke & ArtistSpoke.Albums) == 0) s.Run(Relation.ArtistAlbums).EndEvenIfEmpty(in artist);
            if ((spoke & ArtistSpoke.Singles) == 0) s.Run(Relation.ArtistSingles).EndEvenIfEmpty(in artist);
            if ((spoke & ArtistSpoke.Compilations) == 0) s.Run(Relation.ArtistCompilations).EndEvenIfEmpty(in artist);
        }

        static void ArtistProfile(ref Utf8JsonReader r, Staging s, in StagedId artist, ref ArtistUnionState st, bool overview)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("name"u8)) { r.Read(); st.Row.Name = s.AddJson(ref r); }
                else if (r.ValueTextEquals("verified"u8)) { r.Read(); st.Verified |= Flag(ref r); }
                else if (r.ValueTextEquals("biography"u8))
                {
                    var bio = OneText(ref r, s, "text"u8);
                    st.SawBio = true;
                    st.Row.Bio = bio;
                    st.Row.BioLead = ArtistLead(s, bio);
                }
                else if (r.ValueTextEquals("externalLinks"u8)) { ArtistLinks(ref r, s, in artist); st.Spoke |= ArtistSpoke.Links; }
                else if (overview && r.ValueTextEquals("pinnedItem"u8)) ArtistPick(ref r, s, ref st.Row);
                else if (overview && r.ValueTextEquals("playlistsV2"u8)) { ArtistPlaylists(ref r, s, in artist); st.Spoke |= ArtistSpoke.Playlists; }
                else SkipValue(ref r);
            }
        }

        static void ArtistVisuals(ref Utf8JsonReader r, Staging s, in StagedId artist, ref ArtistUnionState st)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("avatarImage"u8)) st.Row.Image = ImageNode(ref r, s, ref st.AvatarHex);
                else if (r.ValueTextEquals("headerImage"u8)) st.VisualsHeader = ImageNode(ref r, s, ref st.HeaderHex);
                else if (r.ValueTextEquals("gallery"u8)) { ArtistGallery(ref r, s, in artist); st.Spoke |= ArtistSpoke.Gallery; }
                else SkipValue(ref r);
            }
        }

        static void ArtistStats(ref Utf8JsonReader r, Staging s, in StagedId artist, ref ArtistUnionState st)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("followers"u8)) { r.Read(); st.Row.Followers = (uint)Math.Clamp(Num(ref r), 0, uint.MaxValue); }
                else if (r.ValueTextEquals("monthlyListeners"u8)) { r.Read(); st.Row.Monthly = (uint)Math.Clamp(Num(ref r), 0, uint.MaxValue); }
                else if (r.ValueTextEquals("worldRank"u8)) { r.Read(); st.Row.WorldRank = (ushort)Math.Clamp(Num(ref r), 0, ushort.MaxValue); }
                else if (r.ValueTextEquals("topCities"u8)) { ArtistCities(ref r, s, in artist); st.Spoke |= ArtistSpoke.Cities; }
                else SkipValue(ref r);
            }
        }

        /// <summary><c>onPlatformReputationTrait.verification.{isVerified, isRegistered}</c> — either is the check (MapArtist).</summary>
        static bool ArtistVerified(ref Utf8JsonReader r)
        {
            bool verified = false;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("verification"u8)) { SkipValue(ref r); continue; }
                for (int v = Fields(ref r); Next(ref r, v);)
                {
                    if (r.ValueTextEquals("isVerified"u8) || r.ValueTextEquals("isRegistered"u8)) { r.Read(); verified |= Flag(ref r); }
                    else SkipValue(ref r);
                }
            }
            return verified;
        }

        static void ArtistDiscography(ref Utf8JsonReader r, Staging s, in StagedId artist, ref ArtistUnionState st)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("albums"u8)) { ArtistFacetList(ref r, s, in artist, DiscoFacet.Albums, 0); st.Spoke |= ArtistSpoke.Albums; }
                else if (r.ValueTextEquals("singles"u8)) { ArtistFacetList(ref r, s, in artist, DiscoFacet.Singles, 0); st.Spoke |= ArtistSpoke.Singles; }
                else if (r.ValueTextEquals("compilations"u8)) { ArtistFacetList(ref r, s, in artist, DiscoFacet.Compilations, 0); st.Spoke |= ArtistSpoke.Compilations; }
                else if (r.ValueTextEquals("topTracks"u8)) { ArtistTopTracksSeed(ref r, s, in artist); st.Spoke |= ArtistSpoke.Popular; }
                else if (r.ValueTextEquals("latest"u8))
                {
                    var release = default(ArtistRelease);
                    for (int l = Fields(ref r); Next(ref r, l);) ArtistReleaseProperty(ref r, s, ref release);
                    st.Row.LatestUri = ArtistStageRelease(s, in release, facet: null);
                }
                else SkipValue(ref r);
            }
        }

        static void ArtistRelatedContent(ref Utf8JsonReader r, Staging s, in StagedId artist, ref ArtistUnionState st)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("relatedArtists"u8)) { ArtistRelatedArtists(ref r, s, in artist); st.Spoke |= ArtistSpoke.Related; }
                else if (r.ValueTextEquals("appearsOn"u8)) { ArtistAppearsOn(ref r, s, in artist); st.Spoke |= ArtistSpoke.AppearsOn; }
                else if (r.ValueTextEquals("featuringV2"u8) || r.ValueTextEquals("discoveredOnV2"u8))
                {
                    ArtistPlaylists(ref r, s, in artist);
                    st.Spoke |= ArtistSpoke.Playlists;
                }
                else SkipValue(ref r);
            }
        }

        static void ArtistGoods(ref Utf8JsonReader r, Staging s, in StagedId artist, ref ArtistUnionState st, bool overview)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("merch"u8)) { ArtistMerch(ref r, s, in artist); st.Spoke |= ArtistSpoke.Merch; }
                else if (overview && r.ValueTextEquals("concerts"u8)) ArtistConcerts(ref r, s, in artist);
                else SkipValue(ref r);
            }
        }

        /// <summary><c>profile.pinnedItem</c> (MapPinned): the pin's own display fields plus the identity of what it
        /// points at (<c>itemV2.data</c>). The cover prefers <c>thumbnailImage</c>, then the item's own cover. The wire has
        /// no eyebrow — the surface's "Artist pick" string is the UI's, so <c>PickEyebrow</c> stays empty.</summary>
        static void ArtistPick(ref Utf8JsonReader r, Staging s, ref StagedArtist row)
        {
            TextRef itemCover = default, thumb = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); row.PickUri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("title"u8)) { r.Read(); row.PickTitle = s.AddJson(ref r); }
                else if (r.ValueTextEquals("subtitle"u8)) { r.Read(); row.PickSubtitle = s.AddJson(ref r); }
                else if (r.ValueTextEquals("comment"u8)) { r.Read(); row.PickComment = s.AddJson(ref r); }
                else if (r.ValueTextEquals("thumbnailImage"u8)) { r.Read(); thumb = ArtistDeepUrl(ref r, s); }
                else if (r.ValueTextEquals("backgroundImageV2"u8)) { r.Read(); row.PickBackground = ArtistDeepUrl(ref r, s); }
                else if (r.ValueTextEquals("itemV2"u8))
                {
                    for (int w = Fields(ref r); Next(ref r, w);)
                    {
                        if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                        for (int i = Fields(ref r); Next(ref r, i);)
                        {
                            if (r.ValueTextEquals("uri"u8)) { r.Read(); row.PickItemUri = s.AddJson(ref r); }
                            else if (r.ValueTextEquals("coverArt"u8)) { r.Read(); itemCover = ArtistDeepUrl(ref r, s); }
                            else if (r.ValueTextEquals("preReleaseEndDateTime"u8))
                            {
                                r.Read();
                                if (r.TokenType == JsonTokenType.String && !r.HasValueSequence
                                    && ArtistIsoInstant(r.ValueSpan, out long ms, out _))
                                    row.PickReleaseAt = (int)(ms / 1000);
                            }
                            else SkipValue(ref r);
                        }
                    }
                }
                else SkipValue(ref r);
            }
            row.PickCover = thumb.IsEmpty ? itemCover : thumb;
            if (!row.PickItemUri.IsEmpty) row.PickItemKind = (byte)EntityUri.KindOf(s.Utf8(row.PickItemUri));
        }

        /// <summary><c>preReleaseV2.data</c> (MapPreRelease): a real one needs a uri AND a name; the server only answers an
        /// UPCOMING release here, so a staged one carries <see cref="ArtistFlags.Upcoming"/>. A missing end instant is
        /// tolerated (the card announces, it cannot count down).</summary>
        static void ArtistPreRelease(ref Utf8JsonReader r, Staging s, ref StagedArtist row)
        {
            TextRef uri = default, name = default, cover = default, type = default;
            int releaseAt = 0;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int i = Fields(ref r); Next(ref r, i);)
                {
                    if (r.ValueTextEquals("uri"u8)) { r.Read(); uri = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("coverArt"u8)) { r.Read(); cover = ArtistDeepUrl(ref r, s); }
                    else if (r.ValueTextEquals("type"u8)) { r.Read(); type = s.AddJson(ref r); }
                    else if (r.ValueTextEquals("preReleaseEndDateTime"u8))
                    {
                        r.Read();
                        if (r.TokenType == JsonTokenType.String && !r.HasValueSequence && ArtistIsoInstant(r.ValueSpan, out long ms, out _))
                            releaseAt = (int)(ms / 1000);
                    }
                    else SkipValue(ref r);
                }
            }
            if (uri.IsEmpty || name.IsEmpty) return;
            row.UpcomingUri = uri;
            row.UpcomingName = name;
            row.UpcomingCover = cover;
            row.UpcomingType = type;
            row.UpcomingReleaseAt = releaseAt;
            row.Flags |= (uint)ArtistFlags.Upcoming;
        }

        /// <summary>The first <c>url</c> string ANYWHERE under the value (FirstUrl only descends sources/items/image; a
        /// pinned item's <c>thumbnailImage.data.sources</c> and a video's <c>visualIdentityTrait</c> nest deeper).</summary>
        static TextRef ArtistDeepUrl(ref Utf8JsonReader r, Staging s)
        {
            if (r.TokenType == JsonTokenType.String) return s.AddJson(ref r);
            if (r.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) { r.Skip(); return default; }
            int depth = r.CurrentDepth;
            TextRef url = default;
            while (r.Read() && !End(ref r, depth))
            {
                if (r.TokenType != JsonTokenType.PropertyName || !r.ValueTextEquals("url"u8)) continue;
                r.Read();
                if (url.IsEmpty && r.TokenType == JsonTokenType.String) url = s.AddJson(ref r);
            }
            return url;
        }

        /// <summary>The hero's lead sentence, computed ONCE here into the arena (ch 08 GAP 3, P11).</summary>
        static TextRef ArtistLead(Staging s, TextRef bio)
        {
            if (bio.IsEmpty) return default;
            var html = s.Utf8(bio);
            Span<byte> buffer = html.Length <= 1024 ? stackalloc byte[html.Length] : new byte[html.Length];
            int n = ArtistText.Lead(html, buffer);
            return n <= 0 ? default : s.AddText(buffer[..n]);
        }

        static ReadOnlySpan<byte> ArtistFacetName(DiscoFacet facet) => facet switch
        {
            DiscoFacet.Singles => "singles"u8,
            DiscoFacet.Compilations => "compilations"u8,
            _ => "albums"u8,
        };

        static Relation ArtistFacetRelation(DiscoFacet facet) => facet switch
        {
            DiscoFacet.Singles => Relation.ArtistSingles,
            DiscoFacet.Compilations => Relation.ArtistCompilations,
            _ => Relation.ArtistAlbums,
        };

        // ── the discography ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One facet list <c>{ items[] { releases { items[] } }, totalCount }</c> → a PAGE of the facet's relation
        /// at <paramref name="offset"/> with the stated total. A stated total of 0 is a whole, Complete list (an artist
        /// with no compilations must not be Partial forever and re-asked); an unstated one stays Partial.</summary>
        static void ArtistFacetList(ref Utf8JsonReader r, Staging s, in StagedId artist, DiscoFacet facet, int offset)
        {
            int mark = s.Edges.PendingMark;
            long total = -1;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("totalCount"u8)) { r.Read(); total = Num(ref r, -1); }
                else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                {
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        // A group wraps `releases.items[]`; a page may also list the release itself. Both are read at once
                        // (forward-only), and the group's first release wins.
                        var release = default(ArtistRelease);
                        StagedId first = default;
                        for (int e = r.CurrentDepth; Next(ref r, e);)
                        {
                            if (r.ValueTextEquals("releases"u8))
                            {
                                var id = ArtistFirstRelease(ref r, s, facet);
                                if (first.IsEmpty) first = id;
                            }
                            else ArtistReleaseProperty(ref r, s, ref release);
                        }
                        if (first.IsEmpty) first = ArtistStageRelease(s, in release, facet);
                        if (!first.IsEmpty) s.Edges.Push().Target = first;
                    }
                }
                else SkipValue(ref r);
            }

            var relation = ArtistFacetRelation(facet);
            if (total == 0 && offset == 0) s.Edges.Close(relation, in artist, mark, EdgeState.Complete, 0);
            else s.Edges.ClosePage(relation, in artist, mark, offset, (int)Math.Clamp(total, 0, int.MaxValue));
        }

        /// <summary>A group's <c>releases { items[] }</c> → its FIRST release, staged; the rest of the group skipped.</summary>
        static StagedId ArtistFirstRelease(ref Utf8JsonReader r, Staging s, DiscoFacet? facet)
        {
            StagedId first = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    if (!first.IsEmpty) { r.Skip(); continue; }
                    var release = default(ArtistRelease);
                    for (int e = r.CurrentDepth; Next(ref r, e);) ArtistReleaseProperty(ref r, s, ref release);
                    first = ArtistStageRelease(s, in release, facet);
                }
            }
            return first;
        }

        /// <summary>One discography release, before it is staged (MapRelease's field picks).</summary>
        struct ArtistRelease
        {
            public StagedId Uri;
            public TextRef Name, Image, Type, Label, Copyright, Courtesy, ShareUrl, Iso;
            public int Year, Month, Day, Tracks;
            public byte Precision;
            public bool PrecisionStated;
        }

        static void ArtistReleaseProperty(ref Utf8JsonReader r, Staging s, ref ArtistRelease rel)
        {
            if (r.ValueTextEquals("uri"u8)) { r.Read(); if (rel.Uri.IsEmpty) rel.Uri = JsonId(ref r, s); }
            else if (r.ValueTextEquals("name"u8)) { r.Read(); if (rel.Name.IsEmpty) rel.Name = s.AddJson(ref r); }
            else if (r.ValueTextEquals("coverArt"u8)) { r.Read(); rel.Image = FirstUrl(ref r, s); }
            else if (r.ValueTextEquals("type"u8)) { r.Read(); rel.Type = s.AddJson(ref r); }
            else if (r.ValueTextEquals("tracks"u8)) rel.Tracks = (int)OneNumber(ref r, "totalCount"u8);
            else if (r.ValueTextEquals("label"u8)) { r.Read(); rel.Label = s.AddJson(ref r); }
            else if (r.ValueTextEquals("sharingInfo"u8)) rel.ShareUrl = OneText(ref r, s, "shareUrl"u8);
            else if (r.ValueTextEquals("copyright"u8)) ArtistCopyright(ref r, s, ref rel);
            else if (r.ValueTextEquals("date"u8)) ArtistReleaseDate(ref r, s, ref rel);
            else SkipValue(ref r);
        }

        /// <summary><c>date</c>: the discography's <c>{year, month, day, precision}</c>, or an <c>isoString</c>, or a bare
        /// string. The wire's own precision word beats the fields' shape.</summary>
        static void ArtistReleaseDate(ref Utf8JsonReader r, Staging s, ref ArtistRelease rel)
        {
            if (r.TokenType == JsonTokenType.PropertyName && !r.Read()) return;
            if (r.TokenType == JsonTokenType.String) { ArtistReleaseIso(ref r, s, ref rel); return; }
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
            for (int depth = r.CurrentDepth; Next(ref r, depth);)
            {
                if (r.ValueTextEquals("year"u8)) { r.Read(); rel.Year = (int)Num(ref r); }
                else if (r.ValueTextEquals("month"u8)) { r.Read(); rel.Month = (int)Num(ref r); }
                else if (r.ValueTextEquals("day"u8)) { r.Read(); rel.Day = (int)Num(ref r); }
                else if (r.ValueTextEquals("isoString"u8)) { r.Read(); ArtistReleaseIso(ref r, s, ref rel); }
                else if (r.ValueTextEquals("precision"u8))
                {
                    r.Read();
                    if (Says(ref r, "YEAR")) { rel.Precision = 0; rel.PrecisionStated = true; }
                    else if (Says(ref r, "MONTH")) { rel.Precision = 1; rel.PrecisionStated = true; }
                    else if (Says(ref r, "DAY")) { rel.Precision = 2; rel.PrecisionStated = true; }
                }
                else SkipValue(ref r);
            }

            static void ArtistReleaseIso(ref Utf8JsonReader r, Staging s, ref ArtistRelease rel)
            {
                rel.Iso = s.AddJson(ref r);
                var iso = s.Utf8(rel.Iso);
                IsoDate(iso, out ushort year, out _, out byte precision);
                if (rel.Year <= 0) rel.Year = year;
                if (iso.Length >= 7 && rel.Month <= 0) rel.Month = (int)Number(iso.Slice(5, 2));
                if (iso.Length >= 10 && rel.Day <= 0) rel.Day = (int)Number(iso.Slice(8, 2));
                if (!rel.PrecisionStated) rel.Precision = precision;
            }
        }

        /// <summary><c>copyright.items[] { text, type }</c> — "C" the © line, "P" the ℗ courtesy line.</summary>
        static void ArtistCopyright(ref Utf8JsonReader r, Staging s, ref ArtistRelease rel)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    TextRef text = default;
                    bool phono = false;
                    for (int line = r.CurrentDepth; Next(ref r, line);)
                    {
                        if (r.ValueTextEquals("text"u8)) { r.Read(); text = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("type"u8)) { r.Read(); phono = Says(ref r, "P"); }
                        else SkipValue(ref r);
                    }
                    if (text.IsEmpty) continue;
                    if (phono) { if (rel.Courtesy.IsEmpty) rel.Courtesy = text; }
                    else if (rel.Copyright.IsEmpty) rel.Copyright = text;
                }
            }
        }

        /// <summary>Stage one release as a THIN album row with the card group (ch 08 GAP 17): the kind from the wire's type
        /// word (a SINGLE of ≥ 4 tracks is an EP), coerced to the facet's own kind when the two disagree
        /// (<see cref="ArtistCatalog.KindMatches"/> — the facet the server listed it under wins, so the counts agree); the
        /// ISO date at the stated precision (MapRelease's ReleaseDateIso).</summary>
        static StagedId ArtistStageRelease(Staging s, in ArtistRelease rel, DiscoFacet? facet)
        {
            if (rel.Uri.IsEmpty || rel.Uri.Kind(s) != EntityKind.Album) return default;

            int month = rel.Month is >= 1 and <= 12 ? rel.Month : 0;
            int day = month > 0 && rel.Year is >= 1 and <= 9999 && rel.Day >= 1 && rel.Day <= DateTime.DaysInMonth(rel.Year, month)
                ? rel.Day : 0;
            byte precision = rel.PrecisionStated ? rel.Precision : day > 0 ? (byte)2 : month > 0 ? (byte)1 : (byte)0;
            TextRef iso = rel.Iso;
            int at = 0;
            if (rel.Year is >= 1 and <= 9999)
            {
                if (iso.IsEmpty)
                {
                    Span<byte> buf = stackalloc byte[10];
                    ArtistWriteDigits(buf, 0, rel.Year, 4);
                    buf[4] = (byte)'-';
                    ArtistWriteDigits(buf, 5, precision >= 1 && month > 0 ? month : 1, 2);
                    buf[7] = (byte)'-';
                    ArtistWriteDigits(buf, 8, precision >= 2 && day > 0 ? day : 1, 2);
                    iso = s.AddText(buf);
                }
                at = Seconds(rel.Year, month > 0 ? month : 1, day > 0 ? day : 1);
            }

            var kind = ArtistCatalog.KindOf(rel.Type.IsEmpty ? default : s.Utf8(rel.Type), rel.Tracks);
            if (facet is { } f && !ArtistCatalog.KindMatches(kind, f)) kind = ArtistCatalog.CanonicalKind(f, rel.Tracks);

            uint known = (uint)(AlbumFields.Title | AlbumFields.Image | AlbumFields.Year | AlbumFields.Kind);
            if (rel.Tracks > 0) known |= (uint)AlbumFields.TrackCount;
            if (rel.Year > 0) known |= (uint)AlbumFields.Release;
            if (!rel.Label.IsEmpty || !rel.Copyright.IsEmpty || !rel.Courtesy.IsEmpty) known |= (uint)AlbumFields.Publishing;

            ref var row = ref s.Albums.RowFor(rel.Uri, Authority.Thin, known);
            row.Title = rel.Name;
            row.Image = rel.Image;
            row.Year = (ushort)Math.Clamp(rel.Year, 0, ushort.MaxValue);
            row.TrackCount = Math.Max(0, rel.Tracks);
            row.Kind = (byte)kind;
            row.ReleaseDateIso = iso;
            row.ReleaseAt = at;
            row.DatePrecision = precision;
            row.Label = rel.Label;
            row.Copyright = rel.Copyright;
            row.Courtesy = rel.Courtesy;
            row.ShareUrl = rel.ShareUrl;
            return rel.Uri;
        }

        static void ArtistWriteDigits(Span<byte> into, int at, int value, int width)
        {
            for (int i = width - 1; i >= 0; i--)
            {
                into[at + i] = (byte)('0' + value % 10);
                value /= 10;
            }
        }

        // ── the lists ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>discography.topTracks.items[] { track }</c> → the chart SEED, in the overview's order, with its play
        /// counts on the track rows.</summary>
        static void ArtistTopTracksSeed(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            int mark = s.PopularMark;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    var node = default(Node);
                    EntityNode(ref r, s, ref node);               // unwraps `track`, closes the track's own artist run
                    var id = Stage(s, in node, Authority.Thin);
                    if (!id.IsEmpty && id.Kind(s) == EntityKind.Track) s.PopularTracks.Add() = id;
                }
            }
            s.EndPopular(in artist, mark, extension: false);
        }

        /// <summary><c>relatedContent.relatedArtists.items[]</c> → <c>Edges.ArtistRelated</c>, Complete (empty included).</summary>
        static void ArtistRelatedArtists(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            int mark = s.Edges.PendingMark;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    var node = default(Node);
                    EntityNode(ref r, s, ref node);
                    var id = Stage(s, in node, Authority.Thin);
                    if (!id.IsEmpty && id.Kind(s) == EntityKind.Artist) s.Edges.Push().Target = id;
                }
            }
            s.Edges.Close(Relation.ArtistRelated, in artist, mark);
        }

        /// <summary><c>relatedContent.appearsOn.items[] { releases { items[] } }</c> → <c>Edges.ArtistAppearsOn</c>: the
        /// shelf the answer carries, Complete.</summary>
        static void ArtistAppearsOn(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            int mark = s.Edges.PendingMark;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    var release = default(ArtistRelease);
                    StagedId first = default;
                    for (int e = r.CurrentDepth; Next(ref r, e);)
                    {
                        if (r.ValueTextEquals("releases"u8)) { var id = ArtistFirstRelease(ref r, s, null); if (first.IsEmpty) first = id; }
                        else ArtistReleaseProperty(ref r, s, ref release);
                    }
                    if (first.IsEmpty) first = ArtistStageRelease(s, in release, null);
                    if (!first.IsEmpty) s.Edges.Push().Target = first;
                }
            }
            s.Edges.Close(Relation.ArtistAppearsOn, in artist, mark);
        }

        /// <summary>A playlist list (<c>profile.playlistsV2</c>, <c>relatedContent.featuringV2</c> /
        /// <c>discoveredOnV2</c>) <c>{ items[] { data { … ownerV2 { data { name } } } } }</c> → one Playlists run per list
        /// (the commit concatenates an artist's runs and drops repeats). The subtitle is the owner's name, "Spotify" when
        /// none (MapPlaylistRefs). An item that is not a playlist (a GenericError) is skipped.</summary>
        static void ArtistPlaylists(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Playlists);
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    var node = default(Node);
                    TextRef owner = default;
                    int credit = s.CreditMark, mark = s.Edges.PendingMark;
                    for (int e = r.CurrentDepth; Next(ref r, e);)
                    {
                        if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                        for (int p = Fields(ref r); Next(ref r, p);)
                        {
                            if (r.ValueTextEquals("ownerV2"u8) || r.ValueTextEquals("owner"u8)) owner = ArtistOwnerName(ref r, s);
                            else NodeProperty(ref r, s, ref node, credit);
                        }
                    }
                    s.TakeCredit(credit);
                    s.Edges.Pop(mark);
                    if (node.Uri.IsEmpty || node.Uri.Kind(s) != EntityKind.Playlist) continue;
                    var id = Stage(s, in node, Authority.Thin);
                    ref var member = ref run.Add();
                    member.Target = id;
                    member.T0 = owner.IsEmpty ? s.AddText("Spotify"u8) : owner;
                }
            }
            run.End(in artist);
        }

        /// <summary><c>ownerV2 { data { name } }</c> → the name.</summary>
        static TextRef ArtistOwnerName(ref Utf8JsonReader r, Staging s)
        {
            TextRef name = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                else if (r.ValueTextEquals("data"u8)) { var inner = ArtistOwnerName(ref r, s); if (name.IsEmpty) name = inner; }
                else SkipValue(ref r);
            }
            return name;
        }

        /// <summary><c>visuals.gallery.items[] { sources }</c> → <c>Edges.ArtistGallery</c> (payload = the image).</summary>
        static void ArtistGallery(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Gallery);
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    var url = ArtistDeepUrl(ref r, s);
                    if (!url.IsEmpty) run.Add().T0 = url;
                }
            }
            run.End(in artist);
        }

        /// <summary><c>relatedMusicVideos</c> / <c>unmappedMusicVideosV2</c> <c>{ items[] { _uri, data } }</c> → one Videos
        /// run per list (MapMusicVideos: two envelopes, de-duplicated by the commit). A Track-shaped item (it carries its
        /// artists) is staged as a thin track; an Entity-shaped one lands as the edge alone, for the page to demand.
        /// The thumb is the 16:9 still wherever the node keeps it.</summary>
        static void ArtistVideos(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Videos);
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    var node = default(Node);
                    StagedId wrapperUri = default;
                    TextRef thumb = default;
                    int duration = 0;
                    bool trackShaped = false;
                    int credit = s.CreditMark, mark = s.Edges.PendingMark;
                    for (int e = r.CurrentDepth; Next(ref r, e);)
                    {
                        if (r.ValueTextEquals("_uri"u8)) { r.Read(); if (wrapperUri.IsEmpty) wrapperUri = JsonId(ref r, s); }
                        else if (r.ValueTextEquals("data"u8))
                        {
                            for (int p = Fields(ref r); Next(ref r, p);)
                            {
                                if (r.ValueTextEquals("albumOfTrack"u8) || r.ValueTextEquals("coverArt"u8)
                                    || r.ValueTextEquals("thumbnailImage"u8) || r.ValueTextEquals("visualIdentityTrait"u8))
                                { r.Read(); var url = ArtistDeepUrl(ref r, s); if (thumb.IsEmpty) thumb = url; }
                                else if (r.ValueTextEquals("duration"u8)) duration = (int)OneNumber(ref r, "totalMilliseconds"u8);
                                else if (r.ValueTextEquals("identityTrait"u8)) node.Name = ArtistIdentityName(ref r, s);
                                else
                                {
                                    trackShaped |= r.ValueTextEquals("artists"u8);
                                    NodeProperty(ref r, s, ref node, credit);
                                }
                            }
                        }
                        else SkipValue(ref r);
                    }
                    if (node.ArtistLine.IsEmpty) node.ArtistLine = s.TakeCredit(credit);
                    else s.TakeCredit(credit);
                    if (node.Uri.IsEmpty) node.Uri = wrapperUri;
                    if (s.Edges.Pending(mark) > 0) CloseArtists(s, in node, mark);
                    if (node.Uri.IsEmpty || node.Uri.Kind(s) != EntityKind.Track) continue;
                    node.DurationMs = duration;
                    if (trackShaped && !node.Name.IsEmpty) Stage(s, in node, Authority.Thin);
                    ref var member = ref run.Add();
                    member.Target = node.Uri;
                    member.T0 = thumb;
                    member.I0 = duration;
                }
            }
            run.End(in artist);
        }

        /// <summary><c>identityTrait { name }</c> → the name.</summary>
        static TextRef ArtistIdentityName(ref Utf8JsonReader r, Staging s)
        {
            TextRef name = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                else SkipValue(ref r);
            }
            return name;
        }

        /// <summary><c>goods.merch.items[] { nameV2 | name, price, image, url }</c> → merch rows (MapMerch).</summary>
        static void ArtistMerch(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Merch);
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    TextRef nameV2 = default, name = default, price = default, image = default, url = default;
                    for (int e = r.CurrentDepth; Next(ref r, e);)
                    {
                        if (r.ValueTextEquals("nameV2"u8)) { r.Read(); nameV2 = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("price"u8)) { r.Read(); price = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("image"u8)) { r.Read(); image = ArtistDeepUrl(ref r, s); }
                        else if (r.ValueTextEquals("url"u8)) { r.Read(); url = s.AddJson(ref r); }
                        else SkipValue(ref r);
                    }
                    var title = nameV2.IsEmpty ? name : nameV2;
                    if (title.IsEmpty) continue;
                    ref var member = ref run.Add();
                    member.T0 = title;
                    member.T1 = price;
                    member.T2 = image;
                    member.T3 = url;
                }
            }
            run.End(in artist);
        }

        /// <summary><c>stats.topCities.items[] { city, country, numberOfListeners }</c> → <c>Edges.ArtistCities</c>.</summary>
        static void ArtistCities(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Cities);
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    TextRef city = default, country = default;
                    long listeners = 0;
                    for (int e = r.CurrentDepth; Next(ref r, e);)
                    {
                        if (r.ValueTextEquals("city"u8)) { r.Read(); city = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("country"u8)) { r.Read(); country = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("numberOfListeners"u8)) { r.Read(); listeners = Num(ref r); }
                        else SkipValue(ref r);
                    }
                    if (city.IsEmpty) continue;
                    ref var member = ref run.Add();
                    member.T0 = city;
                    member.T1 = country;
                    member.U0 = (uint)Math.Clamp(listeners, 0, uint.MaxValue);
                }
            }
            run.End(in artist);
        }

        /// <summary><c>profile.externalLinks.items[] { name, url }</c> → <c>Edges.ArtistLinks</c>: the name title-cased
        /// ("INSTAGRAM" → "Instagram") and the glyph classified (MapLinks).</summary>
        static void ArtistLinks(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Links);
            Span<byte> cased = stackalloc byte[64];
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    TextRef name = default, url = default;
                    for (int e = r.CurrentDepth; Next(ref r, e);)
                    {
                        if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("url"u8)) { r.Read(); url = s.AddJson(ref r); }
                        else SkipValue(ref r);
                    }
                    if (url.IsEmpty) continue;
                    ref var member = ref run.Add();
                    int n = ArtistCatalog.TitleCase(s.Utf8(name), cased);
                    member.B0 = (byte)ArtistCatalog.ClassifyLink(s.Utf8(name), s.Utf8(url));
                    member.T0 = n > 0 ? s.AddText(cased[..n]) : default;
                    member.T1 = url;
                }
            }
            run.End(in artist);
        }

        /// <summary><c>goods.concerts { items[] { data { uri, title, location { name, city }, startDateIsoString,
        /// festival } }, totalCount }</c> → the concert rows and, WHEN THE LIST IS WHOLE (items ≥ totalCount), the
        /// artist's <c>ArtistConcerts</c> run through N-C's cursor (which derives the tour banner at commit). A capped
        /// list (10 of 12) is not a complete schedule and is left to the <c>ArtistConcerts</c> route.</summary>
        static void ArtistConcerts(ref Utf8JsonReader r, Staging s, in StagedId artist)
        {
            var run = s.ConcertRun(ConcertLink.ArtistConcerts);
            long total = -1;
            int n = 0;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("totalCount"u8)) { r.Read(); total = Num(ref r, -1); }
                else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                {
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        StagedId id = default;
                        TextRef title = default, venue = default, city = default;
                        long dateMs = 0;
                        short offset = 0;
                        bool dated = false, festival = false;
                        for (int e = r.CurrentDepth; Next(ref r, e);)
                        {
                            if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                            for (int p = Fields(ref r); Next(ref r, p);)
                            {
                                if (r.ValueTextEquals("uri"u8)) { r.Read(); id = JsonId(ref r, s); }
                                else if (r.ValueTextEquals("title"u8)) { r.Read(); title = s.AddJson(ref r); }
                                else if (r.ValueTextEquals("festival"u8)) { r.Read(); festival = Flag(ref r); }
                                else if (r.ValueTextEquals("startDateIsoString"u8))
                                {
                                    r.Read();
                                    dated = r.TokenType == JsonTokenType.String && !r.HasValueSequence
                                         && ArtistIsoInstant(r.ValueSpan, out dateMs, out offset);
                                }
                                else if (r.ValueTextEquals("location"u8))
                                {
                                    for (int l = Fields(ref r); Next(ref r, l);)
                                    {
                                        if (r.ValueTextEquals("name"u8)) { r.Read(); venue = s.AddJson(ref r); }
                                        else if (r.ValueTextEquals("city"u8)) { r.Read(); city = s.AddJson(ref r); }
                                        else SkipValue(ref r);
                                    }
                                }
                                else SkipValue(ref r);
                            }
                        }
                        if (id.IsEmpty || !dated) continue;
                        ref var row = ref s.Concerts.RowFor(id, Authority.Thin, (uint)ConcertFields.Identity);
                        row.Title = title;
                        row.Venue = venue;
                        row.City = city;
                        row.Date = dateMs;
                        row.OffsetMinutes = offset;
                        if (festival) row.Flags |= (uint)ConcertFlags.Festival;
                        run.Add(in id);
                        n++;
                    }
                }
                else SkipValue(ref r);
            }
            if (total < 0 || n >= total) run.End(in artist);
            else run.Discard();
        }

        /// <summary>An ISO-8601 instant with its own offset — <c>2026-06-25T16:00+02:00</c>, <c>…:30Z</c>,
        /// <c>…:30.000+0100</c>, or a bare date (midnight UTC) — to unix MILLISECONDS and the offset in minutes (ch 17: the
        /// provider's local clock rides beside the instant). PURE.</summary>
        public static bool ArtistIsoInstant(ReadOnlySpan<byte> iso, out long unixMs, out short offsetMinutes)
        {
            unixMs = 0;
            offsetMinutes = 0;
            if (iso.Length < 10 || iso[4] != (byte)'-' || iso[7] != (byte)'-') return false;
            long year = Number(iso[..4]), month = Number(iso.Slice(5, 2)), day = Number(iso.Slice(8, 2));
            if (year is < 1 or > 9999 || month is < 1 or > 12 || day is < 1 or > 31) return false;
            long seconds = Seconds((int)year, (int)month, (int)day);
            int i = 10;
            if (i < iso.Length && (iso[i] == (byte)'T' || iso[i] == (byte)' '))
            {
                if (iso.Length < i + 6 || iso[i + 3] != (byte)':') return false;
                long hour = Number(iso.Slice(i + 1, 2)), minute = Number(iso.Slice(i + 4, 2));
                if (hour is < 0 or > 23 || minute is < 0 or > 59) return false;
                seconds += hour * 3600 + minute * 60;
                i += 6;
                if (i + 2 < iso.Length && iso[i] == (byte)':')
                {
                    long second = Number(iso.Slice(i + 1, 2));
                    if (second is < 0 or > 60) return false;
                    seconds += second;
                    i += 3;
                }
                long ms = 0;
                if (i < iso.Length && iso[i] == (byte)'.')
                {
                    int start = ++i, digits = 0;
                    while (i < iso.Length && iso[i] >= (byte)'0' && iso[i] <= (byte)'9') { if (digits < 3) { ms = ms * 10 + (iso[i] - '0'); digits++; } i++; }
                    for (; digits < 3 && i > start; digits++) ms *= 10;
                }
                if (i < iso.Length)
                {
                    byte sign = iso[i];
                    if (sign == (byte)'Z') { }
                    else if (sign is (byte)'+' or (byte)'-')
                    {
                        var tail = iso[(i + 1)..];
                        long oh, om;
                        if (tail.Length >= 5 && tail[2] == (byte)':') { oh = Number(tail[..2]); om = Number(tail.Slice(3, 2)); }
                        else if (tail.Length >= 4) { oh = Number(tail[..2]); om = Number(tail.Slice(2, 2)); }
                        else if (tail.Length == 2) { oh = Number(tail); om = 0; }
                        else return false;
                        if (oh is < 0 or > 18 || om is < 0 or > 59) return false;
                        long off = oh * 60 + om;
                        if (sign == (byte)'-') off = -off;
                        offsetMinutes = (short)off;
                        seconds -= off * 60;                      // local wall clock − offset = UTC
                    }
                    else return false;
                }
                unixMs = seconds * 1000 + ms;
                return true;
            }
            unixMs = seconds * 1000;
            return true;
        }
    }
}
