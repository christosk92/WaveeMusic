// ── Entities/Entities.Fake.Audiobook.cs ────────────────────────────────────────────────────────────────────────────
// SEED-SURFACES for the audiobook page (podcast pages v2): eight books, `spotify:show:ab0` .. `ab7`
//
// A partial of `AlbumSeed` (Entities.Fake.Album.cs): `StageShows` calls `StageAudiobooks` once and `LandShows` calls `LandAudiobooks` once. The
// same rules as every seed partial: rows go through `Staging` at `Authority.Seed`, every value is a pure function of the fixture index and `now0`.
//
// A book is a show (`ShowFlags.Audiobook`) whose members are its chapters (`EpisodeFlags.AudiobookChapter`) in BOOK order, plus a sample chapter
// (`EpisodeKind.Trailer`) OUTSIDE the membership, addressed by the show's trailer column, as Spotify's SHOW_V4 field 83 does. The show row carries
// what Wavee's tables hold today: the title as the wire sends it ("Title: Subtitle"), the publisher column = the FIRST AUTHOR (SHOW_V4 field 66 is
// the author), and the plain description opening with the "Author(s): …\nNarrator(s): …" header. What the tables cannot hold yet (the real
// publisher, the edition, the language, the price pair, similar books) goes to `AudiobookFactsStore`, the seam the data lane fills for real.
//
//   · ab0 — IN PROGRESS, named chapters: "The Quiet Engine: How Small Teams Build Things That Last" by Imogen Hartley, narrated by Daniel Osei;
//     19 chapters (Opening credits … Acknowledgements, End credits), 1-8 finished, chapter 9 at 1,100 s; a 5-minute sample; ★ 4.8 (1,204); in
//     Your Library; included in Premium.
//   · ab1 — NOT STARTED, no chapter titles: "Track 1" … "Track 14", one empty and one "07"; two narrators; NOT included (19.99 EUR, list 24.99).
//   · ab2 — FINISHED: every chapter played (the last 20 days before now0); in Your Library; no rating of the listener's own.
//   · ab3-ab7 — thin books for "More by the author and similar books" (three named chapters each, not started).

using System.Collections.Immutable;
using System.Globalization;

namespace Wavee;

public static partial class Entities
{
    static partial class AlbumSeed
    {
        /// <summary>One fixture book. <see cref="Done"/> chapters are finished; <see cref="ResumeAt"/> (seconds) is the resume point of chapter
        /// <see cref="Done"/> (0 = none).</summary>
        sealed record FakeBook(
            string Title, string[] Authors, string[] Narrators, string Publisher, string Genres, int Cover, uint Tone, int RatingX100, int RatingCount,
            DateOnly Released, (string Name, int Seconds)[] Chapters, int Done, int ResumeAt, bool Included, int SampleMinutes, string Blurb,
            int[] Similar);

        const int AudiobookCount = 8;

        static readonly FakeBook[] s_books =
        [
            new("The Quiet Engine: How Small Teams Build Things That Last", ["Imogen Hartley"], ["Daniel Osei"], "Fernhill Audio", "Business\nManagement",
                5, 0xFF6F2B24, 480, 1_204, new DateOnly(2024, 3, 14),
                [("Opening credits", 38), ("Introduction: the nine-person company", 842), ("Small on purpose", 1710), ("The cost of one more hire", 1865),
                 ("Meetings are a tax", 1522), ("Writing things down", 1980), ("Shipping on a calendar", 2105), ("Saying no to good ideas", 1764),
                 ("Hiring slowly", 2240), ("When someone leaves", 1688), ("Money, runway and sleep", 2012), ("Customers who stay", 1856),
                 ("Tools that outlive the team", 1490), ("Growing without getting bigger", 2130), ("The second decade", 1745), ("Handing it over", 1602),
                 ("Epilogue: what lasts", 930), ("Acknowledgements", 295), ("End credits", 46)],
                Done: 8, ResumeAt: 1100, Included: true, SampleMinutes: 5,
                Blurb: "Imogen Hartley ran a nine-person software company for nineteen years and never let it get bigger. This is her account of how: what the team refused to do, what it wrote down, and what happened each time someone left.\n\nPart memoir, part manual. The chapters stand on their own, and the last three are about handing a company over without breaking it.",
                Similar: [2, 3, 4, 5, 6, 7]),
            new("Harrow Light: A Novel", ["Elias Marr"], ["Amara Lind", "Tom Hale"], "Northgate Audio", "Fiction\nMystery",
                9, 0xFF1D3B34, 440, 3_870, new DateOnly(2023, 10, 2),
                [("Track 1", 1260), ("Track 2", 1835), ("", 1544), ("Track 4", 2010), ("Track 5", 1712), ("Track 6", 1980), ("07", 1650),
                 ("Track 8", 2215), ("Track 9", 1490), ("Track 10", 1875), ("Track 11", 2060), ("Track 12", 1720), ("Track 13", 1930), ("Track 14", 1105)],
                Done: 0, ResumeAt: 0, Included: false, SampleMinutes: 5,
                Blurb: "A lighthouse keeper's daughter comes home to Harrow after twenty years and finds the lamp lit every night, though the light was decommissioned the winter she left.\n\nA slow, salt-stung mystery about the stories a village tells to keep itself together.",
                Similar: [6, 0, 4, 7, 3, 5]),
            new("Slack in the System: Notes on Rest, Work and Enough", ["Imogen Hartley"], ["Imogen Hartley"], "Fernhill Audio", "Business\nSelf-help",
                12, 0xFF26324D, 460, 812, new DateOnly(2021, 5, 20),
                [("Opening credits", 31), ("Why slack", 1320), ("The full calendar", 1610), ("Margins", 1455), ("Recovery is work", 1730), ("Saying later", 1290),
                 ("The quiet quarter", 1580), ("Enough", 1405), ("Acknowledgements", 210), ("End credits", 40)],
                Done: 10, ResumeAt: 0, Included: true, SampleMinutes: 4,
                Blurb: "Before The Quiet Engine, Imogen Hartley wrote this short book about the time a team does not schedule, and why it is where the good work comes from.\n\nPractical, warm and brief: one evening's listening.",
                Similar: [0, 3, 4, 5, 6, 7]),
            new("The Second Draft: Writing at Work", ["Imogen Hartley"], ["Daniel Osei"], "Fernhill Audio", "Business\nWriting",
                13, 0xFF3B2E1C, 470, 640, new DateOnly(2025, 2, 11),
                [("Opening credits", 30), ("The first draft is for you", 1520), ("The second is for them", 1690)],
                Done: 0, ResumeAt: 0, Included: true, SampleMinutes: 5, Blurb: "How a small team writes things down so that the next person can pick them up.", Similar: [0, 2]),
            new("Calm Companies", ["Priya Raman"], ["Priya Raman"], "Lantern House", "Business\nManagement",
                14, 0xFF26324D, 450, 2_310, new DateOnly(2022, 9, 6),
                [("Calm is a choice", 1810), ("The forty-hour week", 1650), ("Fewer, better", 1930)],
                Done: 0, ResumeAt: 0, Included: true, SampleMinutes: 5, Blurb: "Running a company without the noise.", Similar: [0, 2]),
            new("Enough", ["Tom Aldous"], ["Sam Iwata"], "Lantern House", "Business",
                15, 0xFFC9A227, 420, 980, new DateOnly(2020, 1, 14),
                [("Enough money", 1450), ("Enough time", 1520), ("Enough", 1210)],
                Done: 0, ResumeAt: 0, Included: true, SampleMinutes: 5, Blurb: "On wanting less and building it.", Similar: [0, 2]),
            new("The Long Table", ["Wren Okoro"], ["Wren Okoro"], "Northgate Audio", "Fiction",
                3, 0xFF4A2A3A, 455, 1_540, new DateOnly(2024, 6, 1),
                [("Part one", 2410), ("Part two", 2630), ("Part three", 2280)],
                Done: 0, ResumeAt: 0, Included: true, SampleMinutes: 5, Blurb: "A family, a farmhouse and forty years of Sunday lunches.", Similar: [1, 0]),
            new("Maintenance", ["Sam Iwata"], ["Sam Iwata"], "Kettle Yard Media", "Technology",
                7, 0xFF1F2937, 430, 410, new DateOnly(2023, 4, 18),
                [("Things that last", 1660), ("Repair", 1745), ("Care", 1390)],
                Done: 0, ResumeAt: 0, Included: true, SampleMinutes: 5, Blurb: "The unglamorous work that keeps everything else running.", Similar: [0, 2]),
        ];

        static string BookUri(int b) => "spotify:show:ab" + b.ToString(CultureInfo.InvariantCulture);
        static string ChapterUri(int b, int i) => "spotify:episode:ab" + b.ToString(CultureInfo.InvariantCulture) + "c" + i.ToString(CultureInfo.InvariantCulture);
        static string SampleUri(int b) => "spotify:episode:ab" + b.ToString(CultureInfo.InvariantCulture) + "sample";

        /// <summary>The fixture books in the saved-shows set (Your Library): ab0 in progress and ab2 finished.</summary>
        internal static readonly int[] SavedBooks = [0, 2];

        /// <summary>Every book's show row, its chapters in book order and its sample chapter.</summary>
        static void StageAudiobooks(Staging s, long now0)
        {
            for (int b = 0; b < AudiobookCount; b++)
            {
                var book = s_books[b];
                ref var show = ref s.Shows.RowFor(Text(s, BookUri(b)), Authority.Seed, (uint)ShowFields.All);
                show.Title = Text(s, book.Title);
                show.Image = Text(s, Cover(book.Cover));
                show.Publisher = Text(s, book.Authors[0]);                         // SHOW_V4 field 66: the author
                string header = "Author(s): " + string.Join(", ", book.Authors) + "\nNarrator(s): " + string.Join(", ", book.Narrators) + "\n\n";
                show.Description = Text(s, header + book.Blurb);
                show.HtmlDescription = Text(s, book.Blurb);                       // HTML_DESCRIPTION has no header
                show.Topics = Text(s, book.Genres);
                show.Order = (byte)ConsumptionOrder.Sequential;
                show.Flags = (uint)(ShowFlags.Audiobook | ShowFlags.CanRate);
                show.HeaderFacts = (uint)ShowFlags.Audiobook;
                show.RatingX100 = (ushort)book.RatingX100;
                show.RatingCount = book.RatingCount;
                show.Tone = book.Tone;
                show.Trailer = Text(s, SampleUri(b));
                show.EpisodesAsked = book.Chapters.Length;

                int released = (int)new DateTimeOffset(book.Released.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
                for (int i = 0; i < book.Chapters.Length; i++)
                {
                    var (name, seconds) = book.Chapters[i];
                    ref var ep = ref s.Episodes.RowFor(Text(s, ChapterUri(b, i)), Authority.Seed, (uint)EpisodeFields.All);
                    ep.Title = Text(s, name);
                    ep.Image = Text(s, Cover(book.Cover));
                    ep.Description = default;
                    ep.DurationMs = seconds * 1000;
                    ep.PublishedAt = released;
                    ep.ShowUri = Text(s, BookUri(b));
                    ep.Number = (ushort)(i + 1);
                    ep.Kind = (byte)EpisodeKind.Full;
                    ep.Flags = (uint)EpisodeFlags.AudiobookChapter;
                    if (i < book.Done)
                    {
                        ep.ProgressMs = ep.DurationMs;
                        ep.ExplicitCompleted = true;
                        ep.PlayedAt = (int)(now0 - (book.Done - i) * Day - 3 * Day);   // one a day, the last three days before now0
                    }
                    else if (i == book.Done && book.ResumeAt > 0)
                    {
                        ep.ProgressMs = book.ResumeAt * 1000;
                        ep.PlayedAt = (int)(now0 - 2 * Day);
                    }
                }

                ref var sample = ref s.Episodes.RowFor(Text(s, SampleUri(b)), Authority.Seed, (uint)EpisodeFields.All);
                sample.Title = Text(s, "Sample");
                sample.Image = Text(s, Cover(book.Cover));
                sample.DurationMs = book.SampleMinutes * 60_000;
                sample.PublishedAt = released;
                sample.Kind = (byte)EpisodeKind.Trailer;
                sample.Flags = (uint)EpisodeFlags.AudiobookChapter;
                sample.ShowUri = Text(s, BookUri(b));
            }
        }

        /// <summary>The chapter memberships (book order, complete) and the facts the show row cannot hold (<see cref="AudiobookFactsStore"/>).</summary>
        static void LandAudiobooks()
        {
            Span<int> targets = stackalloc int[32];
            for (int b = 0; b < AudiobookCount; b++)
            {
                var book = s_books[b];
                int n = book.Chapters.Length;
                for (int i = 0; i < n; i++) targets[i] = SlotOf(Current.Episodes, ChapterUri(b, i));
                Current.Edges.ShowEpisodes.Replace(SlotOf(Current.Shows, BookUri(b)), targets[..n], default, EdgeState.Complete, n);

                var similar = ImmutableArray.CreateBuilder<string>(book.Similar.Length);
                foreach (int k in book.Similar) similar.Add(BookUri(k));
                AudiobookFactsStore.Put(BookUri(b), new AudiobookFacts(
                    [], [],                                     // credits: the page parses the show row (the publisher column, "Narrator(s):")
                    book.Publisher, "Unabridged", "English", book.Released, book.Included,
                    book.Included ? "" : "\u20AC19.99", book.Included ? "" : "\u20AC24.99", "", similar.ToImmutable()));
            }
        }
    }
}
