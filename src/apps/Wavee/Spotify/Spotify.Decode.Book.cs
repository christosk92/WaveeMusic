// ── Spotify/Spotify.Decode.Book.cs — the podcast/audiobook pages' extended-metadata kinds ─────────────────────────────
//
// Role: CORE (pure decoders into Staging)
// Lane: podcast-pages-data (2026-11-17). Wire: C:/WAVEE/scratch/abook/audiobook-api-map.md §4.1, §4.2, §4.6, §10.
//
// The official desktop client builds an audiobook page from extended-metadata (feature show_cosmos), and so does
// Wavee: every kind below rides the same batched POST as ShowV4 (Entities/Fetch.Routes.cs `s_show`), lands in the
// show's own columns under its own ShowFields group and authority (Entities/Show.cs), and is read by
// Entities/PodcastPages*.cs. The web player's GraphQL answers for the same facts (queryShowMetadataV2) stage into the
// SAME groups (Spotify.Decode.BookGraph.cs), so a page never cares which source spoke.
//
// Text columns hold small line formats, parsed by PodcastPages.Parse*: lists are one item per line, pairs are
// tab-separated. A name never contains a tab or a newline on the wire; the writer strips them anyway.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        // the kinds, by their extension_kind.proto names
        public const int AudiobookSpecificsKind = 52, AudiobookPriceKind = 64, AudiobookGenreKind = 83,
            ShowAccessKind = 31, PreviewPlaybackKind = 181, IdentityTraitKind = 178, DirectSalesKind = 253,
            EntityCappingKind = 133;

        /// <summary>Kind 52 <c>spotify.mdata.proto.AudiobookSpecifics</c> → <see cref="ShowFields.Credits"/>:
        /// <c>{1 uri, 2 contributors {2 publisher{2 name}, 3 author{2 name}, 4 narrator{2 name}} (each repeated),
        /// 3 edition, 4 total ms, 5 {1 release unix s}, 6 title, 7 subtitle}</c>. Every author is kept, in wire order.</summary>
        public static void AudiobookSpecifics(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> showUri, Staging s)
        {
            var id = Identity(s, showUri);
            if (id.IsEmpty || !showUri.StartsWith("spotify:show:"u8)) return;
            var publisher = new StringBuilder();
            var authors = new StringBuilder();
            var narrators = new StringBuilder();
            ReadOnlySpan<byte> edition = default, title = default, subtitle = default;
            long durationMs = 0, released = 0;
            for (var r = new ProtoReader(payload); r.Next();)
            {
                switch (r.Field)
                {
                    case 2 when r.Wire == 2:
                        for (var c = r.Message(); c.Next();)
                        {
                            if (c.Wire != 2) { c.Skip(); continue; }
                            var name = c.Message().Bytes(2);
                            switch (c.Field)
                            {
                                case 2: Line(publisher, name); break;
                                case 3: Line(authors, name); break;
                                case 4: Line(narrators, name); break;
                                default: break;
                            }
                        }
                        break;
                    case 3 when r.Wire == 2: edition = r.Bytes(); break;
                    case 4 when r.Wire == 0: durationMs = (long)r.Varint(); break;
                    case 5 when r.Wire == 2: released = r.Message().Varint(1); break;
                    case 6 when r.Wire == 2: title = r.Bytes(); break;
                    case 7 when r.Wire == 2: subtitle = r.Bytes(); break;
                    default: r.Skip(); break;
                }
            }
            ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Credits);
            row.BookTitle = s.AddText(title);
            row.BookSubtitle = s.AddText(subtitle);
            row.Edition = s.AddText(edition);
            row.BookPublisher = Text(s, publisher);
            row.BookAuthors = Text(s, authors);
            row.Narrators = Text(s, narrators);
            row.BookDurationMs = (int)Math.Clamp(durationMs, 0, int.MaxValue);
            row.ReleasedAt = (int)Math.Clamp(released, 0, int.MaxValue);
            s.Shows.Settle();
        }

        /// <summary>Kind 64 <c>spotify.audiobookcashier.v1.AudiobookPrice</c> → <see cref="ShowFields.Price"/>: field 1 =
        /// finalPrice (what the listener pays), field 2 = finalListPrice ("Recommended price"); each
        /// <c>{1 amount double, 2 currency, 3 formatted}</c> (map §10.3). Stored "amount\tcurrency\tformatted".</summary>
        public static void AudiobookPrice(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> showUri, Staging s)
        {
            var id = Identity(s, showUri);
            if (id.IsEmpty || !showUri.StartsWith("spotify:show:"u8)) return;
            string final = "", list = "";
            for (var r = new ProtoReader(payload); r.Next();)
            {
                if (r.Wire != 2 || r.Field is not (1 or 2)) { r.Skip(); continue; }
                double amount = 0;
                ReadOnlySpan<byte> currency = default, formatted = default;
                for (var m = r.Message(); m.Next();)
                {
                    if (m.Field == 1 && m.Wire == 1) amount = m.Double();
                    else if (m.Field == 2 && m.Wire == 2) currency = m.Bytes();
                    else if (m.Field == 3 && m.Wire == 2) formatted = m.Bytes();
                    else m.Skip();
                }
                string money = MoneyText(amount, Encoding.UTF8.GetString(currency), Encoding.UTF8.GetString(formatted));
                if (r.Field == 1) final = money; else list = money;
            }
            ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Price);
            row.PriceFinal = s.AddText(Encoding.UTF8.GetBytes(final));
            row.PriceList = s.AddText(Encoding.UTF8.GetBytes(list));
            s.Shows.Settle();
        }

        /// <summary>The Price column format; shared with the GraphQL decoder so both write identical text.</summary>
        public static string MoneyText(double amount, string currency, string formatted)
            => amount <= 0 && formatted.Length == 0 ? ""
             : amount.ToString("R", CultureInfo.InvariantCulture) + "\t" + Clean(currency) + "\t" + Clean(formatted);

        /// <summary>Kind 31 <c>spotify.podcast_paywalls.ShowAccess</c> → <see cref="ShowFields.Access"/>: the signifier
        /// text at field 15.1 ("Included in Premium"). The packed access-group enums (10, 14) are not read: nothing on the
        /// page states them and their values are unnamed.</summary>
        public static void ShowAccess(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> showUri, Staging s)
        {
            var id = Identity(s, showUri);
            if (id.IsEmpty || !showUri.StartsWith("spotify:show:"u8)) return;
            ReadOnlySpan<byte> signifier = default;
            for (var r = new ProtoReader(payload); r.Next();)
            {
                if (r.Field == 15 && r.Wire == 2) signifier = r.Message().Bytes(1);
                else r.Skip();
            }
            ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Access);
            row.Signifier = s.AddText(signifier);
            s.Shows.Settle();
        }

        /// <summary>Kind 181 <c>PreviewPlaybackTrait</c> → <see cref="ShowFields.Preview"/>: the free sample mp3 at
        /// 3.1.3.1.1 and the sample chapter uri at 4.</summary>
        public static void PreviewPlayback(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> showUri, Staging s)
        {
            var id = Identity(s, showUri);
            if (id.IsEmpty || !showUri.StartsWith("spotify:show:"u8)) return;
            ReadOnlySpan<byte> url = default, sample = default;
            for (var r = new ProtoReader(payload); r.Next();)
            {
                if (r.Field == 3 && r.Wire == 2)
                {
                    var a = r.Message().Sub(1);
                    var b = a.Sub(3);
                    var c = b.Sub(1);
                    var mp3 = c.Bytes(1);
                    if (!mp3.IsEmpty && url.IsEmpty) url = mp3;
                }
                else if (r.Field == 4 && r.Wire == 2) sample = r.Bytes();
                else r.Skip();
            }
            ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Preview);
            row.PreviewUrl = s.AddText(url);
            row.SampleUri = s.AddText(sample);
            s.Shows.Settle();
        }

        /// <summary>Kind 178 <c>IdentityTrait</c> on a show → <see cref="ShowFields.Authors"/>: every
        /// <c>5 {1 name, 2 spotify:author: uri}</c>, as "name\turi" lines in wire order.</summary>
        public static void IdentityTrait(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> showUri, Staging s)
        {
            var id = Identity(s, showUri);
            if (id.IsEmpty || !showUri.StartsWith("spotify:show:"u8)) return;
            var lines = new StringBuilder();
            for (var r = new ProtoReader(payload); r.Next();)
            {
                if (r.Field != 5 || r.Wire != 2) { r.Skip(); continue; }
                r.Message().Fields(1, 2, out var name, out var uri);
                if (name.IsEmpty) continue;
                if (lines.Length > 0) lines.Append('\n');
                lines.Append(Clean(Encoding.UTF8.GetString(name))).Append('\t').Append(Clean(Encoding.UTF8.GetString(uri)));
            }
            ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Authors);
            row.AuthorLinks = Text(s, lines);
            s.Shows.Settle();
        }

        /// <summary>Kind 253 <c>spotify.audiobookdirectsales.v1.DirectSalesPurchaseInfo</c> → <see cref="ShowFields.Sales"/>:
        /// <c>{1: 1, 2: 0, 4: "Buy audiobook", 7: ""}</c>; the label at field 4 is what the button says. Fields 1/2/7 are
        /// unnamed and not read.</summary>
        public static void DirectSales(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> showUri, Staging s)
        {
            var id = Identity(s, showUri);
            if (id.IsEmpty || !showUri.StartsWith("spotify:show:"u8)) return;
            ReadOnlySpan<byte> label = default;
            for (var r = new ProtoReader(payload); r.Next();)
            {
                if (r.Field == 4 && r.Wire == 2) label = r.Bytes();
                else r.Skip();
            }
            ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Sales);
            row.BuyLabel = s.AddText(label);
            s.Shows.Settle();
        }

        /// <summary>Kind 133 <c>ENTITY_CAPPING</c> on a chapter → <see cref="EpisodeFields.Capping"/>: <c>{1: 1}</c> =
        /// counts against capped feature 1 (AudiobookSubscription); an empty body (the sample chapter) = does not.</summary>
        public static void EntityCapping(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> episodeUri, Staging s)
        {
            var id = Identity(s, episodeUri);
            if (id.IsEmpty || !episodeUri.StartsWith("spotify:episode:"u8)) return;
            bool capped = false;
            for (var r = new ProtoReader(payload); r.Next();)
            {
                if (r.Field == 1 && r.Wire == 0) capped |= r.Varint() == 1;
                else r.Skip();
            }
            ref var row = ref s.Episodes.RowFor(id, Authority.Full, (uint)EpisodeFields.Capping);
            row.Capping = capped ? (byte)1 : (byte)2;
            s.Episodes.Settle();
        }

        /// <summary>A terminal or empty answer for one of the pages' show kinds is the group's real "none" (a podcast has
        /// no price; most books have no direct sale): stage the group EMPTY at full authority so it settles instead of
        /// riding the miss retries (the <see cref="TraitNegatives"/> rule, for shows and chapters).</summary>
        static void PagesNegatives(ReadOnlySpan<byte> response, Staging s, FetchBatch asked)
        {
            if (asked.Subject != FetchSubject.Entity || asked.Extension != 0 || asked.Count == 0) return;
            if (asked.Kind is not (EntityKind.Show or EntityKind.Episode)) return;
            Span<FetchRoute> routes = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
            int n = Api.RoutesFor(asked.Subject, asked.Kind, asked.Wanted, routes, out _);
            Span<int> kinds = stackalloc int[FetchRoutes.MaxRoutes];
            int k = Api.MetadataKinds(routes[..n], 0, kinds);
            int pages = 0;
            for (int i = 0; i < k; i++) if (PagesGroup(asked.Kind, kinds[i]) != 0) kinds[pages++] = kinds[i];
            if (pages == 0) return;

            var uris = new string[asked.Count];
            for (int i = 0; i < uris.Length; i++) uris[i] = asked.Uri(i);
            var summary = XmAnswerSummary.Read(response, uris, kinds[..pages]);
            foreach (var row in summary.Rows)
            {
                bool terminal = row.Verdict == XmAnswerSummary.Verdict.Failed && ExtendedMetadataRules.TerminalEntry(row.Status);
                if (!terminal && row.Verdict is not (XmAnswerSummary.Verdict.Omitted or XmAnswerSummary.Verdict.Empty)) continue;
                int at = Array.IndexOf(uris, row.Uri);
                if (at < 0) continue;
                var id = new StagedId(asked.Ids[at]);
                if (id.IsEmpty) continue;
                uint group = PagesGroup(asked.Kind, row.Kind);
                if (asked.Kind == EntityKind.Show) { s.Shows.RowFor(id, Authority.Full, group); s.Shows.Settle(); }
                else
                {
                    // An EMPTY capping body is the sample's "does not count"; a 404 says nothing either way → unknown.
                    ref var e = ref s.Episodes.RowFor(id, Authority.Full, group);
                    e.Capping = row.Verdict == XmAnswerSummary.Verdict.Empty ? (byte)2 : (byte)0;
                    s.Episodes.Settle();
                }
            }
        }

        /// <summary>The pages' group a kind answers for on an entity kind, or 0.</summary>
        static uint PagesGroup(EntityKind entity, int kind) => entity switch
        {
            EntityKind.Show => kind switch
            {
                AudiobookSpecificsKind => (uint)ShowFields.Credits,
                AudiobookPriceKind => (uint)ShowFields.Price,
                ShowAccessKind => (uint)ShowFields.Access,
                PreviewPlaybackKind => (uint)ShowFields.Preview,
                IdentityTraitKind => (uint)ShowFields.Authors,
                DirectSalesKind => (uint)ShowFields.Sales,
                AudiobookGenreKind => (uint)ShowFields.Genres,
                _ => 0u,
            },
            EntityKind.Episode => kind == EntityCappingKind ? (uint)EpisodeFields.Capping : 0u,
            _ => 0u,
        };

        // ── helpers ──

        static void Line(StringBuilder sb, ReadOnlySpan<byte> utf8)
        {
            if (utf8.IsEmpty) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Clean(Encoding.UTF8.GetString(utf8)));
        }

        static TextRef Text(Staging s, StringBuilder sb) => sb.Length == 0 ? default : s.AddText(Encoding.UTF8.GetBytes(sb.ToString()));

        /// <summary>One value of a line format: no tab, no newline.</summary>
        internal static string Clean(string value)
            => value.IndexOfAny(['\t', '\n', '\r']) < 0 ? value : value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
