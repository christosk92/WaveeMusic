// ── Entities/Entities.Fake.Podcast.cs ──────────────────────────────────────────────────────────────────────────────
// SEED-SURFACES for the podcast rework: the three VISITS the show reader's head switches on
//
// Role: CORE
// Owner: F (podcast rework, wave P1)
// Spec: docs/plans/wavee/podcast-show-rework-implementation.md §9 (the seed), §5.1 (the columns), §6.1 (readiness)
//
// A partial of `AlbumSeed` (Entities.Fake.Album.cs): its `StageShows` owns the show loop and calls in here once per
// show (`StagePodcastFacts`), once per resident episode (`StagePodcastEpisode`) and once after the loop
// (`StageTrailer`). The same rules as every seed partial: rows go through `Staging` at `Authority.Seed`, every value is
// a pure function of the fixture index and `now0` (no clock, no `Random`), and nothing publishes.
//
// WHAT IT SEEDS (the three first screens are told apart by the COLUMNS alone — `Show.Rules` derives the visit):
//   · sh0 — SERIAL, RETURNING. `Order = Sequential`; 14 episodes numbered 14 → 1 (newest first); 1-8 played, 9 at 46 %
//     and played 9 days before now0 — the show's last play; 13 and 14 were published after it ("new since you were
//     here"); a trailer episode row OUTSIDE the membership; ★ 4.8 from 12,431; an amber tone.
//   · sh1 — EPISODIC, NEW. No progress anywhere; explicit on the even-numbered episodes, and so the show; ★ 4.6.
//   · sh2 — VIDEO + EXCLUSIVE, CAUGHT UP. Every episode played (each a day after it came out, so nothing is new since);
//     one of them `Paywalled | PreviewOnly | Unplayable` (sh2e3); the listener's own five stars.
//   · sh4 — EPISODIC, NEW EPISODES (podcast pages v2 "New episodes"): one a month across two years; the third to the seventh newest played (the last
//     play a day after the third came out), the two newest out since then ("new since your last visit": the visit is pinned at that last play),
//     the oldest five unplayed, nothing in progress.
//   · sh5 — EPISODIC, IN PROGRESS (v2 "In progress"): #2 at 40 % (played 3 days ago, the resume), #4 at 70 %, the older ones played.
//   · sh6 — SERIAL, FIRST VISIT (v2 "First visit, serial show"): `Order = Sequential`, no progress anywhere, a trailer (`spotify:episode:sh6trailer`).
//   · sh3, sh7, sh8 — ch 09's cards as they were (sh3 keeps its two degraded ones, one "continue" card at a third). Facts and
//     Rating are answered EMPTY — episodic, no trailer, no public rating — known, so the show page's `ShowFields.All`
//     demand never reaches a provider offline (G-260), and empty, so the rail paints nothing it was not given.
//
// LATER WAVES HOOK IN HERE, each once its edge or store exists: six topics on sh0 (`Edges.ShowTopics`) and "more like
// this" = the other shows (`Edges.ShowSimilar`) — P4; four cross-show recommended episodes (`Edges.EpisodeRecommended`)
// — P5; six chapters on sh0 #9 (`Edges.EpisodeChapters`) and the bundled transcript — P6; the three-comment thread — P7
// (the transcript and comment stores take their fetch as a delegate, the `Lyrics.Boot` idiom).

using System.Globalization;

namespace Wavee;

public static partial class Entities
{
    static partial class AlbumSeed
    {
        // ── the visit fixtures (podcast plan §9) ─────────────────────────────────────────────────────────────────────

        /// <summary>sh0 the returning serial · sh1 the new visitor · sh2 the caught-up listener.</summary>
        const int SerialShow = 0, NewShow = 1, CaughtUpShow = 2;
        /// <summary>The podcast pages v2 states: new episodes since the last visit · episodes in progress · a serial's first visit.</summary>
        const int FreshShow = 4, ProgressShow = 5, ColdSerialShow = 6;
        /// <summary>sh4: one episode a month; the newest <see cref="FreshCount"/> came out after the last play; the next five are played.</summary>
        const int FreshCount = 2, FreshPlayed = 5;
        /// <summary>sh0's membership: numbered 14 → 1, newest first.</summary>
        const int SerialEpisodes = 14;
        /// <summary>sh0: episodes 1..<see cref="LastPlayed"/> are played; <see cref="Resume"/> is in progress at
        /// <see cref="ResumePercent"/> %, played <see cref="LastPlayAgo"/> before now0 — the show's newest play.</summary>
        const int LastPlayed = 8, Resume = 9, ResumePercent = 46;
        const long LastPlayAgo = 9 * Day;
        /// <summary>sh2's one paywalled, preview-only episode (a resident index).</summary>
        const int PaywalledEpisode = 3;
        /// <summary>sh0's amber <c>backgroundTintedBase</c>, 0xAARRGGBB.</summary>
        const uint SerialTone = 0xFF9A6424;
        /// <summary>sh9, "The Long Book": an audiobook of a hundred chapters (the realistic maximum the audiobook page's contents are measured on),
        /// chapters 1-30 heard, 31 under way.</summary>
        const int LongBookChapters = 100, LongBookHeard = 30;
        /// <summary>sh9's chapter titles as publishers send them: real titles (a book's parts, then its chapters, then back matter), and the generic ones
        /// the page must rename ("Track 17", "07", an empty title: shown as "Chapter N").</summary>
        static string BookChapterTitle(int number) => number switch
        {
            1 => "Opening Credits",
            2 => "Epigraph",
            17 or 18 or 19 => "Track " + number.ToString(CultureInfo.InvariantCulture),
            44 => number.ToString("D2", CultureInfo.InvariantCulture),
            61 => "",
            LongBookChapters - 1 => "Acknowledgements",
            LongBookChapters => "End Credits",
            _ => s_bookChapterTitles[(number - 3) % s_bookChapterTitles.Length]
                 + (number - 3 >= s_bookChapterTitles.Length ? ", Part " + ((number - 3) / s_bookChapterTitles.Length + 1).ToString(CultureInfo.InvariantCulture) : ""),
        };

        static readonly string[] s_bookChapterTitles =
        [
            "The Lighthouse at Vell", "A Ledger of Small Debts", "Salt and Iron", "The Cartographer's Daughter", "What the Tide Kept",
            "Winter Crossing", "The Long Table", "Letters Never Sent", "An Inventory of Storms", "The Glassblower", "North of Everything",
            "A House With Two Doors", "The Second Harvest", "Lamplight", "The Archivist", "Quiet Water", "Ash and Orchard", "The Night Ferry",
            "Borrowed Time", "The Weathervane", "Under the Old Bridge", "Thaw", "The Clockmaker's Apprentice", "Home, Eventually",
        ];

        /// <summary>sh0's newest part (sh0e0): three hours.</summary>
        const int LongEpisodeMs = 3 * 3_600_000;

        /// <summary>sh0's trailer: <c>spotify:episode:sh0trailer</c>.</summary>
        static string TrailerUri(int sh) => ShowUri(sh).Replace(":show:", ":episode:", StringComparison.Ordinal) + "trailer";

        /// <summary>The show-level Facts and Rating for EVERY seeded show (the file header's table).</summary>
        static void StagePodcastFacts(Staging s, ref StagedShow show, int sh)
        {
            show.Known |= (uint)(ShowFields.Facts | ShowFields.Rating | ShowFields.Appearance | ShowFields.Topics | ShowFields.Html);
            show.HtmlDescription = show.Description;
            if (sh == SerialShow) show.Topics = Text(s, "Culture\nStories\nSociety\nHistory\nInterviews\nIdeas");
            show.Order = (byte)(sh is SerialShow or ColdSerialShow ? ConsumptionOrder.Sequential : ConsumptionOrder.Episodic);
            switch (sh)
            {
                case SerialShow:
                    show.Trailer = Text(s, TrailerUri(sh));
                    show.Flags = (uint)ShowFlags.CanRate;
                    show.RatingX100 = 480;
                    show.RatingCount = 12_431;
                    show.Tone = SerialTone;
                    break;
                case NewShow:
                    show.Flags = (uint)(ShowFlags.Explicit | ShowFlags.CanRate);
                    show.RatingX100 = 460;
                    show.RatingCount = 3_208;
                    break;
                case LongBook:
                    show.Flags = (uint)ShowFlags.Audiobook;
                    show.Order = (byte)ConsumptionOrder.Sequential;
                    break;
                case CaughtUpShow:
                    show.Flags = (uint)(ShowFlags.Video | ShowFlags.Exclusive | ShowFlags.CanRate);
                    show.RatingX100 = 430;
                    show.RatingCount = 2_046;
                    show.MyRating = 5;
                    break;
                case FreshShow:
                    show.Topics = Text(s, "Science\nEducation");
                    show.Flags = (uint)(ShowFlags.Video | ShowFlags.CanRate);
                    show.RatingX100 = 490;
                    show.RatingCount = 12_579;
                    show.Tone = 0xFF8A6A1C;
                    break;
                case ColdSerialShow:
                    show.Topics = Text(s, "True crime\nDocumentary");
                    show.Trailer = Text(s, TrailerUri(sh));
                    show.Flags = (uint)ShowFlags.CanRate;
                    show.RatingX100 = 470;
                    show.RatingCount = 8_204;
                    show.Tone = 0xFF1C5560;
                    break;
            }
        }

        /// <summary>One resident episode's podcast columns. Runs AFTER the loop set the duration and the publish date,
        /// which the progress and the last-play stamp are derived from. The number is the title's own "#N".</summary>
        static void StagePodcastEpisode(Staging s, ref StagedEpisode ep, int sh, int i, int total, long now0)
        {
            int number = total - i;
            ep.Number = (ushort)number;
            ep.Kind = (byte)EpisodeKind.Full;
            ep.Season = (ushort)(sh == SerialShow ? 1 : 0);
            ep.HtmlDescription = ep.Description;
            ep.Flags = 0;
            ep.ProgressMs = 0;
            ep.PlayedAt = 0;
            switch (sh)
            {
                case SerialShow:
                    if (number <= LastPlayed) Played(ref ep, now0 - LastPlayAgo - (Resume - number) * Day);   // one a day before
                    else if (number == Resume)
                    {
                        ep.ProgressMs = ep.DurationMs / 100 * ResumePercent;
                        ep.PlayedAt = (int)(now0 - LastPlayAgo);
                    }
                    break;
                case NewShow:
                    if (number % 2 == 0) ep.Flags = (uint)EpisodeFlags.Explicit;
                    // A bonus episode (the rows' Bonus tag), with a transcript (their transcript mark).
                    if (i == 1) { ep.Kind = (byte)EpisodeKind.Bonus; ep.Flags |= (uint)EpisodeFlags.HasTranscript; }
                    break;
                case LongBook:
                    ep.Flags = (uint)EpisodeFlags.AudiobookChapter;
                    ep.Title = Text(s, BookChapterTitle(number));
                    if (number <= LongBookHeard) Played(ref ep, now0 - (LongBookHeard - number + 2) * Day);
                    else if (number == LongBookHeard + 1) { ep.ProgressMs = ep.DurationMs / 2; ep.PlayedAt = (int)(now0 - Day); }
                    break;
                case CaughtUpShow:
                    // The wire sets PreviewOnly only on an unplayable episode (Spotify.Decode.Podcast.cs EpisodeDetail): the fake says the same.
                    ep.Flags = (uint)(i == PaywalledEpisode
                        ? EpisodeFlags.Video | EpisodeFlags.Paywalled | EpisodeFlags.PreviewOnly | EpisodeFlags.Unplayable
                        : EpisodeFlags.Video);
                    ep.Still = Text(s, Cover(sh + 11 + i));          // each video episode's own still (the show's cover is its square one)
                    Played(ref ep, ep.PublishedAt + Day);
                    break;
                case FreshShow:
                    ep.PublishedAt = (int)(now0 - i * 30 * Day - sh * Day);
                    ep.Flags = i % 3 == 0 ? (uint)EpisodeFlags.Video : 0u;
                    if (i >= FreshCount && i < FreshCount + FreshPlayed)
                        Played(ref ep, now0 - FreshCount * 30 * Day - sh * Day + Day - (i - FreshCount) * Day);
                    if (i == FreshCount) PodcastPages.PinVisit(ShowUri(sh), (now0 - FreshCount * 30 * Day - sh * Day + Day) * 1000L);
                    break;
                case ProgressShow:
                    if (i == 1) { ep.ProgressMs = ep.DurationMs / 10 * 4; ep.PlayedAt = (int)(now0 - 3 * Day); }
                    else if (i == 3) { ep.ProgressMs = ep.DurationMs / 10 * 7; ep.PlayedAt = (int)(now0 - 10 * Day); }
                    else if (i >= 4) Played(ref ep, ep.PublishedAt + Day);
                    break;
                case ColdSerialShow:
                    break;
                default:
                    // ch 09's one "continue listening" card at a third, played two days after it came out
                    if (i == 1)
                    {
                        ep.ProgressMs = ep.DurationMs / 3;
                        ep.PlayedAt = (int)(ep.PublishedAt + 2 * Day);
                    }
                    break;
            }

            // The serial's newest part is its long one: three hours (a hundred chapters and ~2,500 transcript lines in the fixtures,
            // Episode.Fixtures.cs), the realistic maximum the episode page's long lists are measured on.
            if (sh == SerialShow && number == SerialEpisodes) ep.DurationMs = LongEpisodeMs;

            if (sh == SerialShow && number == Resume)
            {
                ep.Flags |= (uint)EpisodeFlags.HasTranscript;
                ep.TranscriptUrl = Text(s, "fake:transcript:serial-resume");
                ep.TranscriptLanguage = Text(s, "en");
                ep.TranscriptReadAlong = true;
                // The show notes as the wire sends them: HTML paragraphs, a link, bold, italic and a list (the episode page's About renders them).
                ep.HtmlDescription = Text(s, "<p>Why do names vanish ten seconds after you hear them? We go through what a memory <b>physically</b> is, "
                    + "how it gets in, and how it gets lost.</p><p>The <i>Baker-baker paradox</i>, sleep, and what to do by Monday. "
                    + "More in <a href=\"spotify:show:sh1\">our sister show</a>.</p><ul><li>Encoding and attention</li><li>Sleep and consolidation</li>"
                    + "<li>False memories &amp; confident mistakes</li></ul>");
            }

            // sh5e1 (in progress) writes its chapters into its show notes, as many shows do, and Spotify lists none for it (the chapter fixture
            // answers empty for an episode whose notes carry them, Episode.Fixtures.cs): the episode page builds its chapters from the notes.
            if (sh == ProgressShow && i == 1)
            {
                // The notes as plain lines (the convention RichTextBlocks.Split reads: one timestamp line each).
                ep.Description = Text(s, "Five habits, one week, and what stuck.\n\nCHAPTERS(00:00:00) Cold open\n(00:02:40) Why habits fail on Wednesday\n"
                    + "(00:07:15) The two-minute rule\n(00:12:30) Stacking and cues\n(00:17:05) What we kept\n\nThanks for listening.");
                ep.HtmlDescription = ep.Description;
            }

            static void Played(ref StagedEpisode row, long at)
            {
                row.ProgressMs = row.DurationMs;
                row.ExplicitCompleted = true;
                row.PlayedAt = (int)at;
            }
        }

        /// <summary>sh0's trailer: its own episode row (kind Trailer, two minutes, unplayed, published before episode 1),
        /// OUTSIDE the membership — the rail's trailer button and the "start here" door address it through
        /// <see cref="ShowTable.Trailer"/>, and the episode count stays the fourteen numbered ones.</summary>
        static void StageTrailer(Staging s, long now0)
        {
            StageTrailer(s, now0, SerialShow);
            StageTrailer(s, now0, ColdSerialShow);
        }

        static void StageTrailer(Staging s, long now0, int sh)
        {
            string name = s_showNames[sh];
            ref var ep = ref s.Episodes.RowFor(Text(s, TrailerUri(sh)), Authority.Seed, (uint)EpisodeFields.All);
            ep.Title = Text(s, "Hear what " + name + " is about");
            ep.Image = Text(s, Cover(sh + 2));
            ep.Description = Text(s, "Two minutes on what " + name + " is, and why it is best heard from episode one.");
            ep.HtmlDescription = ep.Description;
            ep.DurationMs = 2 * 60_000;
            ep.PublishedAt = (int)(now0 - (EpisodesIn(sh) * 7 + 3) * Day);
            ep.Kind = (byte)EpisodeKind.Trailer;
            ep.ShowUri = Text(s, ShowUri(sh));
        }
    }
}
