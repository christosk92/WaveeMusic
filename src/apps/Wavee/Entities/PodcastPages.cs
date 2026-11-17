// ── Entities/PodcastPages.cs — the redesigned podcast and audiobook pages' DATA (view models + the asks) ──────────────
//
// Role: CORE (pure projections of the entity tables) + SHELL (the asks, the per-page watch, the visit stamps)
// Lane: podcast-pages-data (2026-11-17). Contract: C:/WAVEE/scratch/pins/podcast-pages-data-contract.md.
//
// The pages lane draws from three snapshots, ShowPage / EpisodePage / AudiobookPage, built by ShowOf / EpisodeOf /
// AudiobookOf. Each is a pure read of the show/episode tables (+ the return-only Spotify.Books store and the library
// seam) that SUBSCRIBES to what it reads, so calling it in Render() or a Prop.Of thunk re-runs when an answer lands.
// The asks live in Open / LoadMore / Leave: Open ensures the groups through the normal fetch (tickets, batching, store)
// and starts one watch per open show that ensures the member rows as the membership arrives (and pages a book's
// chapter list to the end: the TOC and the book progress need every chapter). No render ever asks.
//
// Facts and where they come from: the contract's §5. Two rules the pages must keep:
//   · the hours line states the ALLOWANCE only (Allowance); Wavee cannot read hours left, so nothing here estimates it;
//   · a chapter's name is shown through ChapterTitle (raw "Track 7"/"07"/"" → "Chapter N"); the raw name stays in the row.

using System.Globalization;
using System.Text.RegularExpressions;
using FluentGpu.Signals;

namespace Wavee;

public enum ShowOrder : byte { Unknown = 0, Serial = 1, Episodic = 2, Recent = 3 }
public enum PlayState : byte { Unplayed = 0, New = 1, InProgress = 2, Played = 3 }
public enum SaveState : byte { Unknown = 0, NotSaved = 1, Saved = 2 }

public readonly record struct Rating(float Average, int Count, int Mine, bool CanRate);
public readonly record struct Person(string Name, string Uri);
public readonly record struct GenreCrumb(string Name, int Level);
public readonly record struct BookAccess(string Signifier, bool Included);
public readonly record struct Money(double Amount, string Currency, string Formatted);
public readonly record struct BookPrice(Money Final, Money List);
public readonly record struct BookPreview(string Mp3Url, Episode Sample, string SampleUri);

public sealed record EpisodeRow(
    Episode Episode, string Uri, string Title, int Number, int DurationMs, long PublishedUnixMs, EpisodeKind Kind,
    PlayState State, int ProgressMs, float Pct, bool IsNew, bool Video, bool Explicit, bool HasTranscript, bool Paywalled);

public sealed record ShowPage(
    Show Show, string Uri, string Title, string Publisher, string ImageUrl, string About, string AboutHtml,
    ShowOrder Order, bool Video, bool Mixed, bool Explicit, Rating? Rating, string[] Categories, Episode Trailer,
    string TrailerUri, SaveState Saved, int TotalEpisodes, EpisodeRow[] Episodes, bool CanLoadMore,
    int NewSinceLastVisit, long LastVisitUnixMs, Episode ListenNext, string Language, bool IsAudiobook, bool Loaded);

public sealed record EpisodePage(
    Episode Episode, string Uri, string Title, Show Show, string ShowTitle, string ImageUrl, string About,
    string AboutHtml, long PublishedUnixMs, int DurationMs, int Number, EpisodeKind Kind, PlayState State,
    int ProgressMs, float Pct, bool Video, bool Explicit, bool HasTranscript, string TranscriptUrl,
    bool IsAudiobookChapter, bool Loaded);

public sealed record ChapterRow(
    Episode Chapter, string Uri, int Number, string RawTitle, string DisplayTitle, int DurationMs, PlayState State,
    int ProgressMs, float Pct, bool Capped, bool Playable);

public sealed record ChapterToc(ChapterRow[] Chapters, int Total, bool Complete);

public readonly record struct BookProgress(
    long ListenedMs, long TotalMs, float Fraction, long LeftMs, Episode Resume, int ResumeNumber, bool Started, bool Finished);

public sealed record AudiobookPage(
    Show Book, string Uri, string Title, string Subtitle, string FullTitle, Person[] Authors, string[] Narrators,
    string Publisher, string Edition, DateOnly? Released, string Language, long TotalDurationMs, Rating? Rating,
    GenreCrumb[] Genres, string About, string AboutHtml, BookAccess Access, BookPrice? Price, string BuyLabel,
    BookPreview? Preview, SaveState Saved, ChapterToc Toc, BookProgress Progress, string[] MoreLikeThis,
    string ImageUrl, bool Loaded);

public static partial class PodcastPages
{
    /// <summary>The spotify.com page that states hours used and left (web-session only; Wavee links, never reads).</summary>
    public const string HoursLeftUrl = "https://www.spotify.com/account/audiobook-listening-time/";
    /// <summary>The top-up purchase page.</summary>
    public const string TopUpUrl = "https://audiobooks.spotify.com/top-up";

    /// <summary>The audiobook-hours allowance (capping-api), null until an answer names it.</summary>
    public static IReadSignal<Spotify.Books.Allowance?> Allowance => Spotify.Books.AllowanceSignal;

    const ShowFields PageGroups = ShowFields.Identity | ShowFields.About | ShowFields.Facts | ShowFields.Rating
                                | ShowFields.Appearance | ShowFields.Topics | ShowFields.Html | ShowFields.Language;
    const EpisodeFields RowGroups = EpisodeFields.Identity | EpisodeFields.Progress | EpisodeFields.Transcript;

    // ══ 1. the reads ═══════════════════════════════════════════════════════════════════════════════════════════════

    public static ShowPage ShowOf(Show show)
    {
        var scope = Read(out var shows, out var episodes);
        if (!show.IsValid) return EmptyShow(show);
        _ = scope.Edges.ShowEpisodes.Changed.Value;
        string uri = show.Uri.Text;
        long lastVisit = LastVisitOf(uri);
        var members = scope.Edges.ShowEpisodes.Targets(show.Slot);
        var rows = new List<EpisodeRow>(members.Length);
        int fresh = 0;
        foreach (int slot in members)
        {
            var e = new Episode(slot);
            if (!e.IsValid) continue;
            var row = RowOf(e, lastVisit);
            if (row.IsNew) fresh++;
            rows.Add(row);
        }
        var order = (ShowOrder)(byte)show.Order;
        int total = show.TotalEpisodes;
        Episode trailer = default;
        string trailerUri = show.Knows(ShowFields.Facts) ? Entities.Strings.Resolve(show.TrailerId) : "";
        if (trailerUri.Length > 0 && episodes.TryGetSlot(show.TrailerId, out int t)) trailer = new Episode(t);
        return new ShowPage(show, uri, show.Title, Entities.Strings.Resolve(show.PublisherId), Entities.Strings.Resolve(show.ImageId),
            Entities.Strings.Resolve(show.DescriptionId), Entities.Strings.Resolve(show.HtmlDescriptionId), order,
            (show.Flags & ShowFlags.Video) != 0, (show.Flags & ShowFlags.Mixed) != 0, (show.Flags & ShowFlags.Explicit) != 0,
            RatingOf(show, null), Lines(Entities.Strings.Resolve(show.TopicsId)), trailer, trailerUri, SavedOf(uri),
            total, rows.ToArray(), rows.Count < total, fresh, lastVisit, ListenNextOf(rows, order), show.Language,
            show.IsAudiobook, show.Knows(ShowFields.Identity | ShowFields.Facts));
    }

    public static EpisodePage EpisodeOf(Episode episode)
    {
        var scope = Read(out var shows, out var episodes);
        if (!episode.IsValid)
            return new EpisodePage(episode, "", "", default, "", "", "", "", 0, 0, 0, EpisodeKind.Full, PlayState.Unplayed, 0, 0,
                false, false, false, "", false, false);
        var row = RowOf(episode, 0);
        var show = episode.Show;
        return new EpisodePage(episode, row.Uri, row.Title, show, show.IsValid ? show.Title : "",
            Entities.Strings.Resolve(episode.ImageId), Entities.Strings.Resolve(episode.DescriptionId),
            Entities.Strings.Resolve(episodes.HtmlDescription[episode.Slot]), row.PublishedUnixMs, row.DurationMs, row.Number,
            row.Kind, row.State, row.ProgressMs, row.Pct, row.Video, row.Explicit, row.HasTranscript, episode.TranscriptUrl,
            (episode.Flags & EpisodeFlags.AudiobookChapter) != 0, episode.Knows(EpisodeFields.Identity));
    }

    public static AudiobookPage AudiobookOf(Show book)
    {
        var scope = Read(out var shows, out var episodes);
        string uri = book.IsValid ? book.Uri.Text : "";
        var meta = uri.Length > 0 ? Spotify.Books.MetaOf(uri) : null;
        var chapterStates = uri.Length > 0 ? Spotify.Books.ChaptersOf(uri) : null;
        var similar = uri.Length > 0 ? Spotify.Books.SimilarOf(uri) : null;
        if (!book.IsValid) return EmptyBook(book);
        _ = scope.Edges.ShowEpisodes.Changed.Value;

        string full = book.Title;
        bool credits = book.Knows(ShowFields.Credits);
        string title = credits && book.BookTitle.Length > 0 ? book.BookTitle : full;
        string subtitle = credits ? book.BookSubtitle : "";
        string description = Entities.Strings.Resolve(book.DescriptionId);

        var authors = AuthorsOf(book);
        string[] narrators = credits && book.Narrators.Length > 0 ? Lines(book.Narrators)
            : meta is { Narrators.Length: > 0 } ? meta.Narrators : NarratorsFromDescription(description);

        DateOnly? released = credits && book.ReleasedAt > 0
            ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(book.ReleasedAt).UtcDateTime) : null;
        string signifier = book.Knows(ShowFields.Access) ? book.Signifier : meta?.Signifier ?? "";
        // "Included" is the account being in an access group (GraphQL); without that answer, a stated signifier is the
        // provider saying it is included ("Included in Premium") — books not in the plan carry an empty one (map §5).
        bool included = meta is { Ok: true } ? meta.Included || signifier.Length > 0 : signifier.Length > 0;
        string sampleUri = book.Knows(ShowFields.Preview) ? book.SampleUri : "";
        if (sampleUri.Length == 0 && book.Knows(ShowFields.Facts)) sampleUri = Entities.Strings.Resolve(book.TrailerId);
        Episode sample = default;
        if (sampleUri.Length > 0 && episodes.TryGetSlot(sampleUri.AsSpan(), out int ss)) sample = new Episode(ss);
        BookPreview? preview = book.Knows(ShowFields.Preview) && book.PreviewUrl.Length > 0
            ? new BookPreview(book.PreviewUrl, sample, sampleUri) : null;

        var toc = TocOf(book, sampleUri, chapterStates);
        long total = credits && book.BookDurationMs > 0 ? book.BookDurationMs : 0;
        var progress = ProgressOf(toc);
        if (total == 0) total = progress.TotalMs;

        return new AudiobookPage(book, uri, title, subtitle, full, authors, narrators,
            credits ? book.BookPublisher : "", credits ? book.Edition : "", released, book.Language, total,
            RatingOf(book, meta), GenresOf(book), StripCredits(description), Entities.Strings.Resolve(book.HtmlDescriptionId),
            new BookAccess(signifier, included), PriceOf(book), book.Knows(ShowFields.Sales) ? book.BuyLabel : "", preview,
            meta is { Ok: true, Saved: true } && SavedOf(uri) == SaveState.Unknown ? SaveState.Saved : SavedOf(uri),
            toc, progress, similar?.Uris ?? [], Entities.Strings.Resolve(book.ImageId),
            book.Knows(ShowFields.Identity | ShowFields.Facts));
    }

    // ══ 2. rules (pure, public so a theme can reuse them) ═════════════════════════════════════════════════════════

    [GeneratedRegex(@"^\s*(Track\s*\d+|\d+)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberedName();

    /// <summary>The display name of chapter <paramref name="number"/>: "Chapter N" (localized) when the wire's name is
    /// empty, <c>Track N</c> or a bare number (the publisher shipped numbered files, map §9); otherwise the name as sent.</summary>
    public static string ChapterTitle(string raw, int number)
        => string.IsNullOrWhiteSpace(raw) || NumberedName().IsMatch(raw)
            ? Strings.Podcast.Pages.ChapterN(number.ToString(CultureInfo.CurrentCulture))
            : raw;

    /// <summary>The audiobook description's own credit prefix ("Author(s): …\nNarrator(s): …\n\n") is the only narrator
    /// source when kind 52 and the GraphQL read are both absent; the page shows credits from the fields, so the prefix is
    /// stripped from About.</summary>
    public static string[] NarratorsFromDescription(string description)
    {
        foreach (var line in description.Split('\n'))
            if (line.StartsWith("Narrator(s):", StringComparison.Ordinal))
                return line["Narrator(s):".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return [];
    }

    public static string StripCredits(string description)
    {
        if (!description.StartsWith("Author(s):", StringComparison.Ordinal)) return description;
        int body = description.IndexOf("\n\n", StringComparison.Ordinal);
        return body < 0 ? description : description[(body + 2)..];
    }

    /// <summary>Σ chapter progress / Σ chapter durations; a finished chapter counts whole. Resume = the in-progress
    /// chapter played last, else the first unfinished chapter after the last finished one, else chapter 1.</summary>
    public static BookProgress ProgressOf(ChapterToc toc)
    {
        long listened = 0, total = 0, latestPlayed = -1;
        int lastFinished = -1, resumeAt = -1;
        var chapters = toc.Chapters;
        for (int i = 0; i < chapters.Length; i++)
        {
            var c = chapters[i];
            total += c.DurationMs;
            listened += c.State == PlayState.Played ? c.DurationMs : Math.Min(c.ProgressMs, c.DurationMs);
            if (c.State == PlayState.Played) lastFinished = i;
            if (c.State == PlayState.InProgress)
            {
                long played = c.Chapter.IsValid ? c.Chapter.PlayedAt : 0;
                if (played > latestPlayed) { latestPlayed = played; resumeAt = i; }
            }
        }
        if (resumeAt < 0)
            for (int i = lastFinished + 1; i < chapters.Length; i++)
                if (chapters[i].State != PlayState.Played) { resumeAt = i; break; }
        bool finished = chapters.Length > 0 && toc.Complete && chapters.All(c => c.State == PlayState.Played);
        if (resumeAt < 0 && chapters.Length > 0) resumeAt = 0;
        var resume = resumeAt >= 0 ? chapters[resumeAt] : null;
        return new BookProgress(listened, total, total > 0 ? Math.Clamp(listened / (float)total, 0f, 1f) : 0f,
            Math.Max(0, total - listened), resume?.Chapter ?? default, resume?.Number ?? 0, listened > 0, finished);
    }

    // ══ 3. the asks ════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly Dictionary<string, long> s_openVisit = [];
    static readonly Dictionary<int, Effect> s_watches = [];
    static ReactiveRuntime? s_runtime;
    static bool s_flushPosted;
    static readonly Action s_flush = static () => { s_flushPosted = false; s_runtime?.Flush(); };

    /// <summary>The page opened (UI thread; idempotent). Ensures the page's groups, starts the member-row watch, the
    /// book's GraphQL reads, and pins the PREVIOUS visit stamp for this page's life.</summary>
    public static void Open(Show show)
    {
        if (!show.IsValid) return;
        string uri = show.Uri.Text;
        if (!s_openVisit.ContainsKey(uri)) s_openVisit[uri] = VisitStamps.Read(VisitStamps.Blob(), uri);
        Entities.Ensure(show, PageGroups);
        if (show.IsAudiobook)
        {
            Entities.Ensure(show, ShowFields.Book);
            Spotify.Books.Ask(uri);
        }
        if (Entities.Current.Edges.ShowEpisodes.State(show.Slot) == EdgeState.Unknown)
            Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot);
        Watch(show);
    }

    public static void Open(Episode episode)
    {
        if (!episode.IsValid) return;
        Entities.Ensure(episode, EpisodeFields.All);
        if (episode.Show.IsValid) Entities.Ensure(episode.Show, ShowFields.Identity | ShowFields.Facts);
    }

    /// <summary>Ask the next page of a podcast's membership (a book pages itself to the end).</summary>
    public static void LoadMore(Show show)
    {
        if (!show.IsValid) return;
        var edge = Entities.Current.Edges.ShowEpisodes;
        int from = edge.Count(show.Slot);
        if (from < edge.Total(show.Slot) && !edge.WasAsked(show.Slot, from))
            Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot, from);
    }

    /// <summary>The page closed: stop its watch and stamp the visit ("new since last visit" counts from now).</summary>
    public static void Leave(Show show)
    {
        if (!show.IsValid) return;
        if (s_watches.Remove(show.Slot, out var watch)) watch.Dispose();
        string uri = show.Uri.Text;
        s_openVisit.Remove(uri);
        if (!Platform.Args.Fake) VisitStamps.Save(uri, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>The visit stamp a page shows: the PREVIOUS one while open, else the stored one. 0 = never visited.</summary>
    public static long LastVisitOf(string uri)
        => s_openVisit.TryGetValue(uri, out long open) ? open : VisitStamps.Read(VisitStamps.Blob(), uri);

    /// <summary>The fixture loader's door: pin a visit stamp for <paramref name="uri"/> without persisting it.</summary>
    internal static void PinVisit(string uri, long unixMs) => s_openVisit[uri] = unixMs;

    static void Watch(Show show)
    {
        if (s_watches.ContainsKey(show.Slot)) return;
        s_runtime ??= new ReactiveRuntime
        {
            FrameRequested = static () => { if (s_flushPosted) return; s_flushPosted = true; Spotify.Post(s_flush); },
        };
        int slot = show.Slot;
        var scope = Entities.Current;
        s_watches[slot] = new Effect(s_runtime, () => DemandMembers(scope, slot));
    }

    /// <summary>The watch body: as the membership lands, ensure each member's row groups (and a book's chapter capping),
    /// and page a book's chapter list to the end. Reads the edge's Changed and the episodes' Changed; writes only asks.</summary>
    static void DemandMembers(Scope scope, int slot)
    {
        if (!ReferenceEquals(scope, Entities.Current)) return;
        _ = scope.Edges.ShowEpisodes.Changed.Value;
        _ = scope.Shows.Changed.Value;
        var show = new Show(slot);
        if (!show.IsValid) return;
        var members = scope.Edges.ShowEpisodes.Targets(slot);
        if (members.IsEmpty) return;
        var rows = new Episode[members.Length];
        for (int i = 0; i < rows.Length; i++) rows[i] = new Episode(members[i]);
        bool book = (show.Flags & ShowFlags.Audiobook) != 0;
        Entities.Ensure(rows, book ? RowGroups | EpisodeFields.Capping : RowGroups, FetchPriority.Visible);
        if (book) LoadMore(show);
    }

    // ══ 4. helpers ═════════════════════════════════════════════════════════════════════════════════════════════════

    static Scope Read(out ShowTable shows, out EpisodeTable episodes)
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        shows = scope.Shows;
        episodes = scope.Episodes;
        _ = shows.Changed.Value;
        _ = episodes.Changed.Value;
        return scope;
    }

    static EpisodeRow RowOf(Episode e, long lastVisitMs)
    {
        float pct = Episode.Rules.PctOf(e);
        bool played = e.Completed;
        long published = e.PublishedAt > 0 ? e.PublishedAt * 1000L : 0;
        bool isNew = lastVisitMs > 0 && published > lastVisitMs && !played && !Episode.Rules.HasRule(pct);
        var state = played || Episode.Rules.Played(pct) ? PlayState.Played
                  : Episode.Rules.InProgress(pct) ? PlayState.InProgress
                  : isNew ? PlayState.New : PlayState.Unplayed;
        var flags = e.Flags;
        return new EpisodeRow(e, e.Uri.Text, e.Title, e.Number, e.DurationMs, published, e.Kind, state,
            e.Knows(EpisodeFields.Progress) ? e.ProgressMs : 0, pct, isNew,
            (flags & EpisodeFlags.Video) != 0, (flags & EpisodeFlags.Explicit) != 0,
            (flags & EpisodeFlags.HasTranscript) != 0, (flags & EpisodeFlags.Paywalled) != 0);
    }

    /// <summary>Serial: the first unfinished episode after the last finished one, walking oldest first. Episodic (and
    /// unknown): the newest unplayed, else the newest.</summary>
    static Episode ListenNextOf(List<EpisodeRow> rows, ShowOrder order)
    {
        if (rows.Count == 0) return default;
        var full = rows.Where(r => r.Kind != EpisodeKind.Trailer).ToList();
        if (full.Count == 0) return default;
        if (order == ShowOrder.Serial)
        {
            var oldestFirst = full.OrderBy(r => r.Number > 0 ? r.Number : int.MaxValue).ThenBy(r => r.PublishedUnixMs).ToList();
            int last = oldestFirst.FindLastIndex(r => r.State == PlayState.Played);
            for (int i = last + 1; i < oldestFirst.Count; i++)
                if (oldestFirst[i].State != PlayState.Played) return oldestFirst[i].Episode;
            return oldestFirst[0].Episode;
        }
        var newestFirst = full.OrderByDescending(r => r.PublishedUnixMs).ToList();
        var inProgress = newestFirst.FirstOrDefault(r => r.State == PlayState.InProgress);
        if (inProgress is not null) return inProgress.Episode;
        return (newestFirst.FirstOrDefault(r => r.State != PlayState.Played) ?? newestFirst[0]).Episode;
    }

    static ChapterToc TocOf(Show book, string sampleUri, Spotify.Books.BookChapters? states)
    {
        var scope = Entities.Current;
        var members = scope.Edges.ShowEpisodes.Targets(book.Slot);
        Dictionary<string, Spotify.Books.ChapterState>? byUri = null;
        if (states is { Ok: true, Items.Length: > 0 })
        {
            byUri = new(StringComparer.Ordinal);
            foreach (var c in states.Items) byUri[c.Uri] = c;
        }
        var list = new List<(Episode E, int Pos)>();
        for (int i = 0; i < members.Length; i++)
        {
            var e = new Episode(members[i]);
            if (!e.IsValid) continue;
            if (e.Kind == EpisodeKind.Trailer) continue;
            if (sampleUri.Length > 0 && string.Equals(e.Uri.Text, sampleUri, StringComparison.Ordinal)) continue;
            list.Add((e, list.Count));
        }
        // Book order = the wire number (field 65) when every chapter states one, else the membership order.
        bool numbered = list.Count > 0 && list.All(c => c.E.Number > 0);
        if (numbered) list.Sort(static (a, b) => a.E.Number != b.E.Number ? a.E.Number.CompareTo(b.E.Number) : a.Pos.CompareTo(b.Pos));
        var rows = new ChapterRow[list.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var e = list[i].E;
            int number = numbered ? e.Number : i + 1;
            string uri = e.Uri.Text;
            string raw = e.Title;
            Spotify.Books.ChapterState? gql = null;
            byUri?.TryGetValue(uri, out gql);
            if (raw.Length == 0 && gql is not null) raw = gql.Name;
            int duration = e.DurationMs > 0 ? e.DurationMs : gql?.DurationMs ?? 0;
            int progress; float pct; bool played;
            if (e.Knows(EpisodeFields.Progress) || gql is null)
            {
                progress = e.Knows(EpisodeFields.Progress) ? Math.Min(e.ProgressMs, Math.Max(duration, 0)) : 0;
                pct = Episode.Rules.PctOf(e);
                played = e.Completed;
            }
            else
            {
                progress = gql.PositionMs;
                played = gql.State == "COMPLETED";
                pct = played ? 1f : Episode.Rules.Pct(progress, duration);
            }
            var state = played || Episode.Rules.Played(pct) ? PlayState.Played
                      : Episode.Rules.InProgress(pct) ? PlayState.InProgress : PlayState.Unplayed;
            if (state == PlayState.Played) progress = duration;
            rows[i] = new ChapterRow(e, uri, number, raw, ChapterTitle(raw, number), duration, state, progress, pct,
                e.Capping == 1, gql?.Playable ?? (e.Flags & EpisodeFlags.Unplayable) == 0);
        }
        var edge = scope.Edges.ShowEpisodes;
        int total = Math.Max(0, edge.Total(book.Slot) - (sampleUri.Length > 0 && Contains(members, sampleUri) ? 1 : 0));
        bool complete = edge.Count(book.Slot) >= edge.Total(book.Slot) && edge.Total(book.Slot) > 0;
        return new ChapterToc(rows, Math.Max(total, rows.Length), complete);
    }

    static bool Contains(ReadOnlySpan<int> members, string uri)
    {
        foreach (int m in members)
            if (string.Equals(new Episode(m).Uri.Text, uri, StringComparison.Ordinal)) return true;
        return false;
    }

    static Person[] AuthorsOf(Show book)
    {
        if (book.Knows(ShowFields.Authors) && book.AuthorLinks.Length > 0)
            return Lines(book.AuthorLinks).Select(static l =>
            {
                int tab = l.IndexOf('\t');
                return tab < 0 ? new Person(l, "") : new Person(l[..tab], l[(tab + 1)..]);
            }).ToArray();
        if (book.Knows(ShowFields.Credits) && book.BookAuthors.Length > 0)
            return Lines(book.BookAuthors).Select(static n => new Person(n, "")).ToArray();
        // ShowV4 field 66 on a book IS the author (map §4.1), the one credit a cold row has.
        string byline = Entities.Strings.Resolve(book.PublisherId);
        return byline.Length > 0 ? [new Person(byline, "")] : [];
    }

    static GenreCrumb[] GenresOf(Show book)
    {
        if (!book.Knows(ShowFields.Genres) || book.Genres.Length == 0) return [];
        return Lines(book.Genres).Select(static l =>
        {
            int tab = l.IndexOf('\t');
            int level = tab > 0 && int.TryParse(l.AsSpan(0, tab), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 1;
            return new GenreCrumb(tab < 0 ? l : l[(tab + 1)..], level);
        }).ToArray();
    }

    static BookPrice? PriceOf(Show book)
    {
        if (!book.Knows(ShowFields.Price)) return null;
        var final = MoneyOf(book.PriceFinal);
        if (final.Formatted.Length == 0 && final.Amount <= 0) return null;
        return new BookPrice(final, MoneyOf(book.PriceList));
    }

    static Money MoneyOf(string text)
    {
        if (text.Length == 0) return default;
        var parts = text.Split('\t');
        double amount = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ? a : 0;
        return new Money(amount, parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
    }

    static Rating? RatingOf(Show show, Spotify.Books.BookMeta? meta)
    {
        int mine = meta?.MyRating ?? show.MyRating;
        bool canRate = meta?.CanRate ?? (show.Flags & ShowFlags.CanRate) != 0;
        if (show.Knows(ShowFields.Rating) && show.RatingX100 > 0)
            return new Rating(show.RatingX100 / 100f, show.RatingCount, mine, canRate);
        if (meta is { Ok: true, ShowAverage: true, RatingAverage: > 0 })
            return new Rating(meta.RatingAverage, meta.RatingCount, mine, canRate);
        return null;
    }

    static SaveState SavedOf(string uri)
    {
        if (TryFixtureSaved(uri, out bool fixtureSaved)) return fixtureSaved ? SaveState.Saved : SaveState.NotSaved;
        if (Controls.Library is not { } lib) return SaveState.Unknown;
        return lib.IsSaved(uri) ? SaveState.Saved : SaveState.NotSaved;
    }

    static string[] Lines(string text)
        => text.Length == 0 ? [] : text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    static ShowPage EmptyShow(Show show)
        => new(show, "", "", "", "", "", "", ShowOrder.Unknown, false, false, false, null, [], default, "", SaveState.Unknown,
            0, [], false, 0, 0, default, "", false, false);

    static AudiobookPage EmptyBook(Show book)
        => new(book, "", "", "", "", [], [], "", "", null, "", 0, null, [], "", "", default, null, "", null, SaveState.Unknown,
            new ChapterToc([], 0, false), default, [], "", false);
}

/// <summary>The per-show "last visit" stamps (<c>podcast.visits</c>): "showUri=unixMs" entries, most recent first, capped.</summary>
public static class VisitStamps
{
    public const int Cap = 128;

    public static string Blob() => Platform.Settings.Get(Platform.Keys.PodcastVisits);

    public static void Save(string showUri, long unixMs)
    {
        string blob = Blob();
        string next = Write(blob, showUri, unixMs);
        if (!string.Equals(next, blob, StringComparison.Ordinal)) Platform.Settings.Set(Platform.Keys.PodcastVisits, next);
    }

    public static long Read(string? blob, string showUri)
    {
        foreach (var entry in (blob ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = entry.LastIndexOf('=');
            if (eq > 0 && entry.AsSpan(0, eq).SequenceEqual(showUri)
                && long.TryParse(entry.AsSpan(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                return ms;
        }
        return 0;
    }

    public static string Write(string? blob, string showUri, long unixMs)
    {
        if (showUri.Length == 0 || showUri.IndexOfAny([';', '=']) >= 0) return blob ?? "";
        var parts = new List<string> { showUri + "=" + unixMs.ToString(CultureInfo.InvariantCulture) };
        foreach (var entry in (blob ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (parts.Count >= Cap) break;
            int eq = entry.LastIndexOf('=');
            if (eq <= 0 || entry.AsSpan(0, eq).SequenceEqual(showUri)) continue;
            parts.Add(entry);
        }
        return string.Join(';', parts);
    }
}
