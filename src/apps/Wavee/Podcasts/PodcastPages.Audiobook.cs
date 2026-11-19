// ── Podcasts/PodcastPages.Audiobook.cs — the audiobook page's core host (podcast pages v2, concept page "Audiobook") ─────────────────────────────────
//
// Spotify models a book as a show whose chapters are episodes; this host reads the same data as a BOOK (concept notes "Audiobooks are books"):
//
//   · DEMAND: the show whole (`ShowFields.All`), its chapter membership (book order; the next page while it is partial), the chapters' rows, the
//     sample chapter (the show's trailer column) and the similar books' identities.
//   · THE SNAPSHOT (`AudiobookView`, one `UseComputed` over a stable delegate): the title split at the first colon; "By" the authors (the facts store,
//     else the description's "Author(s):" header line, else the publisher column, which holds the first author today) and "Narrated by" the narrators
//     (the facts store, else the "Narrator(s):" header line); genres; the rating and its count; the length; the contents (a generic or empty chapter
//     title reads "Chapter N"; credits, introduction, epilogue and acknowledgements are matter, set quieter; each chapter's start time is its page
//     number); the state (not started / in progress / finished) and the one primary; the sample; the library; the Premium line or the price pair;
//     the facts; "More by <author> and similar books".
//   · THE PLACE (`AudiobookPlace`, live): the sum of the chapters' resume points over the book's length, the playing chapter's own clock replacing
//     its resting value, so the bar, the percentage and the current row's time left move while a chapter plays.
//   · THE VERBS: Start / Continue / Listen again (the chapter with the latest resume point, else the one after the last finished; a book not started
//     or finished starts at its first chapter), the sample, a chapter, the library, the speed (0.8 / 1 / 1.2 / 1.5 / 2 ×, `Playback.EpisodeSpeed`),
//     the sleep timer (`SleepTimer`), the hours and top-up links (browser), Buy, the menu (Mark as finished / unfinished), share, rate.
//
// The facts Wavee's tables do not hold yet (the real publisher, edition, language, price, similar books) come from `AudiobookFactsStore`: the data
// lane's AUDIOBOOK_SPECIFICS / AUDIOBOOK_PRICE / similarAudiobooks answers slot in there. The hours line is the plan's allowance and the next refill
// (`AudiobookHours`), never the hours left.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using PP = Wavee.Strings.Podcast.Pages;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The audiobook page's host: the demand, the snapshot, the place and the verbs.</summary>
[CoreHost]
public sealed class AudiobookScreen : Component
{
    Book? _m;

    public override Element Render()
    {
        var p = UseProps<PodcastPages.HostProps>();
        var m = _m ??= new Book(p.Subject, p.RouteKey);
        UseEffect(m.Demand);
        UseEffect(() => m.Leave, DepKey.Empty);
        var snap = UseComputed(m.Compute);
        m.Snap = snap;
        // The window tab and the history rows read the page's own title (`PageChromeChannel.PublishedTitle`), not the route's "Podcast".
        m.Sink = UseContext(PageChromeSink.Context);
        UseEffect(m.PublishTitle);
        UseEffect(() => m.WithdrawTitle, DepKey.Empty);
        var view = UseComputed(m.ViewOf);
        var place = UseComputed(m.PlaceOf);
        var playing = UseComputed(m.PlayingOf);
        var speed = UseComputed(Book.SpeedLabelOf);
        var sleep = UseComputed(Book.SleepLabelOf);
        var sleepOn = UseComputed(Book.SleepOnOf);
        var rateState = UseComputed(m.Rater.Compute);
        m.Props ??= m.BuildProps(view, place, playing, speed, sleep, sleepOn, rateState);
        return Slots.View(ViewKeys.AudiobookPage, m.Props);
    }

    /// <summary>The snapshot plus what the place and the verbs read beside it (each chapter's id, length and resting progress).</summary>
    internal sealed record Snapshot(AudiobookView View, Show Show, ImmutableArray<Episode> Chapters, int[] Rest, int[] Lengths, long TotalMs,
        int Current, AudiobookState State, Episode Sample)
    {
        public static Snapshot Empty { get; } = new(AudiobookView.Empty, default, [], [], [], 0L, -1, AudiobookState.NotStarted, default);
    }

    sealed class Book
    {
        readonly EntityUri subject;
        readonly string routeKey;
        public readonly string UriText;
        public Memo<Snapshot>? Snap;
        public AudiobookPageProps? Props;
        /// <summary>The rate verb and the rate control's state (shared with the show page).</summary>
        public readonly PodcastRater Rater;

        public Book(EntityUri subject, string routeKey)
        {
            this.subject = subject;
            this.routeKey = routeKey;
            UriText = subject.Text;
            Rater = new PodcastRater(subject, RateFacts);
        }

        /// <summary>The rate control's inputs, read from the snapshot (the rater's compute subscribes through it).</summary>
        PodcastRater.Facts RateFacts()
        {
            var v = Snap?.Value.View ?? AudiobookView.Empty;
            return new PodcastRater.Facts(v.Title, v.RatingsLine, v.MyRating, v.CanRate);
        }

        Snapshot Peek() => Snap?.Peek() ?? Snapshot.Empty;

        public PageChromeSink Sink = PageChromeSink.None;
        string _published = "";

        /// <summary>Auto-tracked on the snapshot: publishes the book's title once it is known and whenever it changes.</summary>
        public void PublishTitle()
        {
            string title = Snap?.Value.View.Title ?? "";
            if (title.Length == 0 || title == _published) return;
            _published = title;
            Sink.Publish(new PageChromeRow([], [], Title: title));
        }

        public void WithdrawTitle() => Sink.Publish(null);

        // ══ DEMAND ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

        public void Leave()
        {
            var show = Entities.Current.Shows.TryGetSlot(subject.Id, out int slot) ? new Show(slot) : default;
            if (show.IsValid) PodcastPages.Leave(show);
        }

        /// <summary>Auto-tracked: the data lane's ask (the show's groups, the book's GraphQL reads, the membership and its rows); re-runs in a new
        /// scope. The similar books' thin rows come with it.</summary>
        public void Demand()
        {
            _ = Entities.ScopeEpoch.Value;
            var show = Entities.Show(subject.Id);
            if (show.IsValid) PodcastPages.Open(show);
        }

        // ══ THE SNAPSHOT ════════════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The book's cover, once any answer has named it (a card's thin row does).</summary>
        static string? ArtOf(Show show) => show.Knows(ShowFields.Image) && !show.ImageId.IsEmpty ? Controls.ArtUrl(show.ImageId) : null;

        public Snapshot Compute()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            var show = scope.Shows.TryGetSlot(subject.Id, out int showSlot) ? new Show(showSlot) : default;
            if (!show.IsValid) return Snapshot.Empty with { View = AudiobookView.Empty with { Uri = UriText } };
            var book = PodcastPages.AudiobookOf(show);                    // reactive: the tables, the library, the book stores
            // Still loading, the page holds what it already knows: the cover a card staged stands in the hero at once (the place a card's cover
            // flies to), the rest shimmers until the book lands.
            if (!book.Loaded || book.Title.Length == 0) return Snapshot.Empty with { View = AudiobookView.Empty with { Uri = UriText, ArtUrl = ArtOf(show) } };

            // ── the chapters ──
            var toc = book.Toc.Chapters;
            int n = toc.Length;
            var deck = Playback.CurrentId.Value;
            var chapters = ImmutableArray.CreateBuilder<Episode>(n);
            int[] rest = new int[n], lengths = new int[n];
            var rows = ImmutableArray.CreateBuilder<AudiobookChapterVm>(n);
            long total = 0;
            int onDeck = -1, current = -1, finishedAt = 0;
            bool named = false;
            // A chapter's place in the book is shown once it and every chapter before it have a length (a chapter row arrives before its facts; the
            // first chapter alone saying 0:00 over a column of blanks reads as a fault).
            bool startsKnown = true;
            for (int i = 0; i < n; i++)
                if (toc[i].DisplayTitle == toc[i].RawTitle) { named = true; break; }
            for (int i = 0; i < n; i++)
            {
                var c = toc[i];
                var e = c.Chapter;
                chapters.Add(e);
                int len = Math.Max(0, c.DurationMs);
                bool done = c.State == PlayState.Played;
                rest[i] = done ? len : Math.Clamp(c.ProgressMs, 0, len);
                lengths[i] = len;
                if (done && e.IsValid) finishedAt = Math.Max(finishedAt, e.PlayedAt);
                if (e.IsValid && !deck.IsEmpty && deck == e.Id) onDeck = i;
                if (book.Progress.Resume.IsValid && e == book.Progress.Resume) current = i;
                bool generic = c.DisplayTitle != c.RawTitle;
                rows.Add(new AudiobookChapterVm(i, c.Number.ToString(CultureInfo.CurrentCulture), c.DisplayTitle,
                    !generic && AudiobookRules.IsMatter(c.RawTitle),
                    (int)Math.Min(int.MaxValue, total), len, startsKnown && len > 0 ? AudiobookRules.Clock(total) : "", len > 0 ? Episode.DurationLabel(len) : "", done,
                    generic ? c.DisplayTitle : PodcastPages.ChapterTitle("", c.Number) + ", " + c.DisplayTitle));
                if (len <= 0) startsKnown = false;
                total += len;
            }
            var progress = book.Progress;
            var state = progress.Finished ? AudiobookState.Finished
                : progress.Started || onDeck >= 0 ? AudiobookState.InProgress : AudiobookState.NotStarted;
            if (state != AudiobookState.InProgress) current = -1;
            else if (onDeck >= 0) current = onDeck;
            else if (current < 0) current = 0;

            // ── texts ──
            long bookMs = book.TotalDurationMs > 0 ? book.TotalDurationMs : total;
            string length = bookMs > 0 ? Episode.DurationWords(Episode.Rules.Minutes((int)Math.Min(int.MaxValue, bookMs))) : "";
            int chapterCount = Math.Max(n, book.Toc.Total);
            string chaptersWord = chapterCount > 0 ? PP.ChaptersCount(chapterCount) : "";   // never "0 chapters" while nothing has said how many
            // The contents are on their way until the membership has answered whole (a book pages itself to the end) or failed. The page does
            // not wait for them: it says how many there will be, and a theme holds their place.
            var edge = scope.Edges.ShowEpisodes;
            var readiness = edge.Readiness(showSlot);
            bool chaptersPending = readiness == EdgeState.Unknown
                || (readiness == EdgeState.Partial && !book.Toc.Complete && !edge.IsFailed(showSlot));
            string year = book.Released is { } r ? r.Year.ToString(CultureInfo.CurrentCulture) : "";
            var meta = ImmutableArray.CreateBuilder<string>(3);
            if (length.Length > 0) meta.Add(length);
            if (chapterCount > 0) meta.Add(chaptersWord);
            if (year.Length > 0) meta.Add(year);
            // Nothing of the line is known before the chapters answer: it stands from the first frame, so the primary under it does not step down.
            if (meta.Count == 0 && chaptersPending) meta.Add(HeldLine);

            var rated = book.Rating;
            string rating = rated is { Average: > 0f } ra ? ra.Average.ToString("0.0", CultureInfo.CurrentCulture) : "";
            string count = rated is { Count: > 0 } rc ? rc.Count.ToString("N0", CultureInfo.CurrentCulture) : "";
            string finishedDate = finishedAt > 0 ? Episode.DateLabel(finishedAt) : "";

            // The resting word: Pause is the view's leaf (it binds Playing), so a press never rebuilds the snapshot.
            string primary = Loc.Get(state switch { AudiobookState.InProgress => PP.Continue, AudiobookState.Finished => PP.ListenAgain, _ => PP.StartListening });
            string upTitle, upName = "", upSub;
            switch (state)
            {
                case AudiobookState.InProgress:
                    upTitle = named ? PP.ChapterOfNamed(current + 1, chapterCount) : PP.ChapterOf(current + 1, chapterCount);
                    upName = named && current < n ? rows[current].Name : "";
                    upSub = "";
                    break;
                case AudiobookState.Finished:
                    upTitle = Loc.Get(PP.Finished);
                    upSub = finishedDate.Length > 0 ? PP.FinishedOnLine(finishedDate) : Loc.Get(PP.FinishedLine);
                    break;
                default:
                    // The length arrives with the chapters' facts: its line stands from the first frame (a measured blank holds it), so the
                    // words under it do not step down when it lands.
                    upTitle = length.Length > 0 ? length : HeldLine;
                    upSub = chaptersWord.Length > 0 ? chaptersWord : HeldLine;
                    break;
            }
            string lineSub = state switch
            {
                AudiobookState.InProgress => PP.LineSub(PP.DrawnToLength(chapterCount)),
                AudiobookState.Finished => finishedDate.Length > 0 ? PP.FinishedSub(finishedDate) : "",
                _ => PP.LineSub(Loc.Get(PP.SelectToStart)),
            };
            string contentsSub = length.Length > 0 && chapterCount > 0 ? PP.ContentsSub(chapterCount, length) : chaptersWord;   // no "100 chapters ·" before the length
            if (!named && n > 0) contentsSub += " · " + Loc.Get(PP.NoTitles);

            // ── access: the Premium line (the plan's allowance, never hours left) or the price pair ──
            bool included = book.Access.Included || book.Price is null;
            string premium = "";
            if (included)
            {
                var allowance = PodcastPages.Allowance.Value;
                string signifier = book.Access.Signifier.Length > 0 ? book.Access.Signifier : Loc.Get(PP.Premium);
                premium = allowance is { AllowanceMs: > 0 } al
                    ? PP.PremiumHours((int)(al.AllowanceMs / 3_600_000L), al.Refill.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture))
                    : signifier;
            }
            string price = !included && book.Price is { } bp ? bp.Final.Formatted : "";
            string listPrice = !included && book.Price is { } lp && lp.List.Amount > 0 && lp.List.Amount != lp.Final.Amount ? lp.List.Formatted : "";

            // ── the sample ──
            Episode sample = book.Preview is { } pv ? pv.Sample : default;
            string sampleUri = book.Preview is { } pu ? pu.SampleUri : "";
            if (!sample.IsValid && sampleUri.Length > 0 && scope.Episodes.TryGetSlot(sampleUri.AsSpan(), out int ss)) sample = new Episode(ss);
            string sampleLength = sample.IsValid && sample.DurationMs > 0 ? Episode.DurationLabel(sample.DurationMs) : "";
            string? sampleId = sample.IsValid ? sample.Uri.Text : sampleUri.Length > 0 ? sampleUri : null;

            // ── about and facts ──
            var authors = book.Authors;
            string authorLine = string.Join(", ", authors.Select(static a => a.Name));
            var factRows = ImmutableArray.CreateBuilder<PodcastFact>(8);
            if (authorLine.Length > 0) factRows.Add(new(Loc.Get(PP.Author), authorLine));
            if (book.Narrators.Length > 0) factRows.Add(new(Loc.Get(PP.Narrator), string.Join(", ", book.Narrators)));
            if (book.Publisher.Length > 0) factRows.Add(new(Loc.Get(PP.Publisher), book.Publisher));
            if (book.Released is { } rel) factRows.Add(new(Loc.Get(PP.Released), rel.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)));
            if (chapterCount > 0) factRows.Add(new(Loc.Get(PP.Length), length.Length > 0 ? PP.LengthValue(length, chapterCount) : chaptersWord));   // no ", 100 chapters" before the length
            if (book.Language.Length > 0) factRows.Add(new(Loc.Get(PP.Language), AudiobookRules.LanguageName(book.Language)));
            if (book.Edition.Length > 0) factRows.Add(new(Loc.Get(PP.Edition), book.Edition));
            var genres = new List<string>(book.Genres.Length);
            foreach (var g in book.Genres) if (!genres.Contains(g.Name)) genres.Add(g.Name);
            if (genres.Count > 0) factRows.Add(new(Loc.Get(PP.Genres), string.Join(", ", genres)));

            // ── more by the author and similar books ──
            string firstAuthor = authors.Length > 0 ? authors[0].Name : "";
            var similar = SimilarCards(scope, book.MoreLikeThis, firstAuthor);
            bool saved = book.Saved == SaveState.Saved;

            var view = new AudiobookView(
                Load: PodcastLoad.Ready,
                Uri: UriText, Title: book.Title, Subtitle: book.Subtitle,
                ArtUrl: ArtOf(show),
                Tone: show.Knows(ShowFields.Rating) ? show.Tone : 0u,
                Kind: Loc.Get(PP.KindAudiobook), Genres: [.. genres.Take(2)], Included: included,
                IncludedTag: book.Access.Signifier.Length > 0 ? book.Access.Signifier : Loc.Get(PP.Premium),
                Authors: [.. authors.Select(static a => new AudiobookPersonVm(a.Name, a.Uri))],
                Narrators: [.. book.Narrators.Select(static a => new AudiobookPersonVm(a, ""))],
                ByLabel: Loc.Get(PP.By), NarratedByLabel: Loc.Get(PP.NarratedBy),
                Rating: rating, RatingCount: count.Length > 0 ? PP.RatingOutOf(count) : "", Meta: meta.ToImmutable(),
                State: state, PrimaryLabel: primary, PrimaryAccent: state != AudiobookState.Finished,
                UpTitle: upTitle, UpName: upName, UpSub: upSub,
                BarTitle: state == AudiobookState.Finished ? Loc.Get(PP.Finished) : length,
                SampleId: sampleId,
                SampleButton: sampleLength.Length > 0 ? PP.Sample(sampleLength) : Loc.Get(PP.SampleRow),
                SampleTitle: Loc.Get(PP.SampleRow),
                SampleSub: sampleLength.Length > 0 ? PP.SampleSub(sampleLength) : "",
                InLibrary: saved, LibraryLabel: Loc.Get(saved ? PP.InLibrary : PP.AddLibrary),
                PremiumLine: premium, CheckHoursLabel: Loc.Get(PP.CheckHours), TopUpLabel: Loc.Get(PP.TopUp),
                Price: price, ListPrice: listPrice, BuyLabel: included ? "" : book.BuyLabel.Length > 0 ? book.BuyLabel : Loc.Get(PP.Buy),
                Chapters: rows.ToImmutable(), NamesMissing: !named && n > 0, ContentsTitle: Loc.Get(PP.Contents), ContentsSub: contentsSub,
                StartsAtLabel: Loc.Get(PP.StartsAt),
                LineTitle: Loc.Get(state == AudiobookState.Finished ? PP.Finished : PP.WholeBook), LineSub: lineSub, LineRight: length,
                AboutTitle: Loc.Get(PP.AboutBook), Facts: factRows.ToImmutable(),
                RatingsLine: rated is { Count: > 0 } rl ? rl.Count == 1 ? PP.RatingsCount(1) : PP.RatingsMany(count) : "",   // "4,952 ratings": the grouped count
                RateLabel: Loc.Get(state == AudiobookState.InProgress ? PP.Rate : PP.RateAfter),
                FinishedTitle: Loc.Get(PP.YouFinished),
                FinishedSub: rating.Length > 0 ? PP.HowWasIt(rating, count) : "",
                Similar: similar, SimilarTitle: firstAuthor.Length > 0 ? PP.MoreByAuthor(firstAuthor) : Loc.Get(PP.SimilarBooks),
                SeeAllLabel: Loc.Get(PP.SeeAll))
            {
                Current = current, ChaptersPending = chaptersPending, ChapterCount = chapterCount, LineKnown = n > 0 && !chaptersPending && startsKnown,
                KindRoute = "browse:" + AudiobookRules.AudiobooksCategory,   // the Browse category route (Browse.CategoryPageFor)
                AboutHtml = AudiobookRules.TidyHtml(book.AboutHtml.Length > 0 ? book.AboutHtml : book.About),
                MyRating = rated?.Mine ?? (show.Knows(ShowFields.Rating) ? show.MyRating : 0),
                CanRate = rated?.CanRate ?? (show.Knows(ShowFields.Rating) && (show.Flags & ShowFlags.CanRate) != 0),
            };
            return new Snapshot(view, show, chapters.ToImmutable(), rest, lengths, total, current, state, sample);
        }

        /// <summary>A line that is not known yet, held: one FIGURE SPACE (it measures, so the line box stands; nothing is drawn).</summary>
        const string HeldLine = "\u2007";

        /// <summary>The similar books (similarAudiobooks' thin show rows), the author's own first; at most twelve.</summary>
        ImmutableArray<PodcastCardVm> SimilarCards(Scope scope, string[] uris, string author)
        {
            var mine = new List<PodcastCardVm>();
            var others = new List<PodcastCardVm>();
            foreach (string uri in uris)
            {
                if (uri == UriText || !scope.Shows.TryGetSlot(uri.AsSpan(), out int slot)) continue;
                var s = new Show(slot);
                if (!s.Knows(ShowFields.Title) || s.Title.Length == 0) continue;
                string by = s.Knows(ShowFields.Publisher) ? Entities.Strings.Resolve(s.PublisherId) : "";
                var card = new PodcastCardVm(uri, AudiobookRules.SplitTitle(s.Title).Title, by,
                    s.Knows(ShowFields.Image) && !s.ImageId.IsEmpty ? Controls.ArtUrl(s.ImageId) : null);
                (by.Length > 0 && by == author ? mine : others).Add(card);
            }
            mine.AddRange(others);
            return [.. mine.Take(12)];
        }

        public AudiobookView ViewOf() => Snap!.Value.View;

        // ══ THE PLACE (live) ════════════════════════════════════════════════════════════════════════════════════════════════════

        public AudiobookPlace PlaceOf()
        {
            var s = Snap!.Value;
            int n = s.Chapters.Length;
            if (n == 0 || s.TotalMs <= 0) return default;
            if (s.State == AudiobookState.Finished)
                return new AudiobookPlace((int)Math.Min(int.MaxValue, s.TotalMs), 1f, -1, 1f, "", "", "", "");
            if (s.State == AudiobookState.NotStarted) return default;
            long listened = 0;
            for (int i = 0; i < n; i++) listened += s.Rest[i];
            int current = s.Current, curPos = 0;
            float fill = 0f;
            if (current >= 0)
            {
                var live = Episode.PlaybackProgressOf(s.Chapters[current]);   // the deck chapter's clock; the resting value otherwise
                curPos = live.Completed ? s.Lengths[current] : Math.Clamp(live.PositionMs, 0, s.Lengths[current]);
                listened += curPos - s.Rest[current];
                fill = s.Lengths[current] > 0 ? curPos / (float)s.Lengths[current] : 0f;
            }
            float fraction = Math.Clamp(listened / (float)s.TotalMs, 0f, 1f);
            int pct = (int)MathF.Round(fraction * 100f);
            int leftMin = Episode.Rules.Minutes((int)Math.Min(int.MaxValue, s.TotalMs - listened));
            int inMin = Episode.Rules.Minutes((int)Math.Min(int.MaxValue, listened));
            return new AudiobookPlace((int)Math.Min(int.MaxValue, listened), fraction, current, fill,
                s_bookLeft.Get((pct, leftMin), s_bookLeftFormat), s_bookLeftLong.Get((pct, leftMin), s_bookLeftLongFormat),
                s_inLeft.Get((inMin, leftMin), s_inLeftFormat), current >= 0 ? Episode.LeftLabel(curPos, s.Lengths[current]) : "")
            {
                Percent = s_percent.Get((pct, 0), s_percentFormat), Left = s_left.Get((0, leftMin), s_leftFormat),
            };
        }

        static readonly FormatCache<(int Pct, int Left)> s_percent = new(), s_left = new();
        static readonly Func<(int Pct, int Left), string> s_percentFormat = static k => PP.Percent(k.Pct);
        static readonly Func<(int Pct, int Left), string> s_leftFormat = static k => PP.Left(Episode.DurationWords(k.Left));

        static readonly FormatCache<(int Pct, int Left)> s_bookLeft = new(), s_bookLeftLong = new();
        static readonly FormatCache<(int In, int Left)> s_inLeft = new();
        static readonly Func<(int Pct, int Left), string> s_bookLeftFormat = static k => PP.BookLeft(k.Pct, Episode.DurationWords(k.Left));
        static readonly Func<(int Pct, int Left), string> s_bookLeftLongFormat
            = static k => PP.BookLeft(k.Pct, Episode.DurationWords(k.Left)) + Loc.Get(PP.InTheBook);
        static readonly Func<(int In, int Left), string> s_inLeftFormat
            = static k => PP.TimeInLeft(Episode.DurationWords(k.In), Episode.DurationWords(k.Left));

        public bool PlayingOf()
        {
            var s = Snap!.Value;
            if (!Playback.IsPlaying.Value) return false;
            var deck = Playback.CurrentId.Value;
            if (deck.IsEmpty) return false;
            foreach (var e in s.Chapters) if (e.Id == deck) return true;
            return false;
        }

        public static string SpeedLabelOf() => s_rate.Get((int)MathF.Round(Playback.EpisodeSpeed.Value * 100f), s_rateFormat);

        static readonly FormatCache<int> s_rate = new();
        static readonly Func<int, string> s_rateFormat = static x => PP.SpeedRate((x / 100f).ToString("0.##", CultureInfo.CurrentCulture));

        public static string SleepLabelOf() => SleepTimer.Armed.Value switch
        {
            SleepTimer.Mode.Minutes => PP.SleepIn(SleepTimer.MinutesLeft.Value),
            SleepTimer.Mode.EndOfItem => Loc.Get(PP.SleepAtEnd),
            _ => Loc.Get(PP.SleepTimer),
        };

        public static bool SleepOnOf() => SleepTimer.Armed.Value != SleepTimer.Mode.Off;

        // ══ THE VERBS ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

        public AudiobookPageProps BuildProps(IReadSignal<AudiobookView> view, IReadSignal<AudiobookPlace> place, IReadSignal<bool> playing,
            IReadSignal<string> speed, IReadSignal<string> sleep, IReadSignal<bool> sleepOn, IReadSignal<PodcastRateVm> rateState) => new()
        {
            RouteKey = routeKey, View = view, Place = place, Playing = playing,
            Play = Play, PlaySample = PlaySample, PlayChapter = PlayChapter, ToggleLibrary = ToggleLibrary,
            SpeedLabel = speed, SpeedMenu = SpeedMenu, SleepLabel = sleep, SleepOn = sleepOn, SleepMenu = SleepMenu,
            CheckHours = static () => Shell.OpenBrowser(PodcastPages.HoursLeftUrl),
            TopUp = static () => Shell.OpenBrowser(PodcastPages.TopUpUrl),
            Buy = Buy, Download = null, OpenUri = static uri => Shell.OpenLink(uri), Menu = Menu, Share = Share,
            OpenRoute = static key => Shell.GoTo(Shell.Parse(key)),
            SimilarMenu = SimilarMenu, PlaySimilar = PlaySimilar,
            Rate = Rater.Rate, RateState = rateState,
            // A genre searches its words: the genre tree's ids (32-hex AUDIOBOOK_GENRE nodes) name no page Wavee opens (Browse opens
            // spotify:page: categories), so the older page's topic search is the real destination.
            OpenGenre = Show.OpenTopic,
            CoverDrag = PodcastPages.CoverDrag(subject),
        };

        void Play()
        {
            var s = Peek();
            if (s.Chapters.Length == 0) return;
            bool resume = s.State == AudiobookState.InProgress && s.Current >= 0;
            var e = s.Chapters[resume ? s.Current : 0];
            Episode.Invoke(e, () => Episode.StartAt(e, resume ? Playback.EpisodeStartKind.Resume : Playback.EpisodeStartKind.Beginning));
        }

        void PlayChapter(int i)
        {
            var s = Peek();
            if ((uint)i >= (uint)s.Chapters.Length) return;
            var e = s.Chapters[i];
            bool done = s.Lengths[i] > 0 && s.Rest[i] >= s.Lengths[i];
            Episode.Invoke(e, () => Episode.StartAt(e, done ? Playback.EpisodeStartKind.Beginning : Playback.EpisodeStartKind.Resume));
        }

        void PlaySample()
        {
            var s = Peek();
            var sample = s.Sample;
            if (sample.IsValid) Episode.Invoke(sample, () => Playback.PlayContext(sample.Id));
            else if (s.View.SampleId is { Length: > 0 } uri) Playback.PlayContext(uri);
        }

        void ToggleLibrary() => Controls.Library?.ToggleSaved(UriText, Peek().View.Title);

        void Buy() => Shell.OpenBrowser(Actions.WebLinkOf(subject));

        void Share() => Episode.CopyLink(Actions.WebLinkOf(subject));

        /// <summary>The show container menu for a similar book's card (built at open).</summary>
        ContextMenuModel? SimilarMenu(string uri)
        {
            foreach (var c in Peek().View.Similar)
                if (c.Uri == uri)
                {
                    var target = ActionTarget.ForShow(EntityUri.Parse(uri.AsSpan()), c.Title);
                    return Menus.Container(in target, c.ArtUrl, c.Subtitle);
                }
            return null;
        }

        /// <summary>A similar book's card play: the book as the context.</summary>
        static void PlaySimilar(string uri)
        {
            var id = EntityUri.Parse(uri.AsSpan()).Id;
            if (!id.IsEmpty) Playback.PlayContext(id);
        }

        static ImmutableArray<PodcastMenuItem> SpeedMenu()
        {
            float now = Playback.EpisodeSpeed.Peek();
            var items = ImmutableArray.CreateBuilder<PodcastMenuItem>(AudiobookRules.Speeds.Length);
            foreach (float r in AudiobookRules.Speeds)
            {
                float rate = r;
                items.Add(new PodcastMenuItem(s_rate.Get((int)MathF.Round(rate * 100f), s_rateFormat), null, "", MathF.Abs(now - rate) < 0.01f,
                    () => Playback.SetEpisodeSpeed(rate)));
            }
            return items.ToImmutable();
        }

        static ImmutableArray<PodcastMenuItem> SleepMenu()
        {
            var mode = SleepTimer.Armed.Peek();
            var items = ImmutableArray.CreateBuilder<PodcastMenuItem>(7);
            foreach (int m in SleepTimer.Presets)
            {
                int minutes = m;
                items.Add(new PodcastMenuItem(PP.SleepMinutes(minutes), null, "", false, () => SleepTimer.Set(minutes)));
            }
            items.Add(new PodcastMenuItem(Loc.Get(PP.SleepEndOfChapter), null, "", mode == SleepTimer.Mode.EndOfItem, static () => SleepTimer.SetEndOfItem()));
            if (mode != SleepTimer.Mode.Off)
                items.Add(new PodcastMenuItem(Loc.Get(PP.SleepOff), null, "", false, static () => SleepTimer.Off()));
            return items.ToImmutable();
        }

        ContextMenuModel? Menu()
        {
            var s = Peek();
            var show = s.Show;
            if (!show.IsValid) return null;
            bool finished = s.State == AudiobookState.Finished;
            var chapters = s.Chapters;
            var mark = new MenuFlyoutItem(Loc.Get(finished ? PP.MarkUnfinished : PP.MarkFinished), finished ? Icons.Undo : Icons.Check, chapters.Length > 0, () =>
            {
                foreach (var e in chapters) Entities.MarkEpisode(e, played: !finished);
            });
            var target = ActionTarget.ForShow(show.Uri, s.View.Title);
            return Menus.Container(in target, s.View.ArtUrl, null, new ContainerExtras { OnPage = true, Tail = [mark] });
        }
    }
}

/// <summary>The audiobook page's pure rules: the title split, the description's credit header, the chapter-name rule, matter, the clock.</summary>
public static partial class AudiobookRules
{
    /// <summary>Spotify's Browse category of audiobooks (the official client's "Audiobooks" tile, `spotify:genre:0JQ5DAqbMKFETqK4t8f1n3` in its
    /// own logs): the hub the kind word opens. A book's genres (AUDIOBOOK_GENRE ids, 32 hex) name no Browse page, so they are not linked.</summary>
    public const string AudiobooksCategory = "spotify:page:0JQ5DAqbMKFETqK4t8f1n3";

    /// <summary>The speed menu's rates.</summary>
    public static ReadOnlySpan<float> Speeds => [0.8f, 1f, 1.2f, 1.5f, 2f];

    /// <summary>"Title: Subtitle" split at the FIRST colon (SHOW_V4 sends them joined); no colon → the whole title and no subtitle.</summary>
    public static (string Title, string Subtitle) SplitTitle(string full)
    {
        int i = full.IndexOf(':');
        if (i <= 0 || i >= full.Length - 1) return (full.Trim(), "");
        return (full[..i].Trim(), full[(i + 1)..].Trim());
    }

    /// <summary>Front and back matter, set quieter on the contents page (by name).</summary>
    public static bool IsMatter(string raw) => MatterName().IsMatch(raw.Trim());

    [GeneratedRegex(@"^(opening credits|end credits|ending credits|closing credits|credits|introduction|prologue|foreword|preface|epilogue|afterword|acknowledg)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MatterName();

    /// <summary>"h:mm:ss" (or "m:ss" under an hour): where a chapter starts in the book.</summary>
    public static string Clock(long ms)
    {
        long s = Math.Max(0, ms / 1000);
        long h = s / 3600, m = s % 3600 / 60, x = s % 60;
        return h > 0
            ? h.ToString(CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + x.ToString("00", CultureInfo.InvariantCulture)
            : m.ToString(CultureInfo.InvariantCulture) + ":" + x.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>The provider's HTML with the publisher's doubled quotes (a CSV escape that reached the wire: &amp;#34;&amp;#34;) as one. Runs of
    /// line breaks are the renderer's to fold (<c>Controls.ParseRich</c>).</summary>
    public static string TidyHtml(string html)
        => string.IsNullOrEmpty(html) ? "" : html.Replace("&#34;&#34;", "&#34;").Replace("\"\"", "\"");


    /// <summary>The ISO 639-1 code SHOW_V4 states ("en") as the listener's language names it ("English"); the code itself when unknown.</summary>
    public static string LanguageName(string code)
    {
        string name;
        try { name = CultureInfo.GetCultureInfo(code).DisplayName; }
        catch (CultureNotFoundException) { name = code; }
        // The app runs with invariant globalization (no culture names): the catalogue's common book languages by their English names.
        if (name != code) return name;
        return code.ToLowerInvariant() switch
        {
            "en" => "English", "es" => "Spanish", "de" => "German", "fr" => "French", "it" => "Italian", "nl" => "Dutch", "pt" => "Portuguese",
            "sv" => "Swedish", "da" => "Danish", "no" or "nb" => "Norwegian", "fi" => "Finnish", "pl" => "Polish", "ja" => "Japanese",
            "ko" => "Korean", "zh" => "Chinese", "hi" => "Hindi", "tr" => "Turkish", "ar" => "Arabic", _ => code,
        };
    }
}
