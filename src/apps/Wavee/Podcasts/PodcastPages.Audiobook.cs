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
        m.Similar = UseResource(m.LoadSimilar, s_noRecommendations, (m.UriText, (int)Entities.ScopeEpoch.Value));
        var snap = UseComputed(m.Compute);
        m.Snap = snap;
        var view = UseComputed(m.ViewOf);
        var place = UseComputed(m.PlaceOf);
        var playing = UseComputed(m.PlayingOf);
        var speed = UseComputed(Book.SpeedLabelOf);
        var sleep = UseComputed(Book.SleepLabelOf);
        var sleepOn = UseComputed(Book.SleepOnOf);
        m.Props ??= m.BuildProps(view, place, playing, speed, sleep, sleepOn);
        return Slots.View(ViewKeys.AudiobookPage, m.Props);
    }

    static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation> s_noRecommendations = new([], "", 0, "", 200);

    /// <summary>The snapshot plus what the place and the verbs read beside it (each chapter's id, length and resting progress).</summary>
    internal sealed record Snapshot(AudiobookView View, Show Show, ImmutableArray<Episode> Chapters, int[] Rest, int[] Lengths, long TotalMs,
        int Current, AudiobookState State, Episode Sample)
    {
        public static Snapshot Empty { get; } = new(AudiobookView.Empty, default, [], [], [], 0L, -1, AudiobookState.NotStarted, default);
    }

    sealed class Book(EntityUri subject, string routeKey)
    {
        public readonly string UriText = subject.Text;
        public Resource<Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>> Similar;
        public Memo<Snapshot>? Snap;
        public AudiobookPageProps? Props;

        Snapshot Peek() => Snap?.Peek() ?? Snapshot.Empty;

        // ══ DEMAND ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Auto-tracked: re-runs in a new scope, as the membership grows and as the facts name similar books.</summary>
        public void Demand()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var show = Entities.Show(subject.Id);
            if (!show.IsValid) return;
            Entities.Ensure(show, ShowFields.All);
            var edge = scope.Edges.ShowEpisodes;
            _ = edge.Changed.Value;
            var state = edge.State(show.Slot);
            if (state == EdgeState.Unknown) { Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot); return; }
            var targets = edge.Targets(show.Slot);
            if (state == EdgeState.Partial && targets.Length < edge.Total(show.Slot) && !edge.WasAsked(show.Slot, targets.Length))
                Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot, targets.Length);
            if (targets.Length > 0) Entities.Ensure(scope.Episodes, targets, (uint)ShowReaderRules.RowDemand);

            _ = scope.Shows.Changed.Value;
            if (show.Knows(ShowFields.Facts) && !show.TrailerId.IsEmpty)
            {
                var sample = new Episode(scope.Episodes.Slot(show.TrailerId));
                if (sample.IsValid) Entities.Ensure(sample, EpisodeFields.Row);
            }
            foreach (string uri in AudiobookFactsStore.Of(UriText).Similar)
            {
                var other = Entities.Show(EntityUri.Parse(uri.AsSpan()).Id);
                if (other.IsValid) Entities.Ensure(other, ShowFields.Identity);
            }
        }

        public Task<Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>> LoadSimilar(CancellationToken ct)
            => PodcastReaderUI.RecommendationsOf(UriText, show: true, ct);

        // ══ THE SNAPSHOT ════════════════════════════════════════════════════════════════════════════════════════════════════════

        public Snapshot Compute()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            _ = scope.Episodes.Changed.Value;
            _ = scope.Edges.ShowEpisodes.Changed.Value;
            var facts = AudiobookFactsStore.Of(UriText);
            var show = scope.Shows.TryGetSlot(subject.Id, out int showSlot) ? new Show(showSlot) : default;
            if (!show.IsValid || !show.Knows(ShowFields.Title) || show.Title.Length == 0)
                return Snapshot.Empty with { View = AudiobookView.Empty with { Uri = UriText } };

            // ── identity ──
            var (title, subtitle) = AudiobookRules.SplitTitle(show.Title);
            string description = show.Knows(ShowFields.About) ? Entities.Strings.Resolve(show.DescriptionId) : "";
            var header = AudiobookRules.ParseHeader(description);
            ImmutableArray<AudiobookCredit> authors = facts.Authors.Length > 0 ? facts.Authors
                : header.Authors.Length > 0 ? header.Authors
                : show.Knows(ShowFields.Publisher) && !show.PublisherId.IsEmpty ? [new AudiobookCredit(Entities.Strings.Resolve(show.PublisherId), "")]
                : [];
            var narrators = facts.Narrators.Length > 0 ? facts.Narrators : header.Narrators;
            var genres = AudiobookRules.Lines(show.Knows(ShowFields.Topics) ? Entities.Strings.Resolve(show.TopicsId) : "");
            string? art = show.Knows(ShowFields.Image) && !show.ImageId.IsEmpty ? Controls.ArtUrl(show.ImageId) : null;

            // ── the chapters (the sample is never one) ──
            var edge = scope.Edges.ShowEpisodes;
            var targets = edge.Targets(show.Slot);
            bool membershipKnown = edge.State(show.Slot) != EdgeState.Unknown;
            var chapters = ImmutableArray.CreateBuilder<Episode>(targets.Length);
            bool rowsKnown = true;
            for (int i = 0; i < targets.Length; i++)
            {
                var e = new Episode(targets[i]);
                if (!e.IsValid) continue;
                if (!e.Knows(EpisodeFields.Title)) rowsKnown = false;
                if (e.Kind == EpisodeKind.Trailer) continue;
                chapters.Add(e);
            }
            int n = chapters.Count;
            var deck = Playback.CurrentId.Value;
            bool playing = Playback.IsPlaying.Value;
            int[] rest = new int[n], lengths = new int[n];
            var rows = ImmutableArray.CreateBuilder<AudiobookChapterVm>(n);
            long total = 0, listened = 0;
            int onDeck = -1, latest = -1, latestAt = int.MinValue, lastDone = -1, doneCount = 0, finishedAt = 0;
            bool named = false;
            for (int i = 0; i < n; i++)
                if (!AudiobookRules.IsGeneric(chapters[i].Title)) { named = true; break; }
            for (int i = 0; i < n; i++)
            {
                var e = chapters[i];
                int len = Math.Max(0, e.DurationMs);
                bool done = Episode.Rules.Played(Episode.ReaderPctOf(e));
                int progress = done ? len : Math.Clamp(e.Knows(EpisodeFields.Progress) ? e.ProgressMs : 0, 0, len);
                rest[i] = progress;
                lengths[i] = len;
                if (done) { doneCount++; lastDone = i; finishedAt = Math.Max(finishedAt, e.PlayedAt); }
                else if (progress > 0 && e.PlayedAt >= latestAt) { latest = i; latestAt = e.PlayedAt; }
                if (!deck.IsEmpty && deck == e.Id) onDeck = i;
                string raw = e.Title;
                bool generic = AudiobookRules.IsGeneric(raw);
                string name = generic ? PP.ChapterN(i + 1) : raw;
                rows.Add(new AudiobookChapterVm(i, (i + 1).ToString(CultureInfo.CurrentCulture), name, !generic && AudiobookRules.IsMatter(raw),
                    (int)Math.Min(int.MaxValue, total), len, AudiobookRules.Clock(total), Episode.DurationLabel(len), done,
                    generic ? name : PP.ChapterN(i + 1) + ", " + name));
                total += len;
                listened += progress;
            }

            var state = n > 0 && doneCount == n ? AudiobookState.Finished
                : listened > 0 || doneCount > 0 || onDeck >= 0 ? AudiobookState.InProgress : AudiobookState.NotStarted;
            int current = state != AudiobookState.InProgress ? -1
                : onDeck >= 0 ? onDeck
                : latest >= 0 ? latest
                : Math.Min(n - 1, lastDone + 1);

            // ── the sample ──
            Episode sample = default;
            if (show.Knows(ShowFields.Facts) && !show.TrailerId.IsEmpty && scope.Episodes.TryGetSlot(show.TrailerId, out int sampleSlot))
                sample = new Episode(sampleSlot);
            string sampleLength = sample.IsValid && sample.DurationMs > 0 ? Episode.DurationLabel(sample.DurationMs) : "";

            // ── texts ──
            string length = total > 0 ? Episode.DurationWords(Episode.Rules.Minutes((int)Math.Min(int.MaxValue, total))) : "";
            string chaptersWord = PP.ChaptersCount(n);
            DateOnly? released = facts.Released ?? AudiobookRules.EarliestRelease(chapters);
            string year = released is { } r ? r.Year.ToString(CultureInfo.CurrentCulture) : "";
            var meta = ImmutableArray.CreateBuilder<string>(3);
            if (length.Length > 0) meta.Add(length);
            if (n > 0) meta.Add(chaptersWord);
            if (year.Length > 0) meta.Add(year);

            string rating = show.RatingX100 > 0 ? (show.RatingX100 / 100f).ToString("0.0", CultureInfo.CurrentCulture) : "";
            string count = show.RatingCount > 0 ? show.RatingCount.ToString("N0", CultureInfo.CurrentCulture) : "";
            bool included = facts.Included ?? true;
            string finishedDate = finishedAt > 0 ? Episode.DateLabel(finishedAt) : "";

            string primary = onDeck >= 0 && playing ? Loc.Get(PP.Pause)
                : Loc.Get(state switch { AudiobookState.InProgress => PP.Continue, AudiobookState.Finished => PP.ListenAgain, _ => PP.StartListening });
            string upTitle, upName = "", upSub;
            switch (state)
            {
                case AudiobookState.InProgress:
                    upTitle = named ? PP.ChapterOfNamed(current + 1, n) : PP.ChapterOf(current + 1, n);
                    upName = named ? rows[current].Name : "";
                    upSub = "";
                    break;
                case AudiobookState.Finished:
                    upTitle = Loc.Get(PP.Finished);
                    upSub = finishedDate.Length > 0 ? PP.FinishedOnLine(finishedDate) : Loc.Get(PP.FinishedLine);
                    break;
                default:
                    upTitle = length;
                    upSub = chaptersWord;
                    break;
            }

            string lineSub = state switch
            {
                AudiobookState.InProgress => PP.LineSub(PP.DrawnToLength(n)),
                AudiobookState.Finished => finishedDate.Length > 0 ? PP.FinishedSub(finishedDate) : "",
                _ => PP.LineSub(Loc.Get(PP.SelectToStart)),
            };

            string contentsSub = PP.ContentsSub(n, length);
            if (!named && n > 0) contentsSub += " · " + Loc.Get(PP.NoTitles);

            // ── about and facts ──
            var about = AudiobookRules.Paragraphs(header.Body);
            var factRows = ImmutableArray.CreateBuilder<PodcastFact>(8);
            if (authors.Length > 0) factRows.Add(new(Loc.Get(PP.Author), AudiobookRules.Join(authors)));
            if (narrators.Length > 0) factRows.Add(new(Loc.Get(PP.Narrator), AudiobookRules.Join(narrators)));
            if (facts.Publisher.Length > 0) factRows.Add(new(Loc.Get(PP.Publisher), facts.Publisher));
            if (released is { } rel) factRows.Add(new(Loc.Get(PP.Released), rel.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)));
            if (n > 0) factRows.Add(new(Loc.Get(PP.Length), PP.LengthValue(length, n)));
            if (facts.Language.Length > 0) factRows.Add(new(Loc.Get(PP.Language), facts.Language));
            if (facts.Edition.Length > 0) factRows.Add(new(Loc.Get(PP.Edition), facts.Edition));
            if (genres.Length > 0) factRows.Add(new(Loc.Get(PP.Genres), string.Join(", ", genres)));

            // ── more by the author and similar books ──
            string firstAuthor = authors.Length > 0 ? authors[0].Name : "";
            var similar = SimilarCards(scope, facts, firstAuthor);
            bool saved = Controls.Library?.IsSaved(UriText) ?? false;

            var view = new AudiobookView(
                Load: rowsKnown && membershipKnown ? PodcastLoad.Ready : PodcastLoad.Pending,
                Uri: UriText, Title: title, Subtitle: subtitle, ArtUrl: art, Tone: show.Knows(ShowFields.Rating) ? show.Tone : 0u,
                Kind: Loc.Get(PP.KindAudiobook), Genres: genres, Included: included, IncludedTag: Loc.Get(PP.Premium),
                Authors: [.. authors.Select(static a => new AudiobookPersonVm(a.Name, a.Uri))],
                Narrators: [.. narrators.Select(static a => new AudiobookPersonVm(a.Name, a.Uri))],
                ByLabel: Loc.Get(PP.By), NarratedByLabel: Loc.Get(PP.NarratedBy),
                Rating: rating, RatingCount: count.Length > 0 ? PP.RatingOutOf(count) : "", Meta: meta.ToImmutable(),
                State: state, PrimaryLabel: primary, PrimaryAccent: state != AudiobookState.Finished,
                UpTitle: upTitle, UpName: upName, UpSub: upSub,
                BarTitle: state == AudiobookState.Finished ? Loc.Get(PP.Finished) : length,
                SampleId: sample.IsValid ? sample.Uri.Text : null,
                SampleButton: sampleLength.Length > 0 ? PP.Sample(sampleLength) : Loc.Get(PP.SampleRow),
                SampleTitle: Loc.Get(PP.SampleRow),
                SampleSub: sampleLength.Length > 0 ? PP.SampleSub(sampleLength) : "",
                InLibrary: saved, LibraryLabel: Loc.Get(saved ? PP.InLibrary : PP.AddLibrary),
                PremiumLine: included ? PP.PremiumHours(AudiobookHours.PlanHours,
                    AudiobookHours.NextRefill(DateOnly.FromDateTime(DateTime.Now)).ToString("d MMM", CultureInfo.CurrentCulture)) : "",
                CheckHoursLabel: Loc.Get(PP.CheckHours), TopUpLabel: Loc.Get(PP.TopUp),
                Price: included ? "" : facts.Price, ListPrice: included ? "" : facts.ListPrice, BuyLabel: included ? "" : Loc.Get(PP.Buy),
                Chapters: rows.ToImmutable(), NamesMissing: !named && n > 0, ContentsTitle: Loc.Get(PP.Contents), ContentsSub: contentsSub,
                StartsAtLabel: Loc.Get(PP.StartsAt),
                LineTitle: Loc.Get(state == AudiobookState.Finished ? PP.Finished : PP.WholeBook), LineSub: lineSub, LineRight: length,
                AboutTitle: Loc.Get(PP.AboutBook), About: about, Facts: factRows.ToImmutable(),
                RatingsLine: show.RatingCount > 0 ? PP.RatingsCount(show.RatingCount) : "",
                RateLabel: Loc.Get(state == AudiobookState.InProgress ? PP.Rate : PP.RateAfter),
                FinishedTitle: Loc.Get(PP.YouFinished),
                FinishedSub: rating.Length > 0 ? PP.HowWasIt(rating, count) : "",
                Similar: similar, SimilarTitle: firstAuthor.Length > 0 ? PP.MoreByAuthor(firstAuthor) : Loc.Get(PP.SimilarBooks),
                SeeAllLabel: Loc.Get(PP.SeeAll));
            return new Snapshot(view, show, chapters.ToImmutable(), rest, lengths, total, current, state, sample);
        }

        /// <summary>The fact store's similar books (the author's own first), then the provider's recommendations; at most twelve.</summary>
        ImmutableArray<PodcastCardVm> SimilarCards(Scope scope, AudiobookFacts facts, string author)
        {
            var mine = new List<PodcastCardVm>();
            var others = new List<PodcastCardVm>();
            foreach (string uri in facts.Similar)
            {
                if (!scope.Shows.TryGetSlot(EntityUri.Parse(uri.AsSpan()).Id, out int slot)) continue;
                var s = new Show(slot);
                if (!s.Knows(ShowFields.Title) || s.Title.Length == 0) continue;
                string by = s.Knows(ShowFields.Publisher) ? Entities.Strings.Resolve(s.PublisherId) : "";
                var card = new PodcastCardVm(uri, AudiobookRules.SplitTitle(s.Title).Title, by,
                    s.Knows(ShowFields.Image) && !s.ImageId.IsEmpty ? Controls.ArtUrl(s.ImageId) : null);
                (by.Length > 0 && by == author ? mine : others).Add(card);
            }
            if (Similar.Loadable is { } loadable && loadable.State.Value == (byte)LoadState.Ready)
                foreach (var rec in loadable.Value.Value.Items)
                    if (rec.Uri != UriText)
                        others.Add(new PodcastCardVm(rec.Uri, rec.Title, rec.Subtitle, rec.Image.Length > 0 ? rec.Image : null));
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
                s_inLeft.Get((inMin, leftMin), s_inLeftFormat), current >= 0 ? Episode.LeftLabel(curPos, s.Lengths[current]) : "");
        }

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
            IReadSignal<string> speed, IReadSignal<string> sleep, IReadSignal<bool> sleepOn) => new()
        {
            RouteKey = routeKey, View = view, Place = place, Playing = playing,
            Play = Play, PlaySample = PlaySample, PlayChapter = PlayChapter, ToggleLibrary = ToggleLibrary,
            SpeedLabel = speed, SpeedMenu = SpeedMenu, SleepLabel = sleep, SleepOn = sleepOn, SleepMenu = SleepMenu,
            CheckHours = static () => Shell.OpenBrowser(AudiobookHours.HoursLeftUrl),
            TopUp = static () => Shell.OpenBrowser(AudiobookHours.TopUpUrl),
            Buy = Buy, Download = null, OpenUri = static uri => Shell.OpenLink(uri), Menu = Menu, Share = Share,
            Rate = Platform.Args.Fake ? null : Rate,
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
            var sample = Peek().Sample;
            if (sample.IsValid) Episode.Invoke(sample, () => Playback.PlayContext(sample.Id));
        }

        void ToggleLibrary() => Controls.Library?.ToggleSaved(UriText, Peek().View.Title);

        void Buy() => Shell.OpenBrowser(AudiobookFactsStore.Of(UriText).BuyUrl is { Length: > 0 } url ? url : Actions.WebLinkOf(subject));

        void Share() => Episode.CopyLink(Actions.WebLinkOf(subject));

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

        async void Rate(int value)
        {
            var show = Peek().Show;
            uint epoch = Entities.ScopeEpoch.Peek();
            Spotify.Podcasts.Mutation result;
            try { result = await Spotify.Podcasts.RateAsync(UriText, value, CancellationToken.None).ConfigureAwait(false); }
            catch { result = new(false, 0, true); }
            Store.Post(() =>
            {
                if (Entities.ScopeEpoch.Peek() != epoch || !result.Success || !show.IsValid) return;
                Entities.Invalidate(Entities.Current.Shows, [show.Slot], (uint)ShowFields.Rating);
                Entities.Ensure(show, ShowFields.Rating);
            });
        }
    }
}

/// <summary>The audiobook page's pure rules: the title split, the description's credit header, the chapter-name rule, matter, the clock.</summary>
public static partial class AudiobookRules
{
    /// <summary>The speed menu's rates.</summary>
    public static ReadOnlySpan<float> Speeds => [0.8f, 1f, 1.2f, 1.5f, 2f];

    /// <summary>"Title: Subtitle" split at the FIRST colon (SHOW_V4 sends them joined); no colon → the whole title and no subtitle.</summary>
    public static (string Title, string Subtitle) SplitTitle(string full)
    {
        int i = full.IndexOf(':');
        if (i <= 0 || i >= full.Length - 1) return (full.Trim(), "");
        return (full[..i].Trim(), full[(i + 1)..].Trim());
    }

    /// <summary>The plain description's credit header ("Author(s): A, B\nNarrator(s): C\n\n…") and the text after it.</summary>
    public static (ImmutableArray<AudiobookCredit> Authors, ImmutableArray<AudiobookCredit> Narrators, string Body) ParseHeader(string description)
    {
        if (string.IsNullOrEmpty(description)) return ([], [], "");
        ImmutableArray<AudiobookCredit> authors = [], narrators = [];
        int pos = 0;
        while (pos < description.Length)
        {
            int end = description.IndexOf('\n', pos);
            if (end < 0) end = description.Length;
            var line = description.AsSpan(pos, end - pos).Trim();
            if (line.StartsWith("Author(s):", StringComparison.OrdinalIgnoreCase)) authors = Names(line["Author(s):".Length..]);
            else if (line.StartsWith("Narrator(s):", StringComparison.OrdinalIgnoreCase)) narrators = Names(line["Narrator(s):".Length..]);
            else if (line.Length > 0) break;
            pos = end + 1;
        }
        return (authors, narrators, pos < description.Length ? description[pos..].Trim() : "");
    }

    static ImmutableArray<AudiobookCredit> Names(ReadOnlySpan<char> list)
    {
        var b = ImmutableArray.CreateBuilder<AudiobookCredit>();
        foreach (var part in list.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            b.Add(new AudiobookCredit(part, ""));
        return b.ToImmutable();
    }

    public static string Join(ImmutableArray<AudiobookCredit> people) => string.Join(", ", people.Select(static p => p.Name));

    /// <summary>A chapter title the publisher left generic: empty, "Track 5", "Chapter 5", "Part 5", "05".</summary>
    public static bool IsGeneric(string raw) => string.IsNullOrWhiteSpace(raw) || GenericName().IsMatch(raw.Trim());

    /// <summary>Front and back matter, set quieter on the contents page (by name).</summary>
    public static bool IsMatter(string raw) => MatterName().IsMatch(raw.Trim());

    [GeneratedRegex(@"^(track|chapter|part)?\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericName();

    [GeneratedRegex(@"^(opening credits|end credits|credits|introduction|prologue|foreword|preface|epilogue|afterword|acknowledg)",
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

    /// <summary>Non-empty lines (the topics column is newline-separated).</summary>
    public static ImmutableArray<string> Lines(string text)
        => string.IsNullOrEmpty(text) ? [] : [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Paragraphs (blank-line separated), at most four.</summary>
    public static ImmutableArray<string> Paragraphs(string text)
        => string.IsNullOrWhiteSpace(text) ? [] : [.. text.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4)];

    /// <summary>The audio release date the chapters state (their earliest publish date), when no AUDIOBOOK_SPECIFICS says it.</summary>
    internal static DateOnly? EarliestRelease(ImmutableArray<Episode>.Builder chapters)
    {
        int best = 0;
        foreach (var e in chapters)
            if (e.PublishedAt > 0 && (best == 0 || e.PublishedAt < best)) best = e.PublishedAt;
        return best > 0 ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(best).UtcDateTime) : null;
    }
}
