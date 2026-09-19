// ── Spotify/Spotify.Decode.Home.cs ─────────────────────────────────────────────────────────────────────────────────
// the Home document fold for ANY subject, the what's-new feed and the account's top content
//
// Role: CORE
// Owner: P
// Wave: 5
// Budget: 1,400 lines (DERIVED — no plan row names this file; three folds, one of them string-producing, reported)
// Spec: ch 10 §7 (the section ledger, the typename verdict, the card facts), ch 11 §7 DATA GAPS (userTopContent),
//   ch 19 §7 (what's-new rows); WP-5.P contract §2.7; 0.2.9 `SpotifyHomeComposer.cs`, `SpotifyExportMapper.cs:1142-1520`
//   (CardFromEntity / ParseSeeds / RecentCardsFromListData / AccentArgb), `SpotifyWhatsNewService.Parse`,
//   `SpotifyExportMapper.TopArtistsFromUserTop / TopTracksFromUserTop`
//
// WHY A SECOND HOME FOLD. B1's `Decode.Home` (Pathfinder.cs) was the Wave-2 skeleton and it is wrong in four ways that
// ch 10's projections read: it files `HomeShortsSectionData` as a BASELINE band (every shorts card grew an eyebrow)
// and `HomeFeedBaselineSectionData` as a GENERIC one (the discover feed never formed); it reads the section ITEM's own
// `uri`, so the shorts band's `UnknownType` wrapper around `spotify:user:@:collection` became a nameless Liked card
// instead of an unsupported item; it treats the recently-played band's single `List` wrapper as one card (0.2.9 walks
// the wrapped list — twenty cards, and the list's own total); and it stages none of what a home card PAINTS that no
// entity column holds. This fold keeps B1's reader idiom and its helpers (`Fields`/`Next`/`Element`, `JsonId`,
// `Label`, `Chips`, `NodeProperty`, `Stage`) and replaces only the card walk and the band verdict.
//
// THE CARD WALK READS `content.data` AND NOTHING ELSE (0.2.9 `CardFromEntity(Dig(item, "content", "data"))`). The kind
// is the wire's `__typename` — Album / Playlist / Artist / Episode / Audiobook / Podcast — and anything else is
// UNSUPPORTED on the ledger (`Raw == Cards + Unsupported + Duplicates`). The entity row goes through `Decode.Stage` at
// THIN authority, exactly like every other pathfinder mention; the section's CARD FACTS (Home.cs §2b) carry the rest:
// the payload accent for every kind (`extractedColors.colorDark`, `isFallback` rejected, walked in 0.2.9's order), the
// subtitle line 0.2.9's mapper wrote, the RAW format token, the seeds, an episode's video / resume, an audiobook's
// author / rating / signifier.
//
// THE RECENTLY-PLAYED BAND'S ROWS ARE MENTIONS, AT SEED AUTHORITY. Its list carries title + art + contributor names and
// nothing else, and the same playlist is usually ALSO a full card two bands earlier in the same answer — a thin write of
// the sparse row would land after the rich one in the same commit and blank its description, owner and track count.
// `Authority.Seed` is the rung that FILLS a group nobody has filled and never degrades one (Entities.cs D16).
//
// ZERO ALLOCATION. No string is built: every text is a `TextRef` in the staging arena, a seed is a SLICE of the
// description it was parsed from, and the subtitle compositions ("Song - A, B", "Single - SHAUN") are composed on the
// stack. `WhatsNew` is the one string-producing fold here, for the same reason `Decode.Notifications` is: its product is
// the `Notification` values `Notify.Rebuild` publishes.

using System.Text;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── 1. the Home document ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>data.home</c> for ANY subject (B1b gap 3): the <see cref="StagedHome"/> row keyed by
        /// <paramref name="subjectUri"/> (<c>wavee:home</c>, or <c>wavee:home:&lt;facet&gt;</c> with its Facet text), one
        /// <see cref="StagedSection"/> per band with its card facts and its <see cref="Relation.SectionCards"/> run, and
        /// the subject's <see cref="Relation.HomeSection"/> run — ALWAYS closed, so an empty facet is an answered, Complete
        /// empty list. Accepts the whole answer (<c>{"data":{"home":…}}</c>); a missing or null <c>home</c> stages
        /// nothing (the caller's to fail).</summary>
        public static void HomeFeed(ReadOnlySpan<byte> json, ReadOnlySpan<byte> subjectUri, Staging s)
        {
            s.ClearCredit();
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int data = Fields(ref r); Next(ref r, data);)
                {
                    if (r.ValueTextEquals("home"u8)) { r.Read(); HomeFeed(ref r, subjectUri, s); }
                    else SkipValue(ref r);
                }
            }
        }

        /// <summary>The same fold with the reader ON the <c>home</c> value — the shape <see cref="Export"/>'s dispatch
        /// calls (the reported patch).</summary>
        public static void HomeFeed(ref Utf8JsonReader r, ReadOnlySpan<byte> subjectUri, Staging s)
            => HomeFeedFold.Feed(ref r, subjectUri.IsEmpty ? "wavee:home"u8 : subjectUri, s);

        // ── 2. what's new ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>queryWhatsNewFeed</c> → the new-release rows, appended to <paramref name="into"/>; returns how many
        /// (0.2.9 <c>SpotifyWhatsNewService.Parse</c>, verbatim rules): Album and Episode items only (an unknown typename
        /// is skipped); <c>uri</c> and <c>name</c> required; <c>state.state == "SEEN"</c> is read; the instant is the
        /// item's <c>timestamp.isoString</c>, else the album's <c>date</c> / the episode's <c>releaseDate</c>; an album's
        /// creator is its artists joined ", ", an episode's its show; <c>playedState.state == "FULLY_PLAYED"</c> is played.
        /// <para>The row's <c>Id</c> is the uri. Its <c>Subject</c> is set for a GID-form uri only — a decoder may not
        /// intern (C1); the feed host re-parses a text-form subject on the UI thread before it publishes.</para></summary>
        public static int WhatsNew(ReadOnlySpan<byte> json, List<Notification> into)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return 0;
            int added = 0;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int data = Fields(ref r); Next(ref r, data);)
                {
                    if (!r.ValueTextEquals("whatsNewFeedItems"u8)) { SkipValue(ref r); continue; }
                    for (int feed = Fields(ref r); Next(ref r, feed);)
                    {
                        if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                        for (int list = r.CurrentDepth; Element(ref r, list);)
                            if (HomeFeedFold.Release(ref r, into)) added++;
                    }
                }
            }
            return added;
        }

        // ── 3. the account's top content ─────────────────────────────────────────────────────────────────────────────

        /// <summary><c>userTopContent</c> → <see cref="Edges.UserTopArtists"/> and <see cref="Edges.UserTopTracks"/> over
        /// <paramref name="meUri"/>, rank order (0.2.9 <c>TopArtistsFromUserTop</c> / <c>TopTracksFromUserTop</c>): each
        /// list from the FIRST non-empty of <c>data.me.profile</c>, <c>data.me</c>, <c>data</c>; items wrapped
        /// (<c>{data}</c>, <c>{track}</c>, <c>{itemV2:{data}}</c>) or bare; an artist without a name is dropped; a repeated
        /// uri keeps its first occurrence. A document with a <c>data</c> object always closes BOTH runs (an empty podium
        /// is an answer); one without stages nothing.</summary>
        public static void UserTop(ReadOnlySpan<byte> json, ReadOnlySpan<byte> meUri, Staging s)
        {
            if (meUri.IsEmpty) return;
            s.ClearCredit();
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;

            // [priority] = (start, count) into s.TopTargets; priority 0 = me.profile, 1 = me, 2 = data.
            Span<int> artistStart = stackalloc int[3];
            Span<int> artistCount = stackalloc int[3];
            Span<int> trackStart = stackalloc int[3];
            Span<int> trackCount = stackalloc int[3];
            artistCount.Clear();
            trackCount.Clear();
            bool sawData = false;

            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                int data = Fields(ref r);
                if (data < 0) continue;
                sawData = true;
                while (Next(ref r, data))
                {
                    if (r.ValueTextEquals("me"u8))
                    {
                        for (int me = Fields(ref r); Next(ref r, me);)
                        {
                            if (r.ValueTextEquals("profile"u8))
                            {
                                for (int p = Fields(ref r); Next(ref r, p);)
                                {
                                    if (r.ValueTextEquals("topArtists"u8)) HomeFeedFold.TopList(ref r, s, false, out artistStart[0], out artistCount[0]);
                                    else if (r.ValueTextEquals("topTracks"u8)) HomeFeedFold.TopList(ref r, s, true, out trackStart[0], out trackCount[0]);
                                    else SkipValue(ref r);
                                }
                            }
                            else if (r.ValueTextEquals("topArtists"u8)) HomeFeedFold.TopList(ref r, s, false, out artistStart[1], out artistCount[1]);
                            else if (r.ValueTextEquals("topTracks"u8)) HomeFeedFold.TopList(ref r, s, true, out trackStart[1], out trackCount[1]);
                            else SkipValue(ref r);
                        }
                    }
                    else if (r.ValueTextEquals("topArtists"u8)) HomeFeedFold.TopList(ref r, s, false, out artistStart[2], out artistCount[2]);
                    else if (r.ValueTextEquals("topTracks"u8)) HomeFeedFold.TopList(ref r, s, true, out trackStart[2], out trackCount[2]);
                    else SkipValue(ref r);
                }
            }
            if (!sawData) return;

            var parent = new StagedId(s.AddText(meUri));
            HomeFeedFold.TopRun(s, in parent, tracks: false, artistStart, artistCount);
            HomeFeedFold.TopRun(s, in parent, tracks: true, trackStart, trackCount);
        }

        /// <summary>The fold's private half. A nested class so its helper names cannot collide with the dozen other
        /// <c>Decode</c> partials; it reaches the reader idiom and the shared helpers through the enclosing class.</summary>
        static class HomeFeedFold
        {
            // ── the document ─────────────────────────────────────────────────────────────────────────────────────────

            internal static void Feed(ref Utf8JsonReader r, ReadOnlySpan<byte> subjectUri, Staging s)
            {
                int depth = Fields(ref r);
                if (depth < 0) return;

                var subject = new StagedId(s.AddText(subjectUri));
                ref var feed = ref s.Homes.RowFor(subject, Authority.Full, (uint)HomeFields.Sections);
                ReadOnlySpan<byte> facetPrefix = "wavee:home:"u8;
                if (subjectUri.Length > facetPrefix.Length && subjectUri.StartsWith(facetPrefix))
                    feed.Facet = s.AddText(subjectUri[facetPrefix.Length..]);
                feed.ChipStart = s.Chips.Count;
                int mark = s.Edges.PendingMark;

                while (Next(ref r, depth))
                {
                    if (r.ValueTextEquals("greeting"u8))
                    {
                        r.Read();
                        feed.Greeting = Label(ref r, s);
                        feed.Known |= (uint)HomeFields.Greeting;
                    }
                    else if (r.ValueTextEquals("homeChips"u8))
                    {
                        Chips(ref r, s, feed.ChipStart);
                        feed.Known |= (uint)HomeFields.Chips;
                    }
                    else if (r.ValueTextEquals("sectionContainer"u8))
                    {
                        for (int c = Fields(ref r); Next(ref r, c);)
                        {
                            if (!r.ValueTextEquals("sections"u8)) { SkipValue(ref r); continue; }
                            for (int list = Fields(ref r); Next(ref r, list);)
                            {
                                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                                for (int items = r.CurrentDepth; Element(ref r, items);)
                                {
                                    var uri = Band(ref r, s);
                                    if (!uri.IsEmpty) s.Edges.Push().Target = uri;
                                }
                            }
                        }
                    }
                    else SkipValue(ref r);
                }

                feed.ChipCount = s.Chips.Count - feed.ChipStart;
                s.Edges.Close(Relation.HomeSection, in subject, mark);
            }

            /// <summary>One band (the reader on its object). The typename is read first off a COPY of the reader, because
            /// the recently-played band's items mean something different and a cursor cannot look ahead.</summary>
            static StagedId Band(ref Utf8JsonReader r, Staging s)
            {
                byte kind = BandKind(r);
                bool recents = kind == (byte)SectionKind.HomeRecentlyPlayed;
                int factStart = s.CardFacts.Count, seedMark = s.CardSeeds.Count, cards = s.Edges.PendingMark;

                ref var row = ref s.Sections.RowFor(default, Authority.Full, (uint)SectionFields.Identity);
                row.NextOffset = SectionPaging.NoCursor;
                row.Kind = kind;
                row.HasFacts = true;
                row.FactStart = factStart;
                int wrapperTotal = 0, listTotal = -1;
                bool listSeen = false;

                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (r.ValueTextEquals("uri"u8)) { r.Read(); if (row.Id.IsEmpty) row.Id = JsonId(ref r, s); }
                    else if (r.ValueTextEquals("data"u8))
                    {
                        for (int d = Fields(ref r); Next(ref r, d);)
                        {
                            if (r.ValueTextEquals("title"u8)) { r.Read(); row.Title = Label(ref r, s); }
                            else if (r.ValueTextEquals("subtitle"u8)) { r.Read(); row.Subtitle = Label(ref r, s); }
                            else SkipValue(ref r);
                        }
                    }
                    else if (r.ValueTextEquals("sectionItems"u8))
                    {
                        for (int d = Fields(ref r); Next(ref r, d);)
                        {
                            if (r.ValueTextEquals("totalCount"u8)) { r.Read(); wrapperTotal = (int)Num(ref r); }
                            // `nextOffset: null` and an absent cursor both read NoCursor (B1's rule, ch 10 §7): only a
                            // number is a cursor, and `0` on a complete band stays a number.
                            else if (r.ValueTextEquals("pagingInfo"u8))
                                row.NextOffset = (int)OneNumber(ref r, "nextOffset"u8, SectionPaging.NoCursor);
                            else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                            {
                                for (int list = r.CurrentDepth; Element(ref r, list);)
                                    Item(ref r, s, ref row, recents, ref listSeen, ref listTotal);
                            }
                            else SkipValue(ref r);
                        }
                    }
                    else SkipValue(ref r);
                }

                // The recents band's wrapper counts its ONE List child; the real total is the wrapped list's own
                // (0.2.9 `RecentsTotal`).
                row.Total = listSeen ? Math.Max(0, listTotal) : wrapperTotal;
                row.FactCount = s.CardFacts.Count - factStart;
                var id = row.Id;
                if (IsChartUri(s, in id)) row.Flags |= (byte)SectionFlags.Chart;
                if (!s.Sections.Settle())
                {
                    s.Edges.Pop(cards);
                    s.CardFacts.Rewind(factStart);
                    s.CardSeeds.Rewind(seedMark);
                    return default;
                }
                s.Edges.Close(Relation.SectionCards, in id, cards);
                return id;
            }

            /// <summary><c>data.__typename</c> of the band the (copied) reader is on → the <see cref="SectionKind"/>.</summary>
            static byte BandKind(Utf8JsonReader r)
            {
                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                    for (int d = Fields(ref r); Next(ref r, d);)
                    {
                        if (!r.ValueTextEquals("__typename"u8)) { SkipValue(ref r); continue; }
                        r.Read();
                        return Says(ref r, "HomeSpotlightSectionData") ? (byte)SectionKind.HomeSpotlight
                             : Says(ref r, "HomeRecentlyPlayedSectionData") ? (byte)SectionKind.HomeRecentlyPlayed
                             : Says(ref r, "HomeFeedBaselineSectionData") ? (byte)SectionKind.HomeBaseline
                             : Says(ref r, "HomeShortsSectionData") ? (byte)SectionKind.HomeShorts
                             : (byte)SectionKind.HomeGeneric;
                    }
                    return (byte)SectionKind.HomeGeneric;
                }
                return (byte)SectionKind.HomeGeneric;
            }

            /// <summary>One of <see cref="ChartSections.All"/>? An exact byte compare (base62 ids are case-sensitive).</summary>
            static bool IsChartUri(Staging s, in StagedId id)
            {
                if (id.Text.IsEmpty) return false;
                var utf8 = s.Utf8(id.Text);
                var all = ChartSections.All;
                for (int i = 0; i < all.Count; i++)
                {
                    string c = all[i];
                    if (utf8.Length != c.Length) continue;
                    int k = 0;
                    while (k < c.Length && utf8[k] == c[k]) k++;
                    if (k == c.Length) return true;
                }
                return false;
            }

            // ── one item ─────────────────────────────────────────────────────────────────────────────────────────────

            const int Unsupported = 0, Card = 1, Duplicate = 2;

            static void Item(ref Utf8JsonReader r, Staging s, ref StagedSection row, bool recents, ref bool listSeen, ref int listTotal)
            {
                int outcome = Unsupported;
                bool list = false;
                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (!r.ValueTextEquals("content"u8)) { SkipValue(ref r); continue; }
                    for (int c = Fields(ref r); Next(ref r, c);)
                    {
                        if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                        if (recents && !listSeen)
                        {
                            r.Read();
                            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                            listSeen = list = true;
                            listTotal = 0;
                            RecentsList(ref r, s, ref row, ref listTotal);
                        }
                        else outcome = CardOf(ref r, s, ref row);
                    }
                }
                if (list) return;                         // the wrapper is not an item; its members were counted
                row.Raw++;
                if (outcome == Card) row.Cards++;
                else if (outcome == Duplicate) row.Duplicates++;
                else row.Unsupported++;
            }

            /// <summary>A card's scratch: B1's <see cref="Node"/> for the entity row, plus what only a home card carries.</summary>
            struct CardNode
            {
                public Node Node;
                public byte Type;
                public TextRef ProfileName, OwnerName, ShowName, Publisher, Author, Signifier;
                public TextRef Pretitle, Terms, HeaderImage;
                public TextRef ImgImages, ImgCover, ImgAvatar, ImgVisual;
                public uint AccImages, AccCover, AccAvatar, AccVisual;
                public long ExpiresMs, CreatedMs;
                public int ResumeMs, BookMs;
                public ushort Rating;
                public bool Video;
                public StagedId ShowUri;
            }

            const byte TAlbum = 1, TPlaylist = 2, TArtist = 3, TEpisode = 4, TAudiobook = 5, TPodcast = 6;

            /// <summary><c>content.data</c> → a staged entity row + one card fact + the card edge (0.2.9
            /// <c>CardFromEntity</c>). The reader is on the <c>data</c> property.</summary>
            static int CardOf(ref Utf8JsonReader r, Staging s, ref StagedSection row)
            {
                var c = default(CardNode);
                int credit = s.CreditMark, mark = s.Edges.PendingMark, artistMark = s.Artists.Count;
                for (int d = Fields(ref r); Next(ref r, d);) CardProperty(ref r, s, ref c, credit);

                if (c.Node.ArtistLine.IsEmpty) c.Node.ArtistLine = s.TakeCredit(credit);
                else s.TakeCredit(credit);
                TextRef firstArtist = s.Artists.Count > artistMark ? s.Artists[artistMark].Name : default;
                // An album's artists sit on the SAME pending stack the band's cards do: close (or drop) them before this
                // card's own edge goes on, or the band's run is not contiguous.
                if (s.Edges.Pending(mark) > 0)
                {
                    if (c.Type == TAlbum && !c.Node.Uri.IsEmpty) CloseArtists(s, in c.Node, mark);
                    else s.Edges.Pop(mark);
                }
                if (c.Type == 0 || c.Node.Uri.IsEmpty) return Unsupported;

                var n = c.Node;
                bool liked = false;
                switch (c.Type)
                {
                    case TAlbum:
                    case TEpisode:
                    case TAudiobook:
                    case TPodcast:
                        n.Image = First(c.ImgCover, c.ImgVisual, c.ImgAvatar, c.ImgImages);
                        if (c.Type == TEpisode) n.ShowUri = c.ShowUri;
                        break;
                    case TPlaylist:
                        n.Image = First(c.ImgImages, c.ImgVisual, c.ImgAvatar, c.ImgCover);
                        n.Accent = Accent(in c);
                        if (!n.Uri.Packed.IsEmpty || !IsLikedUri(s.Utf8(n.Uri.Text))) break;
                        // Liked Songs on the wire is a Playlist node: the canonical uri, and NO image (the stock heart is
                        // the one cover the app replaces with the user's own treatment).
                        liked = true;
                        n.Uri = new StagedId(s.AddText(LikedUri));
                        n.Image = default;
                        break;
                    case TArtist:
                        n.Name = c.ProfileName.IsEmpty ? n.Name : c.ProfileName;
                        n.Image = First(c.ImgAvatar, c.ImgImages, c.ImgVisual, c.ImgCover);
                        break;
                }

                if (Holds(s, in row, in n.Uri)) return Duplicate;
                var staged = Stage(s, in n, Authority.Thin);
                if (staged.IsEmpty) return Unsupported;
                // A podcast's publisher is a column of the show's Identity group that Stage has no Node field for.
                if (c.Type == TPodcast && !c.Publisher.IsEmpty && s.Shows.Count > 0 && staged.Kind(s) == EntityKind.Show)
                {
                    ref var show = ref s.Shows[s.Shows.Count - 1];
                    show.Publisher = c.Publisher;
                    show.Known |= (uint)ShowFields.Publisher;
                }

                // The playlist extras Stage has no Node field for (the Format group's header image + generic title, the
                // daylist window). Stage appended exactly one playlist row, so it is the last one.
                bool daylist = c.Type == TPlaylist && !n.Format.IsEmpty && StartsAscii(s.Utf8(n.Format), "daylist");
                if (c.Type == TPlaylist && s.Playlists.Count > 0 && staged.Kind(s) is EntityKind.Playlist or EntityKind.Collection)
                {
                    ref var p = ref s.Playlists[s.Playlists.Count - 1];
                    if (daylist)
                    {
                        p.GenericTitle = c.Pretitle;
                        p.HeaderImage = c.HeaderImage;
                        if (c.ExpiresMs > 0 || c.CreatedMs > 0)
                        {
                            p.Known |= (uint)PlaylistFields.Daylist;
                            p.DaylistExpiresAt = (int)(c.ExpiresMs / 1000);
                            p.DaylistCreatedAt = (int)(c.CreatedMs / 1000);
                        }
                    }
                }
                if (!c.OwnerName.IsEmpty && !n.OwnerUri.IsEmpty)
                {
                    ref var owner = ref s.Users.RowFor(n.OwnerUri, Authority.Thin, (uint)UserFields.Identity);
                    owner.Name = c.OwnerName;
                }
                if (c.Type == TEpisode && !c.ShowUri.IsEmpty && !c.ShowName.IsEmpty)
                {
                    ref var show = ref s.Shows.RowFor(c.ShowUri, Authority.Thin, (uint)ShowFields.Title);
                    show.Title = c.ShowName;
                }

                ref var f = ref s.CardFacts.Add();
                f.Target = staged;
                f.Accent = Accent(in c);
                f.SeedStart = -1;
                switch (c.Type)
                {
                    case TAlbum:
                        f.Subtitle = firstArtist;
                        break;
                    case TPlaylist:
                        {
                            var description = n.Description.IsEmpty ? default : Unescaped(s, n.Description);
                            f.Subtitle = description.IsEmpty ? c.OwnerName : description;
                            if (!liked && !n.Format.IsEmpty) f.Format = n.Format;
                            int seedStart = s.CardSeeds.Count, seeds = 0;
                            if (daylist) seeds = SplitTerms(s, c.Terms);
                            if (seeds == 0 && ListsSeeds(s.Utf8(n.Format)) && !description.IsEmpty) seeds = ParseSeeds(s, description);
                            if (seeds > 0) { f.SeedStart = seedStart; f.SeedCount = seeds; }
                            break;
                        }
                    case TEpisode:
                        f.Subtitle = c.ShowName;
                        f.DurationMs = n.DurationMs;
                        f.ResumeMs = c.ResumeMs;
                        if (c.Video) f.Flags |= (byte)HomeCardFlags.HasVideo;
                        break;
                    case TAudiobook:
                        f.Subtitle = c.Author;
                        f.Author = c.Author;
                        f.Signifier = c.Signifier;
                        f.DurationMs = c.BookMs;
                        f.Rating = c.Rating;
                        f.Flags |= (byte)HomeCardFlags.Audiobook;
                        break;
                    case TPodcast:
                        f.Subtitle = c.Publisher;
                        break;
                }
                s.Edges.Push().Target = staged;
                return Card;
            }

            static void CardProperty(ref Utf8JsonReader r, Staging s, ref CardNode c, int credit)
            {
                if (r.ValueTextEquals("__typename"u8)) { r.Read(); c.Type = TypeOf(ref r); }
                else if (r.ValueTextEquals("uri"u8)) { r.Read(); if (c.Node.Uri.IsEmpty) c.Node.Uri = JsonId(ref r, s); }
                else if (r.ValueTextEquals("name"u8)) { r.Read(); if (c.Node.Name.IsEmpty) c.Node.Name = s.AddJson(ref r); }
                else if (r.ValueTextEquals("profile"u8)) c.ProfileName = OneText(ref r, s, "name"u8);
                else if (r.ValueTextEquals("description"u8)) { r.Read(); c.Node.Description = s.AddJson(ref r); }
                else if (r.ValueTextEquals("format"u8)) { r.Read(); c.Node.Format = s.AddJson(ref r); }
                else if (r.ValueTextEquals("images"u8)) Images(ref r, s, ref c.ImgImages, ref c.AccImages);
                else if (r.ValueTextEquals("coverArt"u8)) Picture(ref r, s, ref c.ImgCover, ref c.AccCover);
                else if (r.ValueTextEquals("visuals"u8))
                {
                    for (int d = Fields(ref r); Next(ref r, d);)
                    {
                        if (r.ValueTextEquals("avatarImage"u8)) Picture(ref r, s, ref c.ImgAvatar, ref c.AccAvatar);
                        else SkipValue(ref r);
                    }
                }
                else if (r.ValueTextEquals("visualIdentity"u8) || r.ValueTextEquals("visualIdentityTrait"u8))
                {
                    for (int d = Fields(ref r); Next(ref r, d);)
                    {
                        if (r.ValueTextEquals("squareCoverImage"u8)) Picture(ref r, s, ref c.ImgVisual, ref c.AccVisual);
                        else SkipValue(ref r);
                    }
                }
                else if (r.ValueTextEquals("attributes"u8)) Attributes(ref r, s, ref c);
                else if (r.ValueTextEquals("ownerV2"u8)) Owner(ref r, s, ref c.Node.OwnerUri, ref c.OwnerName);
                else if (r.ValueTextEquals("podcastV2"u8)) Owner(ref r, s, ref c.ShowUri, ref c.ShowName);
                else if (r.ValueTextEquals("publisher"u8)) c.Publisher = OneText(ref r, s, "name"u8);
                else if (r.ValueTextEquals("authorsV2"u8)) c.Author = FirstAuthor(ref r, s);
                else if (r.ValueTextEquals("rating"u8)) c.Rating = Rating(ref r);
                else if (r.ValueTextEquals("accessInfo"u8))
                {
                    for (int d = Fields(ref r); Next(ref r, d);)
                    {
                        if (r.ValueTextEquals("signifier"u8)) c.Signifier = OneText(ref r, s, "text"u8);
                        else SkipValue(ref r);
                    }
                }
                else if (r.ValueTextEquals("audiobookDuration"u8)) c.BookMs = (int)OneNumber(ref r, "totalMilliseconds"u8);
                else if (r.ValueTextEquals("duration"u8)) c.Node.DurationMs = (int)OneNumber(ref r, "totalMilliseconds"u8);
                else if (r.ValueTextEquals("playedState"u8)) c.ResumeMs = (int)OneNumber(ref r, "playPositionMilliseconds"u8);
                else if (r.ValueTextEquals("mediaTypes"u8)) c.Video = HasVideo(ref r);
                else if (r.ValueTextEquals("content"u8)) c.Node.TrackCount = (int)OneNumber(ref r, "totalCount"u8);
                else if (r.ValueTextEquals("albumType"u8)) { r.Read(); c.Node.AlbumKind = AlbumKindOf(ref r); }
                // The shared vocabulary for what a card has in common with every other node: the album's artist run and
                // credit line, its release date, its rating, its playability, its share url.
                else if (r.ValueTextEquals("artists"u8) || r.ValueTextEquals("date"u8) || r.ValueTextEquals("releaseDate"u8)
                      || r.ValueTextEquals("contentRating"u8) || r.ValueTextEquals("playability"u8) || r.ValueTextEquals("sharingInfo"u8))
                    NodeProperty(ref r, s, ref c.Node, credit);
                else SkipValue(ref r);
            }

            static byte TypeOf(ref Utf8JsonReader r)
                => Says(ref r, "Album") ? TAlbum
                 : Says(ref r, "Playlist") ? TPlaylist
                 : Says(ref r, "Artist") ? TArtist
                 : Says(ref r, "Episode") ? TEpisode
                 : Says(ref r, "Audiobook") ? TAudiobook
                 : Says(ref r, "Podcast") ? TPodcast
                 : (byte)0;

            static byte AlbumKindOf(ref Utf8JsonReader r)
                => Says(ref r, "SINGLE") ? (byte)Wavee.AlbumKind.Single
                 : Says(ref r, "EP") ? (byte)Wavee.AlbumKind.EP
                 : Says(ref r, "COMPILATION") ? (byte)Wavee.AlbumKind.Compilation
                 : (byte)Wavee.AlbumKind.Album;

            /// <summary>0.2.9 <c>AccentArgb</c>'s order: the first image's colours, the cover's, the avatar's, the visual
            /// identity's.</summary>
            static uint Accent(in CardNode c)
                => c.AccImages != 0 ? c.AccImages : c.AccCover != 0 ? c.AccCover : c.AccAvatar != 0 ? c.AccAvatar : c.AccVisual;

            static TextRef First(TextRef a, TextRef b, TextRef c, TextRef d)
                => !a.IsEmpty ? a : !b.IsEmpty ? b : !c.IsEmpty ? c : d;

            /// <summary>An image node: <c>{ extractedColors, sources }</c>, <c>{ image: { data: { sources } } }</c>, or the
            /// element of an <c>items[]</c> — the first url and the non-fallback dark colour.</summary>
            static void Picture(ref Utf8JsonReader r, Staging s, ref TextRef url, ref uint accent)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("extractedColors"u8)) { uint a = ColorDark(ref r); if (accent == 0) accent = a; }
                    else if (r.ValueTextEquals("sources"u8)) { r.Read(); var u = FirstUrl(ref r, s); if (url.IsEmpty) url = u; }
                    else if (r.ValueTextEquals("image"u8) || r.ValueTextEquals("data"u8)) Picture(ref r, s, ref url, ref accent);
                    else SkipValue(ref r);
                }
            }

            /// <summary><c>images: { items: [ {…}, … ] }</c> — the FIRST image only (0.2.9 <c>ImagesCover</c> / the accent).</summary>
            static void Images(ref Utf8JsonReader r, Staging s, ref TextRef url, ref uint accent)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    bool first = true;
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        if (!first) { r.Skip(); continue; }
                        first = false;
                        Picture(ref r, s, ref url, ref accent);
                    }
                }
            }

            /// <summary><c>extractedColors: { colorDark: { hex, isFallback } }</c> → ARGB, 0 for none or a fallback.</summary>
            static uint ColorDark(ref Utf8JsonReader r)
            {
                uint colour = 0;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("colorDark"u8)) { SkipValue(ref r); continue; }
                    uint hex = 0;
                    bool fallback = false;
                    for (int c = Fields(ref r); Next(ref r, c);)
                    {
                        if (r.ValueTextEquals("hex"u8))
                        {
                            r.Read();
                            if (r.TokenType == JsonTokenType.String && !r.HasValueSequence) hex = Argb(r.ValueSpan);
                        }
                        else if (r.ValueTextEquals("isFallback"u8)) { r.Read(); fallback = Flag(ref r); }
                        else SkipValue(ref r);
                    }
                    colour = fallback ? 0 : hex;
                }
                return colour;
            }

            /// <summary><c>{ data: { name, uri } }</c> — a playlist's owner, an episode's show.</summary>
            static void Owner(ref Utf8JsonReader r, Staging s, ref StagedId uri, ref TextRef name)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                    for (int o = Fields(ref r); Next(ref r, o);)
                    {
                        if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("uri"u8)) { r.Read(); uri = JsonId(ref r, s); }
                        else SkipValue(ref r);
                    }
                }
            }

            /// <summary><c>authorsV2</c> as an array of <c>{ name }</c> or <c>{ items: [...] }</c> → the first name.</summary>
            static TextRef FirstAuthor(ref Utf8JsonReader r, Staging s)
            {
                if (r.TokenType == JsonTokenType.PropertyName && !r.Read()) return default;
                TextRef name = default;
                if (r.TokenType == JsonTokenType.StartObject)
                {
                    for (int d = r.CurrentDepth; Next(ref r, d);)
                    {
                        if (r.ValueTextEquals("items"u8) && EnterArray(ref r)) name = FirstNameIn(ref r, s);
                        else SkipValue(ref r);
                    }
                    return name;
                }
                if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return default; }
                return FirstNameIn(ref r, s);

                static TextRef FirstNameIn(ref Utf8JsonReader r, Staging s)
                {
                    TextRef first = default;
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        if (!first.IsEmpty) { r.Skip(); continue; }
                        for (int d = r.CurrentDepth; Next(ref r, d);)
                        {
                            if (r.ValueTextEquals("name"u8)) { r.Read(); first = s.AddJson(ref r); }
                            else SkipValue(ref r);
                        }
                    }
                    return first;
                }
            }

            /// <summary><c>rating: { averageRating: { average, showAverage } }</c> → average × 100, 0 unless shown.</summary>
            static ushort Rating(ref Utf8JsonReader r)
            {
                double average = 0;
                bool show = false;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("averageRating"u8)) { SkipValue(ref r); continue; }
                    for (int a = Fields(ref r); Next(ref r, a);)
                    {
                        if (r.ValueTextEquals("average"u8)) { r.Read(); if (r.TokenType == JsonTokenType.Number) average = r.GetDouble(); }
                        else if (r.ValueTextEquals("showAverage"u8)) { r.Read(); show = Flag(ref r); }
                        else SkipValue(ref r);
                    }
                }
                return show && average > 0 ? (ushort)Math.Clamp(Math.Round(average * 100d), 0d, ushort.MaxValue) : (ushort)0;
            }

            static bool HasVideo(ref Utf8JsonReader r)
            {
                if (!EnterArray(ref r)) return false;
                bool video = false;
                for (int depth = r.CurrentDepth; r.Read() && !End(ref r, depth);)
                    if (Says(ref r, "VIDEO")) video = true;
                return video;
            }

            /// <summary><c>attributes: [ { key, value } ]</c> — the five keys a home playlist card reads; every other
            /// value's text is handed straight back to the arena.</summary>
            static void Attributes(ref Utf8JsonReader r, Staging s, ref CardNode c)
            {
                if (!EnterArray(ref r)) return;
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    byte key = 0;
                    bool keyRead = false;
                    TextRef value = default;
                    for (int d = r.CurrentDepth; Next(ref r, d);)
                    {
                        if (r.ValueTextEquals("key"u8)) { r.Read(); key = AttributeKey(ref r); keyRead = true; }
                        else if (r.ValueTextEquals("value"u8))
                        {
                            r.Read();
                            if ((keyRead && key == 0) || r.TokenType != JsonTokenType.String) { r.Skip(); continue; }
                            value = s.AddJson(ref r);
                        }
                        else SkipValue(ref r);
                    }
                    switch (key)
                    {
                        case 1: c.Pretitle = value; break;
                        case 2: c.Terms = value; break;
                        case 5: c.HeaderImage = value; break;
                        case 3:
                        case 4:
                            {
                                long ms = value.IsEmpty ? 0 : IsoInstantMs(s.Utf8(value));
                                if (key == 3) c.ExpiresMs = ms; else c.CreatedMs = ms;
                                if (!value.IsEmpty) s.RewindText(value.Offset);
                                break;
                            }
                        default:
                            if (!value.IsEmpty) s.RewindText(value.Offset);
                            break;
                    }
                }
            }

            static byte AttributeKey(ref Utf8JsonReader r)
                => r.ValueTextEquals("daylist_pretitle"u8) ? (byte)1
                 : r.ValueTextEquals("localized_terms"u8) ? (byte)2
                 : r.ValueTextEquals("expires"u8) ? (byte)3
                 : r.ValueTextEquals("created"u8) ? (byte)4
                 : r.ValueTextEquals("header_image_url_desktop"u8) ? (byte)5
                 : (byte)0;

            /// <summary>Does the band already hold this card? Duplicates stay on the ledger and off the edge.</summary>
            static bool Holds(Staging s, in StagedSection row, in StagedId id)
            {
                var facts = s.CardFacts;
                for (int i = row.FactStart; i < facts.Count; i++)
                    if (Same(s, in facts[i].Target, in id)) return true;
                return false;
            }

            static bool Same(Staging s, in StagedId a, in StagedId b)
            {
                if (!a.Packed.IsEmpty || !b.Packed.IsEmpty) return a.Packed.Equals(b.Packed);
                return s.Utf8(a.Text).SequenceEqual(s.Utf8(b.Text));
            }

            static ReadOnlySpan<byte> LikedUri => "spotify:collection:tracks"u8;

            /// <summary><see cref="EntityUri.IsLikedCollection(ReadOnlySpan{char})"/> over UTF-8.</summary>
            static bool IsLikedUri(ReadOnlySpan<byte> u)
            {
                if (u.SequenceEqual(LikedUri)) return true;
                if (EntityUri.KindOf(u) != EntityKind.Collection) return false;
                int at = u.LastIndexOf(":collection"u8);
                if (at < 0) return false;
                var tail = u[(at + ":collection"u8.Length)..];
                return tail.Length == 0 || tail.SequenceEqual(":tracks"u8);
            }

            // ── the recently-played band (0.2.9 `RecentCardsFromListData` / `CardFromRecentEntity`) ───────────────────

            /// <summary>The band's one <c>List</c> wrapper (the reader on its data object): <c>items.totalCount</c> is the
            /// band's total, and every <c>items.items[]</c> entry is a raw item of the band.</summary>
            static void RecentsList(ref Utf8JsonReader r, Staging s, ref StagedSection row, ref int listTotal)
            {
                for (int d = r.CurrentDepth; Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("items"u8)) { SkipValue(ref r); continue; }
                    for (int o = Fields(ref r); Next(ref r, o);)
                    {
                        if (r.ValueTextEquals("totalCount"u8)) { r.Read(); listTotal = (int)Num(ref r); }
                        else if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                        {
                            for (int list = r.CurrentDepth; Element(ref r, list);)
                            {
                                row.Raw++;
                                int outcome = RecentItem(ref r, s, ref row);
                                if (outcome == Card) row.Cards++;
                                else if (outcome == Duplicate) row.Duplicates++;
                                else row.Unsupported++;
                            }
                        }
                        else SkipValue(ref r);
                    }
                }
            }

            struct RecentNode
            {
                public StagedId Uri, WrapperUri;
                public TextRef Name, Type, Image, FirstContributor, Credit;
                public int Contributors;
                public byte EntityType;      // 1 track, 2 artist, 3 album, 4 playlist
            }

            static int RecentItem(ref Utf8JsonReader r, Staging s, ref StagedSection row)
            {
                var rc = default(RecentNode);
                int credit = s.CreditMark;
                for (int d = r.CurrentDepth; Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("entity"u8)) { SkipValue(ref r); continue; }
                    for (int e = Fields(ref r); Next(ref r, e);)
                    {
                        if (r.ValueTextEquals("_uri"u8)) { r.Read(); if (rc.WrapperUri.IsEmpty) rc.WrapperUri = JsonId(ref r, s); }
                        else if (r.ValueTextEquals("data"u8)) RecentData(ref r, s, ref rc, credit);
                        else SkipValue(ref r);
                    }
                }
                rc.Credit = s.TakeCredit(credit);

                var uri = rc.Uri.IsEmpty ? rc.WrapperUri : rc.Uri;
                if (uri.IsEmpty || rc.Name.IsEmpty) return Unsupported;
                var kind = uri.Kind(s);
                var n = default(Node);
                n.Name = rc.Name;
                n.Image = rc.Image;
                TextRef subtitle;
                if (uri.Packed.IsEmpty && IsLikedUri(s.Utf8(uri.Text)))
                {
                    n.Uri = new StagedId(s.AddText(LikedUri));
                    n.Image = default;
                    subtitle = rc.FirstContributor;
                }
                else if (rc.EntityType == 1 || kind == EntityKind.Track)
                {
                    n.Uri = uri;
                    n.ArtistLine = rc.Credit;
                    subtitle = Compose(s, "Song"u8, rc.Credit, rc.Contributors);
                }
                else if (rc.EntityType == 2 || kind == EntityKind.Artist)
                {
                    n.Uri = uri;
                    subtitle = default;                       // HomeCard.Subtitle answers "Artist" for every artist card
                }
                else if (rc.EntityType == 3 || kind == EntityKind.Album)
                {
                    n.Uri = uri;
                    n.AlbumKind = RecentAlbumKind(s.Utf8(rc.Type));
                    subtitle = ComposeAlbum(s, rc.Type, rc.Credit, rc.Contributors);
                }
                else if (rc.EntityType == 4 || kind == EntityKind.Playlist)
                {
                    n.Uri = uri;
                    subtitle = rc.FirstContributor.IsEmpty ? s.AddText("Playlist"u8) : rc.FirstContributor;
                }
                else return Unsupported;

                if (Holds(s, in row, in n.Uri)) return Duplicate;
                var staged = Stage(s, in n, Authority.Seed);
                if (staged.IsEmpty) return Unsupported;
                ref var f = ref s.CardFacts.Add();
                f.Target = staged;
                f.Subtitle = subtitle;
                f.SeedStart = -1;
                s.Edges.Push().Target = staged;
                return Card;
            }

            static void RecentData(ref Utf8JsonReader r, Staging s, ref RecentNode rc, int credit)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("uri"u8)) { r.Read(); if (rc.Uri.IsEmpty) rc.Uri = JsonId(ref r, s); }
                    else if (r.ValueTextEquals("entityTypeTrait"u8))
                    {
                        for (int t = Fields(ref r); Next(ref r, t);)
                        {
                            if (!r.ValueTextEquals("type"u8)) { SkipValue(ref r); continue; }
                            r.Read();
                            rc.EntityType = Says(ref r, "ENTITY_TYPE_TRACK") ? (byte)1
                                          : Says(ref r, "ENTITY_TYPE_ARTIST") ? (byte)2
                                          : Says(ref r, "ENTITY_TYPE_ALBUM") ? (byte)3
                                          : Says(ref r, "ENTITY_TYPE_PLAYLIST") ? (byte)4
                                          : (byte)0;
                        }
                    }
                    else if (r.ValueTextEquals("identityTrait"u8))
                    {
                        for (int t = Fields(ref r); Next(ref r, t);)
                        {
                            if (r.ValueTextEquals("name"u8)) { r.Read(); rc.Name = s.AddJson(ref r); }
                            else if (r.ValueTextEquals("type"u8)) { r.Read(); rc.Type = s.AddJson(ref r); }
                            else if (r.ValueTextEquals("contributors"u8)) Contributors(ref r, s, ref rc, credit);
                            else SkipValue(ref r);
                        }
                    }
                    else if (r.ValueTextEquals("visualIdentityTrait"u8))
                    {
                        uint ignored = 0;
                        for (int t = Fields(ref r); Next(ref r, t);)
                        {
                            if (r.ValueTextEquals("squareCoverImage"u8) || r.ValueTextEquals("image"u8)) Picture(ref r, s, ref rc.Image, ref ignored);
                            else SkipValue(ref r);
                        }
                    }
                    else SkipValue(ref r);
                }
            }

            /// <summary><c>contributors.items[] { name }</c>: the first name kept whole, the first three joined into the
            /// credit line, the count kept for the ", ..." tail.</summary>
            static void Contributors(ref Utf8JsonReader r, Staging s, ref RecentNode rc, int credit)
            {
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        for (int e = r.CurrentDepth; Next(ref r, e);)
                        {
                            if (!r.ValueTextEquals("name"u8)) { SkipValue(ref r); continue; }
                            r.Read();
                            var name = s.AddJson(ref r);
                            if (name.IsEmpty) continue;
                            if (rc.Contributors < 3) s.AppendCredit(s.Utf8(name), credit);
                            if (rc.Contributors == 0) rc.FirstContributor = name;
                            else s.RewindText(name.Offset);
                            rc.Contributors++;
                        }
                    }
                }
            }

            static byte RecentAlbumKind(ReadOnlySpan<byte> type)
                => Is(type, "single") ? (byte)Wavee.AlbumKind.Single
                 : Is(type, "ep") ? (byte)Wavee.AlbumKind.EP
                 : Is(type, "compilation") ? (byte)Wavee.AlbumKind.Compilation
                 : (byte)Wavee.AlbumKind.Album;

            /// <summary>0.2.9 <c>JoinNames(prefix, artists)</c>: "prefix - A, B, C" (", ..." past three), or the prefix alone.</summary>
            static TextRef Compose(Staging s, ReadOnlySpan<byte> prefix, TextRef credit, int contributors)
            {
                if (contributors == 0 || credit.IsEmpty) return s.AddText(prefix);
                Span<byte> buffer = stackalloc byte[512];
                int w = Append(buffer, 0, prefix);
                w = Append(buffer, w, " - "u8);
                w = Append(buffer, w, s.Utf8(credit));
                if (contributors > 3) w = Append(buffer, w, ", ..."u8);
                return s.AddText(buffer[..w]);
            }

            /// <summary>The album arm: the identity type title-cased with underscores as spaces ("Single"), else "Album".</summary>
            static TextRef ComposeAlbum(Staging s, TextRef type, TextRef credit, int contributors)
            {
                Span<byte> prefix = stackalloc byte[64];
                var raw = type.IsEmpty ? "Album"u8 : s.Utf8(type);
                int n = Math.Min(raw.Length, prefix.Length);
                for (int i = 0; i < n; i++)
                {
                    byte b = raw[i] == (byte)'_' ? (byte)' ' : raw[i];
                    if (i == 0) { if (b is >= (byte)'a' and <= (byte)'z') b -= 32; }
                    else if (b is >= (byte)'A' and <= (byte)'Z') b += 32;
                    prefix[i] = b;
                }
                return Compose(s, prefix[..n], credit, contributors);
            }

            static int Append(Span<byte> buffer, int at, ReadOnlySpan<byte> bytes)
            {
                int n = Math.Min(bytes.Length, buffer.Length - at);
                if (n <= 0) return at;
                bytes[..n].CopyTo(buffer[at..]);
                return at + n;
            }

            // ── the seeds (0.2.9 `ListsSeeds` / `ParseSeeds` / `DropTrailingConjunction` / `SplitTerms`, over UTF-8) ──

            /// <summary>The playlist formats whose DESCRIPTION is a seed list rather than prose.</summary>
            static bool ListsSeeds(ReadOnlySpan<byte> format)
                => Is(format, "daily-mix") || Is(format, "inspiredby-mix") || Is(format, "topic-mix")
                || Is(format, "artist-mix-reader") || Is(format, "descripto") || Is(format, "daylist");

            /// <summary>A daylist's <c>localized_terms</c> comma list → seeds (slices of the value). Returns how many.</summary>
            static int SplitTerms(Staging s, TextRef csv)
            {
                if (csv.IsEmpty) return 0;
                var u = s.Utf8(csv);
                int count = 0, start = 0;
                for (int i = 0; i <= u.Length; i++)
                {
                    if (i < u.Length && u[i] != (byte)',') continue;
                    var (at, len) = Trimmed(u, start, i);
                    if (len > 0) { s.CardSeeds.Add() = new TextRef(csv.Offset + at, len); count++; }
                    start = i + 1;
                }
                return count;
            }

            /// <summary>Anchors win (their inner text is the exact display name); else the plain comma form, with a leading
            /// "With " stripped and the localized "… and more" trailer dropped — never by counting tokens. Returns how many
            /// seeds were added (slices of <paramref name="text"/>).</summary>
            static int ParseSeeds(Staging s, TextRef text)
            {
                var u = s.Utf8(text);
                int count = 0, i = 0;
                while (true)
                {
                    int a = IndexOfAscii(u, i, "<a"u8);
                    if (a < 0) break;
                    int open = u[a..].IndexOf((byte)'>');
                    if (open < 0) break;
                    open += a;
                    int close = IndexOfAscii(u, open, "</a>"u8);
                    if (close < 0) break;
                    var (at, len) = Trimmed(u, open + 1, close);
                    if (len > 0) { s.CardSeeds.Add() = new TextRef(text.Offset + at, len); count++; }
                    i = close + 4;
                }
                if (count > 0) return count;

                var (from, length) = Trimmed(u, 0, u.Length);
                if (length == 0) return 0;
                int end = from + length;
                if (u.Slice(from, length).IndexOf((byte)'<') >= 0) return 0;          // markup we did not recognise
                if (length >= 5 && Is(u.Slice(from, 5), "With ")) (from, length) = Trimmed(u, from + 5, end);
                if (length == 0 || u.Slice(from, length).IndexOf((byte)',') < 0) return 0;   // a sentence, not a list

                int segStart = from;
                for (int k = from; k <= end; k++)
                {
                    if (k < end && u[k] != (byte)',') continue;
                    var (at, len) = Trimmed(u, segStart, k);
                    if (k == end && len > 0) len = DropTrailingConjunction(u.Slice(at, len));
                    if (len > 0 && Encoding.UTF8.GetCharCount(u.Slice(at, len)) <= 64)
                    {
                        s.CardSeeds.Add() = new TextRef(text.Offset + at, len);
                        count++;
                    }
                    segStart = k + 1;
                }
                return count;
            }

            static readonly string[] s_trailingConjunctions =
                ["and", "en", "und", "et", "y", "e", "och", "og", "ja", "i", "oraz", "ve", "и", "та", "외", "등"];

            /// <summary>The kept length of the LAST segment: "KIMMUSEUM and more" → "KIMMUSEUM"; "Simon and Garfunkel",
            /// "Wind &amp; Fire" and every other real name left alone (a conjunction AND a lower-case quantity word).</summary>
            static int DropTrailingConjunction(ReadOnlySpan<byte> segment)
            {
                int last = segment.LastIndexOf((byte)' ');
                if (last <= 0) return segment.Length;
                int penult = segment[..last].LastIndexOf((byte)' ');
                if (penult < 0) return segment.Length;
                if (!IsConjunction(segment.Slice(penult + 1, last - penult - 1))) return segment.Length;
                if (!IsQuantityWord(segment[(last + 1)..])) return segment.Length;
                int keep = penult;
                while (keep > 0 && segment[keep - 1] is (byte)' ' or (byte)'\t') keep--;
                return keep;
            }

            static bool IsConjunction(ReadOnlySpan<byte> token)
            {
                if (token.IsEmpty || token.Length > 16) return false;
                Span<char> chars = stackalloc char[16];
                int n;
                try { n = Encoding.UTF8.GetChars(token, chars); }
                catch (ArgumentException) { return false; }
                ReadOnlySpan<char> word = chars[..n];
                for (int i = 0; i < s_trailingConjunctions.Length; i++)
                    if (word.Equals(s_trailingConjunctions[i], StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            /// <summary>"more" / "meer" / "더보기" → true; "Garfunkel" (an upper-case letter) and punctuation → false.</summary>
            static bool IsQuantityWord(ReadOnlySpan<byte> token)
            {
                bool letter = false;
                while (!token.IsEmpty)
                {
                    if (Rune.DecodeFromUtf8(token, out var rune, out int consumed) != System.Buffers.OperationStatus.Done) return false;
                    if (Rune.IsUpper(rune)) return false;
                    letter |= Rune.IsLetter(rune);
                    token = token[consumed..];
                }
                return letter;
            }

            static (int At, int Length) Trimmed(ReadOnlySpan<byte> u, int from, int to)
            {
                while (from < to && u[from] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') from++;
                while (to > from && u[to - 1] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') to--;
                return (from, to - from);
            }

            /// <summary>ASCII-case-insensitive search for an ASCII needle from <paramref name="from"/>.</summary>
            static int IndexOfAscii(ReadOnlySpan<byte> u, int from, ReadOnlySpan<byte> needle)
            {
                for (int i = Math.Max(0, from); i + needle.Length <= u.Length; i++)
                {
                    int k = 0;
                    while (k < needle.Length && Lower(u[i + k]) == Lower(needle[k])) k++;
                    if (k == needle.Length) return i;
                }
                return -1;

                static int Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b;
            }

            static bool StartsAscii(ReadOnlySpan<byte> u, string literal)
                => u.Length >= literal.Length && Is(u[..literal.Length], literal);

            /// <summary>0.2.9 <c>HtmlText</c>: the description with its HTML entities decoded (tags kept — the seeds and
            /// the plain-text cut read them). Unchanged text is the SAME <see cref="TextRef"/>; a changed one is copied.</summary>
            static TextRef Unescaped(Staging s, TextRef raw)
            {
                var u = s.Utf8(raw);
                if (u.IndexOf((byte)'&') < 0 || u.Length > 4096) return raw;
                Span<byte> buffer = stackalloc byte[u.Length];
                int w = 0;
                for (int i = 0; i < u.Length;)
                {
                    if (u[i] == (byte)'&')
                    {
                        int semi = u.Slice(i, Math.Min(12, u.Length - i)).IndexOf((byte)';');
                        if (semi > 1 && Entity(u.Slice(i + 1, semi - 1), buffer[w..], out int written))
                        {
                            w += written;
                            i += semi + 1;
                            continue;
                        }
                    }
                    buffer[w++] = u[i++];
                }
                return s.AddText(buffer[..w]);

                static bool Entity(ReadOnlySpan<byte> name, Span<byte> into, out int written)
                {
                    written = 0;
                    byte single = Is(name, "amp") ? (byte)'&' : Is(name, "lt") ? (byte)'<' : Is(name, "gt") ? (byte)'>'
                                : Is(name, "quot") ? (byte)'"' : Is(name, "apos") ? (byte)'\'' : (byte)0;
                    if (single != 0) { into[0] = single; written = 1; return true; }
                    int codepoint = Is(name, "nbsp") ? 0xA0 : -1;
                    if (codepoint < 0 && name.Length > 1 && name[0] == (byte)'#')
                    {
                        long v = Number(name[1..]);
                        if (v is > 0 and < 0xD800) codepoint = (int)v;
                    }
                    if (codepoint < 0) return false;
                    var rune = new Rune(codepoint);
                    if (rune.Utf8SequenceLength > into.Length) return false;
                    written = rune.EncodeToUtf8(into);
                    return true;
                }
            }

            // ── what's new ───────────────────────────────────────────────────────────────────────────────────────────

            internal static bool Release(ref Utf8JsonReader r, List<Notification> into)
            {
                bool seen = false, played = false;
                long ts = 0, dated = 0;
                byte type = 0;
                string? uri = null, name = null, albumType = null, cover = null, show = null, artists = null;
                EntityUri subject = default;

                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (r.ValueTextEquals("state"u8))
                    {
                        for (int d = Fields(ref r); Next(ref r, d);)
                        {
                            if (r.ValueTextEquals("state"u8)) { r.Read(); seen = Says(ref r, "SEEN"); }
                            else SkipValue(ref r);
                        }
                    }
                    else if (r.ValueTextEquals("timestamp"u8)) ts = IsoField(ref r);
                    else if (r.ValueTextEquals("content"u8))
                    {
                        for (int c = Fields(ref r); Next(ref r, c);)
                        {
                            if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                            for (int d = Fields(ref r); Next(ref r, d);)
                            {
                                if (r.ValueTextEquals("__typename"u8)) { r.Read(); type = Says(ref r, "Album") ? (byte)1 : Says(ref r, "Episode") ? (byte)2 : (byte)0; }
                                else if (r.ValueTextEquals("uri"u8))
                                {
                                    r.Read();
                                    if (r.TokenType != JsonTokenType.String || r.HasValueSequence) continue;
                                    if (EntityId.TryParseGid(r.ValueSpan, out var id)) subject = new EntityUri(id);
                                    uri = Text(ref r);
                                }
                                else if (r.ValueTextEquals("name"u8)) { r.Read(); name = Text(ref r); }
                                else if (r.ValueTextEquals("type"u8)) { r.Read(); albumType = Text(ref r) ?? albumType; }
                                else if (r.ValueTextEquals("albumType"u8)) { r.Read(); var t = Text(ref r); if (t is not null && albumType is null) albumType = t; }
                                else if (r.ValueTextEquals("artists"u8)) artists = JoinArtists(ref r);
                                else if (r.ValueTextEquals("coverArt"u8)) cover = CoverUrl(ref r);
                                else if (r.ValueTextEquals("podcastV2"u8))
                                {
                                    for (int p = Fields(ref r); Next(ref r, p);)
                                    {
                                        if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                                        for (int pd = Fields(ref r); Next(ref r, pd);)
                                        {
                                            if (r.ValueTextEquals("name"u8)) { r.Read(); show = Text(ref r); }
                                            else SkipValue(ref r);
                                        }
                                    }
                                }
                                else if (r.ValueTextEquals("playedState"u8))
                                {
                                    for (int p = Fields(ref r); Next(ref r, p);)
                                    {
                                        if (r.ValueTextEquals("state"u8)) { r.Read(); played = Says(ref r, "FULLY_PLAYED"); }
                                        else SkipValue(ref r);
                                    }
                                }
                                else if (r.ValueTextEquals("date"u8) || r.ValueTextEquals("releaseDate"u8)) { long v = IsoField(ref r); if (dated == 0) dated = v; }
                                else SkipValue(ref r);
                            }
                        }
                    }
                    else SkipValue(ref r);
                }

                if (type == 0 || uri is null || name is null) return false;
                if (ts == 0) ts = dated;
                into.Add(type == 1
                    ? NotifyRows.ForRelease(uri, ts, !seen, NewReleaseKind.Album, subject, name, cover, artists ?? "", albumType, played: false)
                    : NotifyRows.ForRelease(uri, ts, !seen, NewReleaseKind.Episode, subject, name, cover, show ?? "", null, played));
                return true;
            }

            /// <summary><c>{ isoString }</c> → unix ms, 0 when absent or unparsable.</summary>
            static long IsoField(ref Utf8JsonReader r)
            {
                long ms = 0;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("isoString"u8)) { SkipValue(ref r); continue; }
                    r.Read();
                    if (r.TokenType == JsonTokenType.String && !r.HasValueSequence) ms = IsoInstantMs(r.ValueSpan);
                }
                return ms;
            }

            /// <summary>Only a non-empty JSON string is a value.</summary>
            static string? Text(ref Utf8JsonReader r)
                => r.TokenType == JsonTokenType.String && r.GetString() is { Length: > 0 } v ? v : null;

            /// <summary><c>artists</c> as <c>{ items: [ … ] }</c> or a bare array, each <c>{ profile: { name } }</c> or
            /// <c>{ name }</c> → "A, B".</summary>
            static string? JoinArtists(ref Utf8JsonReader r)
            {
                if (r.TokenType == JsonTokenType.PropertyName && !r.Read()) return null;
                string? joined = null;
                if (r.TokenType == JsonTokenType.StartObject)
                {
                    for (int d = r.CurrentDepth; Next(ref r, d);)
                    {
                        if (r.ValueTextEquals("items"u8) && EnterArray(ref r)) joined = Names(ref r);
                        else SkipValue(ref r);
                    }
                    return joined;
                }
                if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return null; }
                return Names(ref r);

                static string? Names(ref Utf8JsonReader r)
                {
                    string? all = null;
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        string? name = null;
                        for (int e = r.CurrentDepth; Next(ref r, e);)
                        {
                            if (r.ValueTextEquals("profile"u8))
                            {
                                for (int p = Fields(ref r); Next(ref r, p);)
                                {
                                    if (r.ValueTextEquals("name"u8)) { r.Read(); name = Text(ref r) ?? name; }
                                    else SkipValue(ref r);
                                }
                            }
                            else if (r.ValueTextEquals("name"u8)) { r.Read(); name ??= Text(ref r); }
                            else SkipValue(ref r);
                        }
                        if (name is { Length: > 0 }) all = all is null ? name : all + ", " + name;
                    }
                    return all;
                }
            }

            /// <summary><c>coverArt: { sources: [ { url } ] }</c> → the first url.</summary>
            static string? CoverUrl(ref Utf8JsonReader r)
            {
                string? url = null;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("sources"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        for (int e = r.CurrentDepth; Next(ref r, e);)
                        {
                            if (r.ValueTextEquals("url"u8)) { r.Read(); url ??= Text(ref r); }
                            else SkipValue(ref r);
                        }
                    }
                }
                return url;
            }

            // ── the top content ──────────────────────────────────────────────────────────────────────────────────────

            /// <summary><c>topArtists | topTracks: { items: [ … ] }</c> → staged rows + a range of
            /// <see cref="Staging.TopTargets"/>. A second occurrence of a uri is dropped BEFORE its row is staged, so
            /// "Duplicate" never overwrites the first one's title.</summary>
            internal static void TopList(ref Utf8JsonReader r, Staging s, bool tracks, out int start, out int count)
            {
                start = s.TopTargets.Count;
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        var node = default(Node);
                        EntityNode(ref r, s, ref node);         // unwraps {data} / {track} / {itemV2:{data}} / {profile}
                        if (node.Uri.IsEmpty) continue;
                        var kind = node.Uri.Kind(s);
                        if (kind != (tracks ? EntityKind.Track : EntityKind.Artist)) continue;
                        if (!tracks && node.Name.IsEmpty) continue;
                        bool repeated = false;
                        for (int i = start; i < s.TopTargets.Count && !repeated; i++) repeated = Same(s, in s.TopTargets[i], in node.Uri);
                        if (repeated) continue;
                        var staged = Stage(s, in node, Authority.Thin);
                        if (staged.IsEmpty) continue;
                        s.TopTargets.Add() = staged;
                    }
                }
                count = s.TopTargets.Count - start;
            }

            /// <summary>Emit the first non-empty priority's range as the run (an empty run when none held anything).</summary>
            internal static void TopRun(Staging s, in StagedId parent, bool tracks, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts)
            {
                ref var run = ref s.TopRuns.Add();
                run.Parent = parent;
                run.Tracks = tracks;
                run.Start = s.TopTargets.Count;
                run.Count = 0;
                for (int p = 0; p < counts.Length; p++)
                {
                    if (counts[p] <= 0) continue;
                    run.Start = starts[p];
                    run.Count = counts[p];
                    break;
                }
            }
        }
    }
}
