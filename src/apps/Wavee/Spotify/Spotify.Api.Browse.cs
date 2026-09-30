// ── Spotify/Spotify.Api.Browse.cs ───────────────────────────────────────────────────────────────────────────────────
// the browse and section provider answers: a browse page WHOLE (every section page, then the single-section rule's walk),
// a browseSection band (its first page, or walked WHOLE), a homeSection band walked WHOLE, and the always-on `browse.*` /
// `home.*` lines (RCA 2026-09-30)
//
// Role: SHELL (each answer BLOCKS on an api thread) + CORE (the walk's decisions are Browse.Rules.cs's, pure)
// Spec: RCA 2026-09-30 (root causes 1-4); the official client's BrowsePage module 83320 + browseSection hook 87009 + the
//       home-v2-section page's homeSection query (`getNextPageParam`: `nextOffset > 0 ? nextOffset : end`)
//
// THE COORDINATOR'S ROUTING CALLS THESE (`Api.AnswerQuery`), and nothing else here is on a routing path:
//
//     PathfinderOp.BrowsePage     → BrowsePageAnswer(uri, offset, s, ref outcome, groups)
//     PathfinderOp.BrowseSection  → BrowseSectionAnswer(uri, s, ref outcome, groups)
//     PathfinderOp.HomeSection    → HomeSectionAnswer(uri, s, ref outcome, groups)
//
// A PAGE DEMANDS ITS WHOLE MODEL; THIS LAYER PAGES IT. `browsePage` answers ten sections a page; the walk follows
// `sections.pagingInfo.nextOffset` to its end (`BrowseWalk` — the server's cursor ends it, never "fewer than the limit
// came back"), so Music's 14 sections are 14, not 10, and no page ever pages a section list itself. Then the
// SINGLE-SECTION RULE (`BrowseSingleSection`): a page that RETURNED exactly one section — counted, never its
// `totalCount` — is that section, so `browseSection` is walked on `sections[0].uri` 20 items at a time to its end and
// lands WHOLE over the page's own band (`Decode.BrowseSectionWhole`, one row + one whole run, later in the same batch);
// an empty or refused walk leaves the page's band standing (the official fallback), and is only LOGGED — noting it into
// the outcome would retry the whole batch behind the backoff for a page that already has content to show.
//
// A SECTION DEMANDED WHOLE IS WALKED WHOLE (`SectionFields.Whole`, the drill page's ask — `FetchRoutes`' section routes).
// The section's own route is read 20 items at a time on `sectionItems.pagingInfo.nextOffset` by the SAME walk
// (`WalkSection`, `BrowseWalk`'s stop rules and page cap), every page SCANNED before anything is staged, then landed as
// ONE row + ONE whole run (`Decode.BrowseSectionWhole` / `Decode.HomeSectionWhole`: `Identity | Whole`, the card edge
// Complete, `NextOffset` where the walk ended) — so the drill grid renders what the row holds and never pages. A first
// page that is a GenericError / NotFound is a known, EMPTY band (`Decode.UnavailableSection`); a first page with no
// union stages nothing (the planner's miss review re-asks it). A browseSection ask WITHOUT Whole (a deck tile) is the
// band's first page alone (`Decode.BrowseSection`).
//
// EVERY REQUEST OF A PAGE WALK OR A WHOLE-SECTION WALK IS NOTED (`FetchOutcome.Note`): a later page that fails retryably
// fails the batch while attempts remain (the staging is thrown away and the walk re-runs), and on the last attempt
// whatever landed is committed — the planner's one retry machinery, not a second one here.
//
// THE LINES (category `fetch`, Info — always on, in wavee-*.log):
//     browse.page     uri offset status ms root sections=<returned>/<total> next kept dropped bands [uriMismatch]
//     browse.section  uri offset status ms root items=<returned>/<total> next kept dropped bands [uriMismatch]
//                                                                          (a browseSection band's first page alone)
//     browse.walk     uri offset status ms root items=<returned>/<total> next  (one per page a browseSection walk read)
//     browse.single   page section verdict=whole|fallback pages items end stop bands
//     browse.whole    section pages items end stop bands                        (a browseSection band walked whole)
//     home.walk       uri offset status ms root items=<returned>/<total> next  (one per page a homeSection walk read)
//     home.whole      section pages items end stop                              (a homeSection band walked whole)
//     browse.empty    op uri offset status ms bytes root verdict=missing|unavailable error   (op=homeSection too)
//     browse.failed   op uri offset status ms                                   (a non-2xx: nothing decoded)
// `next` / `end` read `null` (the server's explicit end), `none` (no pagingInfo) or the offset. `stop` is `end` (the
// server's cursor), `empty` (a page with no items), `cap` (`BrowseWalk.MaxPages`), `status-<n>` or the root's typename /
// `missing`. `bands` is one `<kind>:<raw>/<unsupported>` per band (`shelf`, `grid`, `related`), a dropped band (no uri)
// prefixed `dropped-`. `browse.empty` is the 200 that decoded to NO content: a body with no union (`verdict=missing`,
// `error=` the first GraphQL error's message — the miss review re-asks it) or a GenericError / NotFound union
// (`verdict=unavailable`, staged as a known empty page / band, so it is sealed and never re-asked).

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>One browse or section request: the uri and the offset in, the transport's answer out. The live answers
        /// pass the real pathfinder calls; a test passes canned bodies — the walk, the rule and the decode are the same
        /// code.</summary>
        public delegate Result BrowseFetch(string uri, int offset);

        static readonly BrowseFetch s_browsePageFetch = static (uri, offset) => BrowsePage(uri, offset, CancellationToken.None);
        static readonly BrowseFetch s_browseSectionFetch = static (uri, offset) => BrowseSection(uri, offset, CancellationToken.None);
        static readonly BrowseFetch s_homeSectionFetch =
            static (uri, offset) => HomeSection(uri, LocalTimeZone, offset, CancellationToken.None);

        /// <inheritdoc cref="BrowsePageAnswer(string,int,Staging,ref FetchOutcome,uint,BrowseFetch,BrowseFetch)"/>
        public static void BrowsePageAnswer(string pageUri, int sectionOffset, Staging s, ref FetchOutcome outcome, uint groups = 0)
            => BrowsePageAnswer(pageUri, sectionOffset, s, ref outcome, groups, s_browsePageFetch, s_browseSectionFetch);

        /// <summary>A BROWSE PAGE, WHOLE (file header): every section page from <paramref name="sectionOffset"/> to the
        /// server's end, each decoded into <paramref name="s"/> as it arrives and noted into <paramref name="outcome"/>;
        /// then, for a walk that began at the first page and RETURNED one section, that section walked and landed whole.
        /// BLOCKS.</summary>
        public static void BrowsePageAnswer(string pageUri, int sectionOffset, Staging s, ref FetchOutcome outcome, uint groups,
                                            BrowseFetch fetchPage, BrowseFetch fetchSection)
        {
            byte[] page = Encoding.UTF8.GetBytes(pageUri);
            var tally = Log.IsEnabled(WaveeLogLevel.Info) ? new List<Decode.BrowseBandTally>(16) : null;
            int start = Math.Max(0, sectionOffset), offset = start, pages = 0, returned = 0;
            StagedId lone = default;
            while (true)
            {
                long sent = Stopwatch.GetTimestamp();
                Result result = fetchPage(pageUri, offset);
                outcome.Note(in result, groups);
                if (!result.Ok) { LogFailed("browsePage", pageUri, offset, in result, sent); break; }   // what landed stands
                pages++;
                tally?.Clear();
                var answer = Decode.BrowsePage(result.Bytes, page, offset, s, tally);
                LogBrowse("browse.page", "browsePage", pageUri, offset, in result, sent, in answer, tally);
                if (answer.Root != Decode.BrowseRoot.Content) break;
                returned += answer.SectionsReturned;
                if (answer.SectionsReturned == 1 && answer.BandsKept == 1) lone = s.Sections[answer.BandStart].Id;
                offset = BrowseWalk.Next(offset, answer.SectionsNext, pages);
                if (offset == BrowseWalk.Stop) break;
            }

            // The rule is the WHOLE page's: only a walk that began at the first page can have counted every section.
            if (start != 0 || !BrowseSingleSection.Applies(returned) || lone.Text.IsEmpty) return;
            SingleSection(pageUri, Encoding.UTF8.GetString(s.Utf8(lone.Text)), s, fetchSection, tally);
        }

        /// <summary>THE SINGLE-SECTION WALK (<see cref="BrowseSingleSection"/>): the page's lone section walked from offset 0
        /// to the server's end (<see cref="WalkSection"/>) — then, when it produced items, landed whole over the page's band;
        /// otherwise the band stands. NEVER noted into the page's outcome: the page's own band is this walk's fallback, so
        /// a refused read must not retry a page that already has content to show.</summary>
        static void SingleSection(string pageUri, string sectionUri, Staging s, BrowseFetch fetch, List<Decode.BrowseBandTally>? tally)
        {
            var unnoted = new FetchOutcome();
            var read = WalkSection(SectionRoute.Browse, sectionUri, fetch, ref unnoted, 0);
            bool whole = BrowseSingleSection.UseWalk(read.Items);
            tally?.Clear();
            if (whole) Decode.BrowseSectionWhole(read.Bodies, Encoding.UTF8.GetBytes(sectionUri), read.End, s, tally);
            if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
            Log.Event(WaveeLogLevel.Info, "fetch", "browse.single", "", null, -1, null,
                WaveeLogField.Of("page", pageUri), WaveeLogField.Of("section", sectionUri),
                WaveeLogField.Of("verdict", whole ? "whole" : "fallback"), WaveeLogField.Of("pages", read.Bodies.Count),
                WaveeLogField.Of("items", read.Items), WaveeLogField.Of("end", Cursor(read.End)), WaveeLogField.Of("stop", read.Stop),
                WaveeLogField.Of("bands", Bands(tally)));
        }

        /// <inheritdoc cref="BrowseSectionAnswer(string,Staging,ref FetchOutcome,uint,BrowseFetch)"/>
        public static void BrowseSectionAnswer(string sectionUri, Staging s, ref FetchOutcome outcome, uint groups = 0)
            => BrowseSectionAnswer(sectionUri, s, ref outcome, groups, s_browseSectionFetch);

        /// <summary>A <c>browseSection</c> band, keyed on <paramref name="sectionUri"/> — the uri ASKED for (the official
        /// answer names none: RCA 2026-09-30, root cause 1). With <see cref="SectionFields.Whole"/> among
        /// <paramref name="groups"/> — the drill page's ask — the band is WALKED to the server's end and landed whole (file
        /// header); without it — a deck tile's (the Charts band, Home's featured charts) — it is the first page alone
        /// (<c>FetchSubject.BrowseSection</c>'s one-page route). Every request is noted into <paramref name="outcome"/>.
        /// BLOCKS.</summary>
        public static void BrowseSectionAnswer(string sectionUri, Staging s, ref FetchOutcome outcome, uint groups,
                                               BrowseFetch fetch)
        {
            byte[] uri = Encoding.UTF8.GetBytes(sectionUri);
            var tally = Log.IsEnabled(WaveeLogLevel.Info) ? new List<Decode.BrowseBandTally>(1) : null;
            if ((groups & (uint)SectionFields.Whole) == 0)
            {
                long sent = Stopwatch.GetTimestamp();
                Result result = fetch(sectionUri, 0);
                outcome.Note(in result, groups);
                if (!result.Ok) { LogFailed("browseSection", sectionUri, 0, in result, sent); return; }
                var answer = Decode.BrowseSection(result.Bytes, uri, 0, s, tally);
                LogBrowse("browse.section", "browseSection", sectionUri, 0, in result, sent, in answer, tally);
                return;
            }

            var read = WalkSection(SectionRoute.Browse, sectionUri, fetch, ref outcome, groups);
            if (read.Bodies.Count > 0) Decode.BrowseSectionWhole(read.Bodies, uri, read.End, s, tally);
            else if (read.Definite) Decode.UnavailableSection(uri, SectionKind.BrowseShelf, s);
            LogWhole("browse.whole", sectionUri, in read, tally);
        }

        /// <inheritdoc cref="HomeSectionAnswer(string,Staging,ref FetchOutcome,uint,BrowseFetch)"/>
        public static void HomeSectionAnswer(string sectionUri, Staging s, ref FetchOutcome outcome, uint groups = 0)
            => HomeSectionAnswer(sectionUri, s, ref outcome, groups, s_homeSectionFetch);

        /// <summary>A <c>homeSection</c> band WHOLE — only the drill page asks a Home band on its own, and it demands the
        /// whole list: walked 20 items at a time to the server's end (<see cref="WalkSection"/>) and landed as one row + one
        /// whole run keyed on the uri ASKED for (<see cref="Decode.HomeSectionWhole"/>); a GenericError / NotFound first page
        /// is a known, EMPTY band. Every request is noted into <paramref name="outcome"/>. BLOCKS.</summary>
        public static void HomeSectionAnswer(string sectionUri, Staging s, ref FetchOutcome outcome, uint groups,
                                             BrowseFetch fetch)
        {
            var read = WalkSection(SectionRoute.Home, sectionUri, fetch, ref outcome, groups);
            byte[] uri = Encoding.UTF8.GetBytes(sectionUri);
            if (read.Bodies.Count > 0) Decode.HomeSectionWhole(read.Bodies, uri, read.End, s);
            else if (read.Definite) Decode.UnavailableSection(uri, SectionKind.HomeGeneric, s);
            LogWhole("home.whole", sectionUri, in read, null);
        }

        /// <summary>Which section route a walk reads.</summary>
        enum SectionRoute : byte { Browse, Home }

        /// <summary>What ONE section walk read (<see cref="WalkSection"/>).</summary>
        struct SectionRead
        {
            /// <summary>Every CONTENT page read, in order — a page with no items included (it ended the walk).</summary>
            public List<byte[]> Bodies;
            /// <summary>The items across <see cref="Bodies"/>.</summary>
            public int Items;
            /// <summary>Where the walk ended: <see cref="SectionPaging.Complete"/> when the server's cursor (or an empty page)
            /// did, else the offset it would have asked next — the landed row's <c>NextOffset</c>, so the ledger never
            /// claims more than it read.</summary>
            public int End;
            /// <summary>Why it ended (the file header's <c>stop</c>).</summary>
            public string Stop;
            /// <summary>The FIRST page was a GenericError / NotFound: the server's definite "nothing here".</summary>
            public bool Definite;
        }

        /// <summary>THE SECTION WALK: <paramref name="route"/> on <paramref name="sectionUri"/> from offset 0, 20 items a
        /// page, each page SCANNED (never staged) and noted into <paramref name="outcome"/> with <paramref name="groups"/>,
        /// following <c>sectionItems.pagingInfo.nextOffset</c> until <see cref="BrowseWalk.Next"/> says stop — the server's
        /// cursor (null, absent, or not past the request), the <see cref="BrowseWalk.MaxPages"/> cap — or a page answers
        /// with no items, fails, or is not content. One <c>browse.walk</c> / <c>home.walk</c> line per page.</summary>
        static SectionRead WalkSection(SectionRoute route, string sectionUri, BrowseFetch fetch, ref FetchOutcome outcome, uint groups)
        {
            bool home = route == SectionRoute.Home;
            string op = home ? "homeSection" : "browseSection";
            string line = home ? "home.walk" : "browse.walk";
            var read = new SectionRead { Bodies = new List<byte[]>(4), End = SectionPaging.NoCursor, Stop = "end" };
            int offset = 0;
            while (true)
            {
                long sent = Stopwatch.GetTimestamp();
                Result result = fetch(sectionUri, offset);
                outcome.Note(in result, groups);
                if (!result.Ok)
                {
                    LogFailed(op, sectionUri, offset, in result, sent);
                    read.End = offset;
                    read.Stop = "status-" + result.Status.ToString(CultureInfo.InvariantCulture);
                    return read;
                }
                var scan = home ? Decode.ScanHomeSection(result.Bytes) : Decode.ScanBrowseSection(result.Bytes);
                LogBrowse(line, op, sectionUri, offset, in result, sent, in scan, null, bands: false);
                if (scan.Root != Decode.BrowseRoot.Content)
                {
                    read.Definite = read.Bodies.Count == 0 && scan.Definite;
                    read.End = offset;
                    read.Stop = scan.RootType ?? "missing";
                    return read;
                }
                read.Bodies.Add(result.Body);
                read.Items += scan.ItemsReturned;
                if (scan.ItemsReturned == 0) { read.End = SectionPaging.Complete; read.Stop = "empty"; return read; }
                int next = BrowseWalk.Next(offset, scan.ItemsNext, read.Bodies.Count);
                if (next == BrowseWalk.Stop)
                {
                    bool served = BrowseWalk.EndedByServer(offset, scan.ItemsNext);
                    read.End = served ? SectionPaging.Complete : scan.ItemsNext;
                    read.Stop = served ? "end" : "cap";
                    return read;
                }
                offset = next;
            }
        }

        /// <summary>The always-on line for a section walked WHOLE (<c>browse.whole</c> / <c>home.whole</c>).</summary>
        static void LogWhole(string eventId, string sectionUri, in SectionRead read, List<Decode.BrowseBandTally>? tally)
        {
            if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
            Log.Event(WaveeLogLevel.Info, "fetch", eventId, "", null, -1, null,
                WaveeLogField.Of("section", sectionUri), WaveeLogField.Of("pages", read.Bodies.Count),
                WaveeLogField.Of("items", read.Items), WaveeLogField.Of("end", Cursor(read.End)), WaveeLogField.Of("stop", read.Stop),
                WaveeLogField.Of("bands", Bands(tally)));
        }

        /// <summary>The always-on line for ONE decoded browse answer (file header): <c>browse.page</c> /
        /// <c>browse.section</c> / a walk's page line for content, <c>browse.empty</c> for a 200 that carried none. Api
        /// worker thread; built only when Info passes.</summary>
        static void LogBrowse(string eventId, string op, string uri, int offset, in Result result, long sent,
                              in Decode.BrowseAnswer answer, List<Decode.BrowseBandTally>? tally, bool bands = true)
        {
            if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
            long ms = (long)Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
            string root = answer.RootType ?? "none";
            if (answer.Root != Decode.BrowseRoot.Content)
            {
                Log.Event(WaveeLogLevel.Info, "fetch", "browse.empty", "", null, -1, null,
                    WaveeLogField.Of("op", op), WaveeLogField.Of("uri", uri), WaveeLogField.Of("offset", offset),
                    WaveeLogField.Of("status", result.Status), WaveeLogField.Of("ms", ms),
                    WaveeLogField.Of("bytes", result.Body.Length), WaveeLogField.Of("root", root),
                    WaveeLogField.Of("verdict", answer.Definite ? "unavailable" : "missing"),
                    WaveeLogField.Of("error", answer.Error ?? ""));
                return;
            }
            bool page = op == "browsePage";
            var fields = new List<WaveeLogField>(12)
            {
                WaveeLogField.Of("uri", uri),
                WaveeLogField.Of("offset", offset),
                WaveeLogField.Of("status", result.Status),
                WaveeLogField.Of("ms", ms),
                WaveeLogField.Of("root", root),
                page
                    ? WaveeLogField.Of("sections", Ratio(answer.SectionsReturned, answer.SectionsTotal))
                    : WaveeLogField.Of("items", Ratio(answer.ItemsReturned, answer.ItemsTotal)),
                WaveeLogField.Of("next", Cursor(page ? answer.SectionsNext : answer.ItemsNext)),
            };
            if (bands)
            {
                fields.Add(WaveeLogField.Of("kept", answer.BandsKept));
                fields.Add(WaveeLogField.Of("dropped", answer.BandsDropped));
                fields.Add(WaveeLogField.Of("bands", Bands(tally)));
            }
            if (answer.UriMismatch) fields.Add(WaveeLogField.Of("uriMismatch", true));
            Log.Event(WaveeLogLevel.Info, "fetch", eventId, "", null, -1, null,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fields));
        }

        /// <summary>The always-on line for a browse or section request that did not answer 2xx — nothing decoded. Every
        /// noted walk has noted it into the outcome (the planner decides retry / deliver); the single-section walk has not
        /// (it falls back).</summary>
        static void LogFailed(string op, string uri, int offset, in Result result, long sent)
        {
            if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
            Log.Event(WaveeLogLevel.Info, "fetch", "browse.failed", "", null, -1, null,
                WaveeLogField.Of("op", op), WaveeLogField.Of("uri", uri), WaveeLogField.Of("offset", offset),
                WaveeLogField.Of("status", result.Status),
                WaveeLogField.Of("ms", (long)Stopwatch.GetElapsedTime(sent).TotalMilliseconds));
        }

        static string Ratio(int returned, int total)
            => returned.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);

        /// <summary>A cursor as the lines write it: <c>null</c> (the server's end), <c>none</c> (no pagingInfo), or the offset.</summary>
        static string Cursor(int cursor)
            => cursor == SectionPaging.Complete ? "null"
             : cursor == SectionPaging.NoCursor ? "none"
             : cursor.ToString(CultureInfo.InvariantCulture);

        /// <summary>The bands as the lines write them: <c>&lt;kind&gt;:&lt;raw&gt;/&lt;unsupported&gt;</c>, comma-separated,
        /// a dropped band prefixed <c>dropped-</c>.</summary>
        static string Bands(List<Decode.BrowseBandTally>? tally)
        {
            if (tally is not { Count: > 0 }) return "";
            var text = new StringBuilder(tally.Count * 16);
            for (int i = 0; i < tally.Count; i++)
            {
                var band = tally[i];
                if (i > 0) text.Append(',');
                if (!band.Kept) text.Append("dropped-");
                text.Append(((SectionKind)band.Kind) switch
                {
                    SectionKind.BrowseCategoryGrid => "grid",
                    SectionKind.BrowseRelated => "related",
                    _ => "shelf",
                });
                text.Append(':').Append(band.Raw.ToString(CultureInfo.InvariantCulture))
                    .Append('/').Append(band.Unsupported.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }
    }
}
