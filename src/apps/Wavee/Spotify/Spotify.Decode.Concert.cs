// ── Spotify/Spotify.Decode.Concert.cs ───────────────────────────────────────────────────────────────────────────────
// the twelve concert answers: concert detail, artist concerts, concepts, feed, count, location details, city search,
// lat/lon lookup, save location, user location, inferred location, artist-page location
//
// Role: CORE
// Owner: N (stream N-C)
// Wave: 5
// Budget: ~900 (new decoder partial, WP-5.N contract §0)
// Spec: ch 17 §7 (DATA GAPS), gap register G-045 ("StagedConcert has no producer"); the wire shapes are 0.2.9's
//   `SpotifyLive/ConcertPathfinderMapper.cs` (554 lines of JsonElement), verified against the captured fixtures in
//   Wavee.Tests/Fixtures/concerts/
//
// ONE `Utf8JsonReader` FOLD PER OPERATION, into `Staging` (the reader idiom of Spotify.Decode.Pathfinder.cs §9). No DOM,
// no strings: text goes to the arena, rows to `s.Concerts` / `s.Places` / `s.ConcertFeedRows`, relations to the concert
// staging (Concert.cs). What a mapper decided in 0.2.9 is decided here the same way:
//   · a concert needs a `spotify:concert:` uri AND a parseable start date, or it is not a row;
//   · the concert-level image (and its extracted DARK accent) wins over the first artist's avatar/banner accent;
//   · a lineup artist needs a name; its uri is optional (a billing-only act);
//   · an image's LARGEST measured rendition wins, and an unmeasured header list keeps its LAST (largest) entry;
//   · an extracted colour flagged `isFallback` is no colour;
//   · feed sections dedupe concerts ACROSS sections (the commit's merge does it, ConcertFeedMerge);
//   · a place needs a name and an id or a geohash.
//
// FORWARD-ONLY: a concert's `artists` and `offers` arrive BEFORE its `uri`, so those lists are pushed and closed as
// DEFERRED runs (`StagedConcertEdgeList.CloseDeferred`) and bound to the concert once its uri is known.

using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // ── the show node ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One decoded concert node before it is staged.</summary>
        struct ShowNode
        {
            public StagedId Uri;
            public TextRef Title, Venue, City, Region, Country, Image, Age, Status, VenueUri, MetroId;
            public TextRef ArtistImage;
            public long Date, Doors;
            public short Offset, DoorsOffset;
            public uint Accent, ArtistAccent;
            public float Lat, Lon;
            public bool HasDate, HasDoors, HasCoords, Festival;
            public int LineupRun, OffersRun, RelatedRun;
            // a playlist wrapper in a feed section reads the same object
            public TextRef Name, Description;
        }

        /// <summary>Which optional lists a show decode stages.</summary>
        [Flags]
        enum ShowParts : byte { None = 0, Lineup = 1, Detail = 2 }

        static void ShowObject(ref Utf8JsonReader r, Staging s, ref ShowNode n, ShowParts parts)
        {
            for (int d = Fields(ref r); Next(ref r, d);) ShowProperty(ref r, s, ref n, parts);
        }

        static void ShowProperty(ref Utf8JsonReader r, Staging s, ref ShowNode n, ShowParts parts)
        {
            if (r.ValueTextEquals("data"u8)) { ShowObject(ref r, s, ref n, parts); return; }
            if (r.ValueTextEquals("uri"u8)) { r.Read(); if (n.Uri.IsEmpty) n.Uri = JsonId(ref r, s); return; }
            if (r.ValueTextEquals("title"u8)) { r.Read(); n.Title = s.AddJson(ref r); return; }
            if (r.ValueTextEquals("name"u8)) { r.Read(); n.Name = s.AddJson(ref r); return; }
            if (r.ValueTextEquals("description"u8)) { r.Read(); n.Description = s.AddJson(ref r); return; }
            if (r.ValueTextEquals("startDateIsoString"u8))
            {
                r.Read();
                n.HasDate = r.TokenType == JsonTokenType.String && !r.HasValueSequence
                            && ConcertTime.TryParseIso(r.ValueSpan, out n.Date, out n.Offset);
                return;
            }
            if (r.ValueTextEquals("doorsOpenTimeIsoString"u8))
            {
                r.Read();
                n.HasDoors = r.TokenType == JsonTokenType.String && !r.HasValueSequence
                             && ConcertTime.TryParseIso(r.ValueSpan, out n.Doors, out n.DoorsOffset);
                return;
            }
            if (r.ValueTextEquals("festival"u8)) { r.Read(); n.Festival = r.TokenType == JsonTokenType.True; return; }
            if (r.ValueTextEquals("ageRestriction"u8)) { r.Read(); n.Age = s.AddJson(ref r); return; }
            if (r.ValueTextEquals("status"u8)) { r.Read(); n.Status = s.AddJson(ref r); return; }
            if (r.ValueTextEquals("location"u8)) { Location(ref r, s, ref n); return; }
            if (r.ValueTextEquals("venue"u8)) { n.VenueUri = NestedUri(ref r, s); return; }
            if (r.ValueTextEquals("images"u8)) { ConcertImages(ref r, s, ref n); return; }
            if (r.ValueTextEquals("artists"u8)) { Lineup(ref r, s, ref n, (parts & ShowParts.Lineup) != 0); return; }
            if ((parts & ShowParts.Detail) != 0 && r.ValueTextEquals("offers"u8)) { Offers(ref r, s, ref n); return; }
            if ((parts & ShowParts.Detail) != 0 && r.ValueTextEquals("relatedConcerts"u8)) { Related(ref r, s, ref n); return; }
            SkipValue(ref r);
        }

        static void Location(ref Utf8JsonReader r, Staging s, ref ShowNode n)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("name"u8)) { r.Read(); n.Venue = s.AddJson(ref r); }
                else if (r.ValueTextEquals("city"u8)) { r.Read(); n.City = s.AddJson(ref r); }
                else if (r.ValueTextEquals("region"u8)) { r.Read(); n.Region = s.AddJson(ref r); }
                else if (r.ValueTextEquals("country"u8)) { r.Read(); n.Country = s.AddJson(ref r); }
                else if (r.ValueTextEquals("coordinates"u8)) n.HasCoords = Coordinates(ref r, out n.Lat, out n.Lon);
                else if (r.ValueTextEquals("metroAreaLocation"u8)) n.MetroId = OneText(ref r, s, "geonameId"u8);
                else SkipValue(ref r);
            }
        }

        static bool Coordinates(ref Utf8JsonReader r, out float lat, out float lon)
        {
            lat = 0; lon = 0;
            bool hasLat = false, hasLon = false;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("latitude"u8)) { r.Read(); if (r.TokenType == JsonTokenType.Number) { lat = (float)r.GetDouble(); hasLat = true; } }
                else if (r.ValueTextEquals("longitude"u8)) { r.Read(); if (r.TokenType == JsonTokenType.Number) { lon = (float)r.GetDouble(); hasLon = true; } }
                else SkipValue(ref r);
            }
            return hasLat && hasLon;
        }

        /// <summary><c>{ data: { uri } }</c> / <c>{ uri }</c>.</summary>
        static TextRef NestedUri(ref Utf8JsonReader r, Staging s)
        {
            TextRef uri = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("data"u8)) { var inner = NestedUri(ref r, s); if (uri.IsEmpty) uri = inner; }
                else if (r.ValueTextEquals("uri"u8)) { r.Read(); uri = s.AddJson(ref r); }
                else SkipValue(ref r);
            }
            return uri;
        }

        /// <summary><c>images.items[0]</c>: the concert-level cover and its dark accent.</summary>
        static void ConcertImages(ref Utf8JsonReader r, Staging s, ref ShowNode n)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                bool first = true;
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    if (!first) { r.Skip(); continue; }
                    first = false;
                    uint accent = 0;
                    var url = Image(ref r, s, preferLast: false, ref accent);
                    if (!url.IsEmpty) { n.Image = url; n.Accent = accent; }
                }
            }
        }

        /// <summary>An image node (<c>{ sources[], extractedColors }</c>, possibly under <c>data</c>) → its best url. The
        /// LARGEST measured rendition wins; with <paramref name="preferLast"/> an unmeasured list keeps its last entry
        /// (header lists are smallest-first, avatar lists largest-first — 0.2.9's <c>MapImage</c>).</summary>
        static TextRef Image(ref Utf8JsonReader r, Staging s, bool preferLast, ref uint accent)
        {
            TextRef best = default;
            long bestArea = -1;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("data"u8))
                {
                    var inner = Image(ref r, s, preferLast, ref accent);
                    if (!inner.IsEmpty) best = inner;
                }
                else if (r.ValueTextEquals("sources"u8) && EnterArray(ref r))
                {
                    for (int list = r.CurrentDepth; Element(ref r, list);)
                    {
                        TextRef url = default;
                        long w = 0, h = 0;
                        for (int e = r.CurrentDepth; Next(ref r, e);)
                        {
                            if (r.ValueTextEquals("url"u8)) { r.Read(); url = s.AddJson(ref r); }
                            else if (r.ValueTextEquals("width"u8) || r.ValueTextEquals("maxWidth"u8)) { r.Read(); if (w == 0) w = Num(ref r); }
                            else if (r.ValueTextEquals("height"u8) || r.ValueTextEquals("maxHeight"u8)) { r.Read(); if (h == 0) h = Num(ref r); }
                            else SkipValue(ref r);
                        }
                        if (url.IsEmpty) continue;
                        long area = w > 0 && h > 0 ? w * h : 0;
                        if (best.IsEmpty || area > bestArea || (preferLast && area == bestArea && area == 0))
                        {
                            best = url;
                            bestArea = area;
                        }
                    }
                }
                else if (r.ValueTextEquals("extractedColors"u8)) accent = DarkColor(ref r);
                else SkipValue(ref r);
            }
            return best;
        }

        /// <summary><c>{ colorDark: { hex, isFallback } }</c> → opaque ARGB; a fallback swatch is no colour.</summary>
        static uint DarkColor(ref Utf8JsonReader r)
        {
            uint argb = 0;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("colorDark"u8)) { SkipValue(ref r); continue; }
                bool fallback = false;
                uint colour = 0;
                for (int k = Fields(ref r); Next(ref r, k);)
                {
                    if (r.ValueTextEquals("hex"u8))
                    {
                        r.Read();
                        if (r.TokenType == JsonTokenType.String && !r.HasValueSequence) colour = Argb(r.ValueSpan);
                    }
                    else if (r.ValueTextEquals("isFallback"u8)) { r.Read(); fallback = r.TokenType == JsonTokenType.True; }
                    else SkipValue(ref r);
                }
                argb = fallback ? 0 : colour;
            }
            return argb;
        }

        /// <summary><c>artists.items[]</c>: the lineup (pushed, closed deferred) and the first avatar / banner accent a
        /// concert with no cover of its own borrows.</summary>
        static void Lineup(ref Utf8JsonReader r, Staging s, ref ShowNode n, bool stage)
        {
            var list = s.ConcertEdges;
            int mark = list.PendingMark;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int items = r.CurrentDepth; Element(ref r, items);)
                {
                    var a = default(LineupNode);
                    LineupArtist(ref r, s, ref a);
                    if (a.Name.IsEmpty) continue;
                    if (n.ArtistImage.IsEmpty && !a.Avatar.IsEmpty) n.ArtistImage = a.Avatar;
                    if (n.ArtistAccent == 0 && a.Accent != 0) n.ArtistAccent = a.Accent;
                    if (!stage) continue;
                    ref var e = ref list.Push();
                    bool artist = !a.Uri.IsEmpty && EntityUri.KindOf(s.Utf8(a.Uri)) == EntityKind.Artist && StartsWith(s.Utf8(a.Uri), "spotify:artist:");
                    if (artist) e.Target = new StagedId(a.Uri);
                    e.T0 = a.Name;
                    e.T1 = a.Avatar;
                    e.T2 = a.Header;
                    e.U0 = a.Accent;
                    e.B0 = artist ? LineupEdge.Navigable : (byte)0;
                }
            }
            if (stage) n.LineupRun = list.CloseDeferred(ConcertLink.Lineup, mark);
            else list.Pop(mark);
        }

        struct LineupNode
        {
            public TextRef Name, Uri, Avatar, Header;
            public uint Accent;
        }

        static void LineupArtist(ref Utf8JsonReader r, Staging s, ref LineupNode a)
        {
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("data"u8)) LineupArtist(ref r, s, ref a);
                else if (r.ValueTextEquals("uri"u8)) { r.Read(); a.Uri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("profile"u8)) a.Name = OneText(ref r, s, "name"u8);
                else if (r.ValueTextEquals("headerImage"u8))
                {
                    uint ignored = 0;                           // the lineup banner carries no colour (0.2.9 MapArtists)
                    var header = Image(ref r, s, preferLast: true, ref ignored);
                    if (!header.IsEmpty) a.Header = header;
                }
                else if (r.ValueTextEquals("visuals"u8))
                {
                    for (int v = Fields(ref r); Next(ref r, v);)
                    {
                        if (r.ValueTextEquals("avatarImage"u8)) { uint none = 0; a.Avatar = Image(ref r, s, preferLast: false, ref none); }
                        else if (r.ValueTextEquals("headerImage"u8))
                        {
                            uint accent = 0;
                            var header = Image(ref r, s, preferLast: true, ref accent);
                            if (a.Header.IsEmpty) a.Header = header;
                            if (accent != 0) a.Accent = accent;
                        }
                        else SkipValue(ref r);
                    }
                }
                else SkipValue(ref r);
            }
        }

        /// <summary><c>offers.items[]</c> (the detail): one payload edge per offer with a provider name.</summary>
        static void Offers(ref Utf8JsonReader r, Staging s, ref ShowNode n)
        {
            var list = s.ConcertEdges;
            int mark = list.PendingMark;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int items = r.CurrentDepth; Element(ref r, items);)
                {
                    var o = default(StagedConcertEdge);
                    for (int f = r.CurrentDepth; Next(ref r, f);)
                    {
                        if (r.ValueTextEquals("providerName"u8)) { r.Read(); o.T0 = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("url"u8)) { r.Read(); o.T1 = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("currency"u8)) { r.Read(); o.T2 = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("availability"u8))
                        {
                            r.Read();
                            o.B0 = Says(ref r, "AVAILABLE") ? (byte)ConcertOfferAvailability.Available
                                 : Says(ref r, "UNAVAILABLE") ? (byte)ConcertOfferAvailability.Unavailable
                                 : (byte)ConcertOfferAvailability.Unknown;
                        }
                        else if (r.ValueTextEquals("minPrice"u8)) { r.Read(); if (Cents(ref r, out o.I0)) o.B1 |= OfferEdge.HasMin; }
                        else if (r.ValueTextEquals("maxPrice"u8)) { r.Read(); if (Cents(ref r, out o.I1)) o.B1 |= OfferEdge.HasMax; }
                        else if (r.ValueTextEquals("hasPromoCodes"u8)) { r.Read(); if (r.TokenType == JsonTokenType.True) o.B1 |= OfferEdge.HasPromoCodes; }
                        else if (r.ValueTextEquals("firstParty"u8)) { r.Read(); if (r.TokenType == JsonTokenType.True) o.B1 |= OfferEdge.FirstParty; }
                        else if (r.ValueTextEquals("dates"u8))
                        {
                            for (int k = Fields(ref r); Next(ref r, k);)
                            {
                                bool start = r.ValueTextEquals("startDateIsoString"u8);
                                if (!start && !r.ValueTextEquals("endDateIsoString"u8)) { SkipValue(ref r); continue; }
                                r.Read();
                                if (r.TokenType != JsonTokenType.String || r.HasValueSequence
                                    || !ConcertTime.TryParseIso(r.ValueSpan, out long ms, out short offset)) continue;
                                if (start) { o.L0 = ms / 1000; o.I2 = offset; o.B1 |= OfferEdge.HasSaleStart; }
                                else { o.L1 = ms / 1000; o.I3 = offset; o.B1 |= OfferEdge.HasSaleEnd; }
                            }
                        }
                        else SkipValue(ref r);
                    }
                    if (o.T0.IsEmpty) continue;                  // an offer with no provider is not an offer (0.2.9)
                    list.Push() = o;
                }
            }
            n.OffersRun = list.CloseDeferred(ConcertLink.Offers, mark);
        }

        static bool Cents(ref Utf8JsonReader r, out int cents)
        {
            cents = 0;
            if (r.TokenType != JsonTokenType.Number || !r.TryGetDecimal(out decimal value)) return false;
            decimal scaled = Math.Round(value * 100m, MidpointRounding.AwayFromZero);
            if (scaled is < int.MinValue or > int.MaxValue) return false;
            cents = (int)scaled;
            return true;
        }

        /// <summary><c>relatedConcerts.items[]</c> (the detail): each a thin tile row, the list a deferred run.</summary>
        static void Related(ref Utf8JsonReader r, Staging s, ref ShowNode n)
        {
            var list = s.ConcertEdges;
            int mark = list.PendingMark;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int items = r.CurrentDepth; Element(ref r, items);)
                {
                    var related = NewShow();
                    ShowObject(ref r, s, ref related, ShowParts.None);
                    var id = StageShow(s, ref related, Authority.Thin, (uint)ConcertFields.Tile, nearMask: false, near: false);
                    if (!id.IsEmpty) list.Push().Target = id;
                }
            }
            n.RelatedRun = list.CloseDeferred(ConcertLink.Related, mark);
        }

        static ShowNode NewShow() => new() { LineupRun = -1, OffersRun = -1, RelatedRun = -1 };

        /// <summary>Stage a decoded show as a concert row (and bind its deferred runs); default when it is not a concert
        /// (no <c>spotify:concert:</c> uri, or no date).</summary>
        static StagedId StageShow(Staging s, ref ShowNode n, Authority authority, uint known, bool nearMask, bool near)
        {
            if (n.Uri.IsEmpty || !n.HasDate || n.Uri.Kind(s) != EntityKind.Concert) return default;
            var image = n.Image.IsEmpty ? n.ArtistImage : n.Image;
            uint accent = n.Accent != 0 ? n.Accent : n.ArtistAccent;

            ref var row = ref s.Concerts.RowFor(n.Uri, authority, known);
            row.Title = n.Title;
            row.Venue = n.Venue;
            row.City = n.City;
            row.Date = n.Date;
            row.OffsetMinutes = n.Offset;
            row.Image = image;
            row.Accent = accent;
            row.Flags = (n.Festival ? (uint)ConcertFlags.Festival : 0)
                      | (image.IsEmpty ? 0 : (uint)ConcertFlags.HasArt)
                      | (near ? (uint)ConcertFlags.NearUser : 0);
            if (nearMask) row.FlagsMask = (uint)ConcertFlags.NearMask;
            if ((known & (uint)ConcertFields.Detail & ~(uint)ConcertFields.Tile) != 0)
            {
                row.Region = n.Region;
                row.Country = n.Country;
                row.VenuePlace = n.VenueUri;
                row.MetroArea = n.MetroId;
                row.AgeRestriction = n.Age;
                row.StatusText = n.Status;
                // The byte is "is this a NOTABLE state" (ConcertDetailInfo.DisplayStatus); the provider's word rides the text.
                row.Status = n.Status.IsEmpty || IsDefaultStatus(s.Utf8(n.Status)) ? (byte)0 : (byte)1;
                if (n.HasDoors) { row.DoorsOpenAt = n.Doors; row.DoorsOffsetMinutes = n.DoorsOffset; }
                if (n.HasCoords) { row.Lat = n.Lat; row.Lon = n.Lon; }
            }
            var list = s.ConcertEdges;
            list.BindParent(n.LineupRun, in n.Uri);
            list.BindParent(n.OffersRun, in n.Uri);
            list.BindParent(n.RelatedRun, in n.Uri);
            return n.Uri;
        }

        static bool IsDefaultStatus(ReadOnlySpan<byte> status)
            => Is(status, "UNKNOWN") || Is(status, "CONFIRMED") || Is(status, "SCHEDULED");

        // ── 1. concert (the detail) ──────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>concert</c> → the row at FULL authority speaking for every group (<see cref="ConcertFields.All"/>),
        /// its offers, lineup and related runs — each landed even when EMPTY, because "no offers" is the answer that omits
        /// the section. Answers the concert's identity, default when the answer carried no concert.</summary>
        public static StagedId ConcertDetail(ReadOnlySpan<byte> json, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "concert"u8)) return default;
            var list = s.ConcertEdges;
            var n = NewShow();
            ShowObject(ref r, s, ref n, ShowParts.Lineup | ShowParts.Detail);
            if (n.LineupRun < 0) n.LineupRun = list.CloseDeferred(ConcertLink.Lineup, list.PendingMark);
            if (n.OffersRun < 0) n.OffersRun = list.CloseDeferred(ConcertLink.Offers, list.PendingMark);
            if (n.RelatedRun < 0) n.RelatedRun = list.CloseDeferred(ConcertLink.Related, list.PendingMark);
            return StageShow(s, ref n, Authority.Full, (uint)ConcertFields.All, nearMask: false, near: false);
        }

        // ── 2. ArtistConcerts (the schedule) ─────────────────────────────────────────────────────────────────────────

        /// <summary><c>ArtistConcerts</c> → the artist's banner (Header group only), every show of
        /// <c>concerts.concerts.items[]</c> as a thin tile row with its lineup, the <see cref="ConcertLink.ArtistConcerts"/>
        /// run (Complete even when empty) and the near-you bit: set on every show the <c>nearby</c> branch lists and
        /// CLEARED on every other show of the list, so a stale bit from an earlier location cannot survive.</summary>
        public static void ArtistConcerts(ReadOnlySpan<byte> json, ReadOnlySpan<byte> artistUri, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;
            var parent = artistUri.IsEmpty ? default : new StagedId(s.AddText(artistUri));
            var list = s.ConcertEdges;
            int mark = list.PendingMark;
            int mainRows = -1, mainEnd = -1, nearStart = -1, nearEnd = -1;
            TextRef header = default;
            uint headerAccent = 0;
            bool answered = false;

            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    answered = true;
                    if (r.ValueTextEquals("artistUnion"u8))
                    {
                        for (int a = Fields(ref r); Next(ref r, a);)
                        {
                            if (r.ValueTextEquals("uri"u8)) { r.Read(); var uri = s.AddJson(ref r); if (!uri.IsEmpty) parent = new StagedId(uri); }
                            else if (r.ValueTextEquals("headerImage"u8)) header = Image(ref r, s, preferLast: true, ref headerAccent);
                            else SkipValue(ref r);
                        }
                    }
                    else if (r.ValueTextEquals("nearby"u8))
                    {
                        for (int b = Fields(ref r); Next(ref r, b);)
                        {
                            if (!r.ValueTextEquals("concerts"u8)) { SkipValue(ref r); continue; }
                            nearStart = s.Concerts.Count;
                            ShowItems(ref r, s, ShowParts.Lineup, near: true, push: false);
                            nearEnd = s.Concerts.Count;
                        }
                    }
                    else if (r.ValueTextEquals("concerts"u8))
                    {
                        for (int b = Fields(ref r); Next(ref r, b);)
                        {
                            if (!r.ValueTextEquals("concerts"u8)) { SkipValue(ref r); continue; }
                            mainRows = s.Concerts.Count;
                            ShowItems(ref r, s, ShowParts.Lineup, near: false, push: true);
                            mainEnd = s.Concerts.Count;
                        }
                    }
                    else SkipValue(ref r);
                }
            }

            if (parent.IsEmpty || !answered) { list.Pop(mark); return; }
            // near bits over the main list (order-independent: a show in both lists ends with the bit set in both rows)
            if (mainEnd >= 0)
            {
                var rows = s.Concerts.Span;
                for (int i = mainRows; i < mainEnd; i++)
                {
                    rows[i].FlagsMask = (uint)ConcertFlags.NearMask;
                    if (nearStart >= 0 && ListsUri(s, rows, nearStart, nearEnd, in rows[i].Id)) rows[i].Flags |= (uint)ConcertFlags.NearUser;
                }
            }
            if (!header.IsEmpty)
            {
                ref var artist = ref s.Artists.RowFor(parent, Authority.Thin, (uint)ArtistFields.Header);
                artist.Header = header;
                artist.HeaderAccent = headerAccent;
            }
            list.Close(ConcertLink.ArtistConcerts, in parent, mark);
        }

        /// <summary><c>{ items: [ { data: show } ] }</c> → thin tile rows, each pushed as a member when asked.</summary>
        static void ShowItems(ref Utf8JsonReader r, Staging s, ShowParts parts, bool near, bool push)
        {
            var list = s.ConcertEdges;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int items = r.CurrentDepth; Element(ref r, items);)
                {
                    var n = NewShow();
                    ShowObject(ref r, s, ref n, parts);
                    var id = StageShow(s, ref n, Authority.Thin, (uint)ConcertFields.Tile, nearMask: near, near: near);
                    if (push && !id.IsEmpty) list.Push().Target = id;
                }
            }
        }

        static bool ListsUri(Staging s, Span<StagedConcert> rows, int start, int end, in StagedId id)
        {
            var uri = id.Packed.IsEmpty ? s.Utf8(id.Text) : default;
            for (int i = start; i < end && i < rows.Length; i++)
            {
                ref readonly var other = ref rows[i].Id;
                if (!id.Packed.IsEmpty) { if (other.Packed == id.Packed) return true; continue; }
                if (other.Packed.IsEmpty && s.Utf8(other.Text).SequenceEqual(uri)) return true;
            }
            return false;
        }

        // ── 3. concertConcepts ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>concertConcepts.items[]</c> → the place's <see cref="ConcertLink.PlaceConcepts"/> run in wire (weight)
        /// order, Complete even when empty. A token needs a uri and a name.</summary>
        public static void ConcertConcepts(ReadOnlySpan<byte> json, ReadOnlySpan<byte> placeKey, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (placeKey.IsEmpty || !Descend(ref r, "data"u8, "concertConcepts"u8)) return;
            var run = s.ConcertRun(ConcertLink.PlaceConcepts);
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int items = r.CurrentDepth; Element(ref r, items);)
                {
                    TextRef uri = default, name = default;
                    double weight = 0;
                    for (int f = r.CurrentDepth; Next(ref r, f);)
                    {
                        if (r.ValueTextEquals("data"u8))
                        {
                            for (int k = Fields(ref r); Next(ref r, k);)
                            {
                                if (r.ValueTextEquals("uri"u8)) { r.Read(); uri = s.AddJson(ref r); }
                                else if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                                else SkipValue(ref r);
                            }
                        }
                        else if (r.ValueTextEquals("weight"u8)) { r.Read(); if (r.TokenType == JsonTokenType.Number) weight = r.GetDouble(); }
                        else SkipValue(ref r);
                    }
                    if (uri.IsEmpty || name.IsEmpty) continue;
                    ref var e = ref run.Add();
                    e.T0 = uri;
                    e.T1 = name;
                    e.U0 = BitConverter.SingleToUInt32Bits((float)weight);
                }
            }
            run.EndKeyed(s.AddText(placeKey));
        }

        // ── 4. concertFeed ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One section member of a feed page, collected during the walk and landed contiguously after it (a
        /// show's nested lists close as the walk goes, so the section runs cannot be appended in the middle of it).</summary>
        struct FeedMember
        {
            public StagedId Target;
            public TextRef Key;
            public byte Kind;
            public bool Playlist;
        }

        [ThreadStatic] static List<FeedMember>? t_feedMembers;

        /// <summary><c>concertFeed</c> → thin tile rows (a Nearby show carries the near-you bit), promo playlist rows, the
        /// subject's <see cref="ConcertLink.FeedSection"/> and <see cref="ConcertLink.FeedPlaylists"/> runs — a whole
        /// rewrite for a first page, a MERGE for <paramref name="append"/> (ConcertFeedMerge) — and the subject's
        /// continuation (empty = no tail). Section kinds are 0.2.9's: <c>ConcertCarousel</c> → Nearby,
        /// <c>LiveEventSection</c> → Recommended, <c>AllEvents</c>'s nested sections → AllEvents (its
        /// <c>paginationKey</c> is the subject's). False when the answer is not a feed.</summary>
        public static bool ConcertFeed(ReadOnlySpan<byte> json, ReadOnlySpan<byte> feedKey, bool append, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (feedKey.IsEmpty || !Descend(ref r, "data"u8, "liveEventsFeed"u8)) return false;
            var members = t_feedMembers ??= new List<FeedMember>(64);
            members.Clear();
            TextRef pagination = default;
            bool sawSections = false;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("sections"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                sawSections = true;
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    int type = SectionTypeOf(r);
                    if (type == 2)
                    {
                        for (int f = r.CurrentDepth; Next(ref r, f);)
                        {
                            if (r.ValueTextEquals("paginationKey"u8)) { r.Read(); pagination = s.AddJson(ref r); }
                            else if (r.ValueTextEquals("sections"u8) && EnterArray(ref r))
                            {
                                for (int nested = r.CurrentDepth; Element(ref r, nested);)
                                    FeedSectionBody(ref r, s, ConcertFeedSectionKind.AllEvents, members);
                            }
                            else SkipValue(ref r);
                        }
                    }
                    else if (type == 0) FeedSectionBody(ref r, s, ConcertFeedSectionKind.Nearby, members);
                    else if (type == 1) FeedSectionBody(ref r, s, ConcertFeedSectionKind.Recommended, members);
                    else r.Skip();
                }
            }
            if (!sawSections) return false;

            var feed = s.AddText(feedKey);
            var concerts = s.ConcertRun(ConcertLink.FeedSection);
            foreach (var m in members)
            {
                if (m.Playlist) continue;
                ref var e = ref concerts.Add(in m.Target);
                e.B0 = m.Kind;
                e.T0 = m.Key;
            }
            if (append) concerts.AppendKeyed(feed); else concerts.EndKeyed(feed);

            var promos = s.ConcertRun(ConcertLink.FeedPlaylists);
            foreach (var m in members)
            {
                if (!m.Playlist) continue;
                ref var e = ref promos.Add(in m.Target);
                e.B0 = m.Kind;
                e.T0 = m.Key;
            }
            if (append) promos.AppendKeyed(feed); else promos.EndKeyed(feed);

            ref var row = ref s.ConcertFeedRows.Add();
            row.Key = feed;
            row.PaginationKey = pagination;
            row.Parts = StagedConcertFeed.PagePart;
            members.Clear();
            return true;
        }

        /// <summary>A section's <c>__typename</c>, read off a COPY of the reader: 0 ConcertCarousel, 1 LiveEventSection,
        /// 2 AllEvents, -1 anything else.</summary>
        static int SectionTypeOf(Utf8JsonReader r)
        {
            for (int depth = r.CurrentDepth; Next(ref r, depth);)
            {
                if (!r.ValueTextEquals("__typename"u8)) { SkipValue(ref r); continue; }
                r.Read();
                return Says(ref r, "ConcertCarousel") ? 0 : Says(ref r, "LiveEventSection") ? 1 : Says(ref r, "AllEvents") ? 2 : -1;
            }
            return -1;
        }

        /// <summary>A section's <c>key</c>, off a copy of the reader.</summary>
        static TextRef SectionKeyOf(Utf8JsonReader r, Staging s)
        {
            for (int depth = r.CurrentDepth; Next(ref r, depth);)
            {
                if (!r.ValueTextEquals("key"u8)) { SkipValue(ref r); continue; }
                r.Read();
                return s.AddJson(ref r);
            }
            return default;
        }

        static void FeedSectionBody(ref Utf8JsonReader r, Staging s, ConcertFeedSectionKind kind, List<FeedMember> members)
        {
            var key = SectionKeyOf(r, s);
            if (key.IsEmpty)
                key = s.AddText(kind switch
                {
                    ConcertFeedSectionKind.Nearby => "Nearby"u8,
                    ConcertFeedSectionKind.Recommended => "Recommended"u8,
                    _ => "AllEvents"u8,
                });
            bool nearby = kind == ConcertFeedSectionKind.Nearby;
            for (int f = r.CurrentDepth; Next(ref r, f);)
            {
                if (!r.ValueTextEquals("concerts"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int items = r.CurrentDepth; Element(ref r, items);)
                {
                    var n = NewShow();
                    ShowObject(ref r, s, ref n, ShowParts.None);
                    if (n.Uri.IsEmpty) continue;
                    var entity = n.Uri.Kind(s);
                    if (entity == EntityKind.Concert)
                    {
                        var id = StageShow(s, ref n, Authority.Thin, (uint)ConcertFields.Tile, nearMask: nearby, near: nearby);
                        if (!id.IsEmpty) members.Add(new FeedMember { Target = id, Key = key, Kind = (byte)kind });
                    }
                    else if (entity == EntityKind.Playlist && !n.Name.IsEmpty)
                    {
                        ref var p = ref s.Playlists.RowFor(n.Uri, Authority.Thin, (uint)PlaylistFields.Identity);
                        p.Title = n.Name;
                        p.Description = n.Description;
                        p.Image = n.Image;
                        members.Add(new FeedMember { Target = n.Uri, Key = key, Kind = (byte)kind, Playlist = true });
                    }
                }
            }
        }

        // ── 5. concertCount ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>concertCount</c> → <c>concerts.concerts.totalCount</c> as the subject's count preview for ask
        /// <paramref name="askVersion"/>. False (nothing staged) when the branch is malformed — a count nobody answered is
        /// never a 0.</summary>
        public static bool ConcertCount(ReadOnlySpan<byte> json, ReadOnlySpan<byte> feedKey, uint askVersion, Staging s)
        {
            if (feedKey.IsEmpty || ConcertCountOf(json) is not { } total) return false;
            ref var row = ref s.ConcertFeedRows.Add();
            row.Key = s.AddText(feedKey);
            row.Count = total;
            row.CountVersion = askVersion;
            row.Parts = StagedConcertFeed.CountPart;
            return true;
        }

        /// <summary>The count alone (PURE): null when the branch is missing or not an integer.</summary>
        public static int? ConcertCountOf(ReadOnlySpan<byte> json)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "concerts"u8)) return null;
            int? total = null;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (!r.ValueTextEquals("concerts"u8)) { SkipValue(ref r); continue; }
                for (int k = Fields(ref r); Next(ref r, k);)
                {
                    if (r.ValueTextEquals("totalCount"u8))
                    {
                        r.Read();
                        if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out int value)) total = value;
                    }
                    else SkipValue(ref r);
                }
            }
            return total;
        }

        // ── 6-8. the location reads: search, lat/lon, location details ───────────────────────────────────────────────

        /// <summary><c>searchConcertLocations</c> / <c>concertLocationsByLatLon</c> / <c>concertLocationDetails</c> →
        /// every <c>concertLocations.items[]</c> place (and a details answer's <c>me.profile.location</c>) as a
        /// <see cref="PlaceRole.Match"/> row, in wire order. Answers how many were staged.</summary>
        public static int ConcertLocations(ReadOnlySpan<byte> json, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return 0;
            int staged = 0;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("data"u8)) { SkipValue(ref r); continue; }
                for (int d = Fields(ref r); Next(ref r, d);)
                {
                    if (r.ValueTextEquals("concertLocations"u8))
                    {
                        for (int c = Fields(ref r); Next(ref r, c);)
                        {
                            if (!r.ValueTextEquals("items"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                            for (int items = r.CurrentDepth; Element(ref r, items);)
                                if (StagePlace(ref r, s, PlaceRole.Match, 0)) staged++;
                        }
                    }
                    else if (r.ValueTextEquals("me"u8))
                    {
                        if (ProfileLocation(ref r, s, PlaceRole.Match, 0)) staged++;
                    }
                    else SkipValue(ref r);
                }
            }
            return staged;
        }

        /// <summary><c>me: { profile: { location } }</c> (the reader on <c>me</c>) → one staged place.</summary>
        static bool ProfileLocation(ref Utf8JsonReader r, Staging s, PlaceRole role, uint flags)
        {
            bool staged = false;
            for (int me = Fields(ref r); Next(ref r, me);)
            {
                if (!r.ValueTextEquals("profile"u8)) { SkipValue(ref r); continue; }
                for (int p = Fields(ref r); Next(ref r, p);)
                {
                    if (r.ValueTextEquals("location"u8)) staged |= StagePlace(ref r, s, role, flags);
                    else SkipValue(ref r);
                }
            }
            return staged;
        }

        /// <summary>One place object → a staged place; a place needs a name and an id or a geohash (0.2.9 MapPlace).</summary>
        static bool StagePlace(ref Utf8JsonReader r, Staging s, PlaceRole role, uint flags)
        {
            TextRef id = default, geo = default, name = default, region = default, country = default;
            float lat = 0, lon = 0;
            bool coords = false;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("geonameId"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.String) id = s.AddJson(ref r);
                    else if (r.TokenType == JsonTokenType.Number && !r.HasValueSequence) id = s.AddText(r.ValueSpan);
                }
                else if (r.ValueTextEquals("geoHash"u8)) { r.Read(); geo = s.AddJson(ref r); }
                else if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                else if (r.ValueTextEquals("region"u8)) { r.Read(); region = s.AddJson(ref r); }
                else if (r.ValueTextEquals("country"u8)) { r.Read(); country = s.AddJson(ref r); }
                else if (r.ValueTextEquals("coordinates"u8)) coords = Coordinates(ref r, out lat, out lon);
                else SkipValue(ref r);
            }
            if (name.IsEmpty || (id.IsEmpty && geo.IsEmpty)) return false;

            TextRef key = id;
            if (key.IsEmpty)
            {
                var hash = s.Utf8(geo);
                Span<byte> buffer = stackalloc byte[4 + Math.Min(hash.Length, 124)];
                "geo:"u8.CopyTo(buffer);
                hash[..(buffer.Length - 4)].CopyTo(buffer[4..]);
                key = s.AddText(buffer);
            }
            ref var p = ref s.Places.Add();
            p.Key = key;
            p.Id = id;
            p.Name = name;
            p.Region = region;
            p.Country = country;
            p.GeoHash = geo;
            p.Lat = lat;
            p.Lon = lon;
            p.Flags = flags | (coords ? (uint)PlaceFlags.HasCoords : 0);
            p.Role = role;
            return true;
        }

        // ── 9-12. save, the user's location, its inferred bit, the artist page's location ───────────────────────────

        /// <summary><c>saveLocation</c> → <c>storeUserLocation.success == true</c>. Anything else is "not stored".</summary>
        public static bool SaveConcertLocation(ReadOnlySpan<byte> json)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "storeUserLocation"u8)) return false;
            bool success = false;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("success"u8)) { r.Read(); success = r.TokenType == JsonTokenType.True; }
                else SkipValue(ref r);
            }
            return success;
        }

        /// <summary><c>userLocation</c> → the account's place as <see cref="PlaceRole.Saved"/>, marked
        /// <see cref="PlaceFlags.Inferred"/> when <paramref name="inferred"/> (the separate <c>inferredUserLocation</c>
        /// answer) says so. False when the answer carried no usable place.</summary>
        public static bool UserLocation(ReadOnlySpan<byte> json, bool? inferred, Staging s)
            => MeLocation(json, s, PlaceRole.Saved, inferred == true ? (uint)PlaceFlags.Inferred : 0);

        /// <summary><c>ArtistConcertsPageLocation</c> → <see cref="PlaceRole.ArtistPage"/>.</summary>
        public static bool ArtistPageLocation(ReadOnlySpan<byte> json, Staging s)
            => MeLocation(json, s, PlaceRole.ArtistPage, 0);

        static bool MeLocation(ReadOnlySpan<byte> json, Staging s, PlaceRole role, uint flags)
        {
            var r = new Utf8JsonReader(json);
            return Descend(ref r, "data"u8, "me"u8) && ProfileLocation(ref r, s, role, flags);
        }

        /// <summary><c>inferredUserLocation</c> → <c>me.profile.location.isInferred</c>; null when absent or not a bool.
        /// PURE.</summary>
        public static bool? InferredUserLocation(ReadOnlySpan<byte> json)
        {
            var r = new Utf8JsonReader(json);
            if (!Descend(ref r, "data"u8, "me"u8)) return null;
            bool? inferred = null;
            for (int me = Fields(ref r); Next(ref r, me);)
            {
                if (!r.ValueTextEquals("profile"u8)) { SkipValue(ref r); continue; }
                for (int p = Fields(ref r); Next(ref r, p);)
                {
                    if (!r.ValueTextEquals("location"u8)) { SkipValue(ref r); continue; }
                    for (int l = Fields(ref r); Next(ref r, l);)
                    {
                        if (!r.ValueTextEquals("isInferred"u8)) { SkipValue(ref r); continue; }
                        r.Read();
                        inferred = r.TokenType switch { JsonTokenType.True => true, JsonTokenType.False => false, _ => inferred };
                    }
                }
            }
            return inferred;
        }
    }
}
