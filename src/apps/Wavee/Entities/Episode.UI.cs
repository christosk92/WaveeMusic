// ── Entities/Episode.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the episode READER ROW (W3) — the show reader's rows, the visit head's "new since you were here" — its figure-space
// seed twin, and the row's own decisions: the pct the reader reads, "N min left", the duration words, the title without
// its number, now-playing and the disc's click
//
// Role: UI (with pure statics)
// Owner: E
// Wave: P3 (the podcast rework)
// Budget: 520 lines (podcast-show-rework-implementation.md §11)
// Spec: podcast-show-rework-implementation.md §2 W3 (the row states), §4 (row = link, the disc plays, ⋯ = the menu),
//       §5.7, §7 (the cluster's 120 ms fade, the disc's emphatic scale) · the prototype podcast-show-episode-mica.html
//       (.erow, .acts, .disc, .mini-ic, .eq) · 0d0429a0 `ShowEpisodeRow.xaml.cs:263-272` (the number-prefix strip)
//
// ── ONE ROW, BOUND ───────────────────────────────────────────────────────────────────────────────────────────────────
//
// `ReaderRow` is built ONCE per slot (the list's template, re-run only when the slot's kind or the reader's width arm
// flips), and every per-episode value is a bind over the slot's equality-gated `RowItem` — the handle, its row version
// and the host's per-row marks — so a recycle re-binds and allocates nothing: titles and descriptions resolve interned
// strings; the date, the duration, "N min left" and the stripped title go through bounded `FormatCache`s; the rule is a
// compositor scale. The visit head's handful of eager rows reuse the SAME template over a fixed scope (`Fixed`).
//
// ── THE STATES (W3) ──────────────────────────────────────────────────────────────────────────────────────────────────
//
//   REST        numeral 30/300 tertiary (46, right) · art 56 r6 · title 14/600 ≤ 2 + chips · blurb 12.5 ≤ 2 · date · length
//   HOVER       the whole row FillSubtleSecondary; the cluster (queue · mark · ♥ · ⋯ + the 36 tone disc) fades in, 120 ms
//   IN PROGRESS the meta leads with "23 min left" 12/600 in the tone; a 3-DIP rule ≤ 340 under it
//   PLAYED      numeral, art and copy at 58 % · "✓ played" · no rule
//   NOW PLAYING a 3-DIP tone bar on the row's left edge · the title in the tone · the equalizer before it
//   NEW         "new" 10.5/700 tone, +60 tracking, before the date (published after the show's last play — the host's mark)
//   ROW = LINK  the whole row is a Hyperlink tab stop that opens the episode page; the disc plays (D-3)
//   NARROW      no numeral, art 48, the cluster is the disc alone and always visible
//
// ONE COMPLETION RULE (D-5): the row reads `ReaderPct` — a completed episode (≥ .98 or ≤ 30 s left) is 1 — so "played",
// the rule and the host's filter/ledger can never disagree about one row.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Episode
{
    // ══ 1. WHAT A ROW BINDS ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the HOST knows about a row that the episode does not: <see cref="Fresh"/> — published after the
    /// show's last play (<c>ShowLedger.IsFresh</c>, only once progress settled); <see cref="NoRule"/> — the row follows a
    /// month header or a section head, so its top hairline is dropped (the prototype's <c>.group + .erow</c>).</summary>
    [Flags]
    public enum RowMarks : byte
    {
        None = 0,
        Fresh = 1,
        NoRule = 2,
    }

    /// <summary>What one bound row slot binds: the handle, its row version and the host's marks — so a data change to
    /// THIS episode (or its marks) re-fires the slot's binds and a publish of any other row does not (the slot item is
    /// equality-gated).</summary>
    /// <param name="Number">A number the HOST states for this row, 0 = "the episode's own". An audiobook's chapters
    /// carry no <c>number</c> on the wire (<see cref="AudiobookOrder"/>), so the reader numbers them by POSITION and
    /// hands the number down here — the row never recomputes an order it cannot see.</param>
    public readonly record struct RowItem(Episode Episode, uint Version, RowMarks Marks = RowMarks.None, int Number = 0)
    {
        public static RowItem Of(Episode e, RowMarks marks = RowMarks.None, int number = 0)
            => new(e, e.IsValid ? e.Version : 0u, marks, number);
    }

    /// <summary>What a row needs from its host, built ONCE per host: the show's tone (a live read), the disc's verb, the
    /// row's own verb (null = the episode page), and the context menu (absent without an overlay).</summary>
    public sealed class RowContext
    {
        public required Func<ColorF> Tone { get; init; }
        /// <summary>The disc. The host plays its own context at the episode; <see cref="Invoke"/> is how it toggles the
        /// now-playing row.</summary>
        public required Action<Episode> Play { get; init; }
        /// <summary>The row click / Enter. Null = <see cref="OpenPage"/>.</summary>
        public Action<Episode>? Open { get; init; }
        public IOverlayService? Overlay { get; init; }
        public Func<Episode, ContextMenuModel?>? Menu { get; init; }
        /// <summary>Multi-select mode, when the row is hosted inside a selection surface. Null = no selection
        /// affordance (the row is a plain link).
        /// <para>B2 (plan §3.3): narrowed from the deleted <c>EpisodeSelection</c> to the two members
        /// <see cref="ReaderRowContent"/> actually reads — <c>Track.Table</c>'s rows carry their OWN selection state
        /// through <c>RowScope</c>/<c>host.Skin</c> (the check lane, the row fill) and reach this seam only for the
        /// hyperlink-vs-toggle click decision. A caller still using the retired per-page selection model (the show
        /// reader, until the S-reader wave migrates it onto <c>Track.Table</c>) adapts to this shape instead.</para></summary>
        public RowSelectionContext? Selection { get; init; }
    }

    /// <summary>The two members <see cref="ReaderRowContent"/> needs from whatever owns row selection: is multi-select
    /// armed, and how to clear it (Escape, the batch bar's ✕). See <see cref="RowContext.Selection"/>.</summary>
    public sealed class RowSelectionContext
    {
        public required Signal<bool> Selecting { get; init; }
        public required Action Clear { get; init; }
    }

    // ── the metrics (W3; the prototype's .erow) ──
    public const float NumeralWidth = 46f, RowArt = 64f, RowArtNarrow = 48f, RowDisc = 36f, RowDiscGlyph = 14f;
    public const float RowGap = 12f, RowPadX = 0f, RowPadY = 12f, RowRadius = 6f, PlayedInk = 0.85f;
    public const float RuleHeight = 3f, RuleMaxWidth = 340f, ToneBarWidth = 3f, ActionBox = 30f;
    const float RuleCorner = 2f, ArtRadius = 6f, ClusterFadeMs = 120f, EqualizerHeight = 11f;
    const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>The explicit mark — the same letter <c>Controls</c>' EXPLICIT badge carries in every locale.</summary>
    const string ExplicitMark = "E";

    /// <summary>Segoe Fluent's Lock (U+E72E) — not in the engine's glyph table; built from its code point so the source
    /// stays ASCII (the <c>WaveeIcons</c> convention).</summary>
    static readonly string LockGlyph = ((char)0xE72E).ToString();

    /// <summary>U+2007 FIGURE SPACE runs: a derived shimmer draws a bar only for a run that MEASURES.</summary>
    static readonly string SeedTitle = new((char)0x2007, 22);
    static readonly string SeedDescription = new((char)0x2007, 34);
    const int SeedDateKey = 20000101;
    const int SeedDurationMs = 180_000;

    static readonly ContextMenuOptions s_menuOptions = new();
    static readonly Action<Episode> s_openPage = OpenPage;

    // ══ 2. THE PURE PIECES (pinned by EpisodeRowRulesTests) ══════════════════════════════════════════════════════════

    /// <summary>THE pct the reader reads (D-5): an episode completed by <see cref="Rules.Completed"/> reads 1, so
    /// <see cref="Rules.Played(float)"/> agrees with the one completion rule; anything else is <see cref="Rules.Pct"/>.</summary>
    public static float ReaderPct(int progressMs, int durationMs)
        => Rules.Completed(progressMs, durationMs) ? 1f : Rules.Pct(progressMs, durationMs);

    /// <summary><see cref="ReaderPct(int,int)"/> of a row; progress UNKNOWN reads 0 — no state, never a guess.</summary>
    public static float ReaderPctOf(Episode e)
        => e.IsValid ? e.Completed ? 1f : e.Knows(EpisodeFields.Progress) ? ReaderPct(e.ProgressMs, e.DurationMs) : 0f : 0f;

    /// <summary>THE reveal rule — what one reader surface (a row, an up-next chip, the continue hero) shows for an
    /// episode, from four column facts and nothing else. Pure, so a test pins the table of outcomes without a table:
    /// <list type="bullet">
    /// <item>no row at all (<paramref name="valid"/> false) → <see cref="LoadState.Pending"/> — a recycled slot holds
    /// the default item for a frame, and a shimmer is the only honest thing to draw for it;</item>
    /// <item>the title is known → <see cref="LoadState.Ready"/> when it has text, else <see cref="LoadState.Failed"/> —
    /// an answered-but-empty identity is a definite failure, not a shimmer that never ends;</item>
    /// <item>the title is not known → <see cref="LoadState.Failed"/> once the ask for it terminally failed
    /// (<c>Table.Failed</c>: a transport failure's <c>MarkFailed</c>, or <c>FetchMissPolicy</c>'s seal after the
    /// wire omitted the row twice), else <see cref="LoadState.Pending"/>.</item>
    /// </list>
    /// Whoever asks for the facts is a different question (<c>ShowReaderRules.RowDemand</c>, the reader host's own
    /// effect): this rule only says what to paint for what the columns hold.</summary>
    public static LoadState RevealState(bool valid, bool knowsTitle, bool hasTitle, bool failed)
    {
        if (!valid) return LoadState.Pending;
        if (knowsTitle) return hasTitle ? LoadState.Ready : LoadState.Failed;
        return failed ? LoadState.Failed : LoadState.Pending;
    }

    /// <summary>Whole minutes left, at least 1 (the prototype's <c>max(1, round(dur × (1 − pct)))</c>); 0 when the
    /// duration is unknown. A position past the end reads as the end.</summary>
    public static int LeftMinutes(int progressMs, int durationMs)
    {
        if (durationMs <= 0) return 0;
        long left = (long)durationMs - Math.Clamp(progressMs, 0, durationMs);
        return (int)Math.Max(1L, (left + 30_000L) / 60_000L);
    }

    /// <summary>"17 min" / "1 hr 5 min" — the detail frame's duration words (never a hard-coded suffix).</summary>
    public static string DurationWords(int minutes)
        => minutes >= 60 ? Strings.Detail.DurationHrMin(minutes / 60, minutes % 60) : Strings.Detail.DurationMin(Math.Max(1, minutes));

    /// <summary>"17 min left" through <c>podcast.left</c>, cached by whole minutes (a bind re-fire formats nothing it
    /// has formatted before); empty when the duration is unknown.</summary>
    public static string LeftLabel(int progressMs, int durationMs)
        => durationMs <= 0 ? "" : s_left.Get(LeftMinutes(progressMs, durationMs), s_leftFormat);

    /// <summary>The row's length ("28 min", "1 hr 5 min"), cached by whole minutes; empty when unknown.</summary>
    public static string DurationLabel(int durationMs)
        => durationMs <= 0 ? "" : s_durations.Get(Rules.Minutes(durationMs), s_durationFormat);

    /// <summary>The title without a leading episode number the numeral already shows (0d0429a0
    /// <c>StripEpisodeNumberPrefix</c>, made stricter): <c>#123 - Title</c>, <c>#123: Title</c>, <c>#14 · Title</c>,
    /// <c>123. Title</c> → <c>Title</c> — but only when the digits ARE <paramref name="number"/>, and a bare number
    /// (no <c>#</c>) needs real punctuation after it, so "1983 Days" keeps its year. Nothing after the prefix, or no
    /// number, returns the title unchanged (the same instance). Pure.</summary>
    public static string TitleSansNumber(string title, int number)
    {
        if (number <= 0 || string.IsNullOrEmpty(title)) return title ?? "";
        var s = title.AsSpan();
        bool hash = s[0] == '#';
        int i = hash ? 1 : 0, start = i;
        long value = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]) && i - start < 9) value = value * 10 + (s[i++] - '0');
        if (i == start || value != number || (i < s.Length && char.IsAsciiDigit(s[i]))) return title;
        int sep = i;
        bool punctuated = false;
        while (i < s.Length && IsPrefixSeparator(s[i]))
        {
            if (s[i] is not (' ' or NoBreakSpace)) punctuated = true;
            i++;
        }
        if (i == sep || i >= s.Length || (!hash && !punctuated)) return title;
        return title[i..];
    }

    static bool IsPrefixSeparator(char c)
        => c is ' ' or NoBreakSpace or '-' or ':' or '.' or '|' or MiddleDot or EnDash or EmDash;

    // The separators by code point, so the source stays ASCII (raw non-ASCII literals get mangled by the edit chain).
    const char NoBreakSpace = (char)0x00A0, MiddleDot = (char)0x00B7, EnDash = (char)0x2013, EmDash = (char)0x2014;

    /// <summary>Does the title start the way a number prefix does? The cheap gate before the cache.</summary>
    static bool HasNumberPrefix(string title) => title.Length > 0 && (title[0] == '#' || char.IsAsciiDigit(title[0]));

    // ── the caches (one instance per call site, never per row) ──
    static readonly FormatCache<int> s_dates = new();
    static readonly FormatCache<int> s_left = new();
    static readonly FormatCache<int> s_durations = new();
    static readonly FormatCache<(string Title, int Number)> s_titles = new();
    /// <summary>The row's date, from the <see cref="DateKeys"/> DAY key the row binds. Total by construction: a key the
    /// family did not mint (a month key, a truncated one, a zero month or day) reads "" — this thunk runs on the render
    /// path, where a thrown <c>DateTime</c> constructor is a crashed app loop.</summary>
    static readonly Func<int, string> s_dateFormat = static key => DateKeys.DayLabel(key, CultureInfo.CurrentCulture);
    static readonly Func<int, string> s_leftFormat = static minutes => Strings.Podcast.Left(DurationWords(minutes));
    static readonly Func<int, string> s_durationFormat = static minutes => DurationWords(minutes);
    static readonly Func<(string Title, int Number), string> s_strip = static k => TitleSansNumber(k.Title, k.Number);

    /// <summary>The row's <see cref="DateKeys"/> day key (<c>year*10000 + month*100 + day</c>) in the LISTENER's clock;
    /// <see cref="DateKeys.None"/> when the date is unknown.
    /// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <c>Playlist.UI.cs</c>) and the
    /// row's date is exactly the decision the crash came out of, so a fact pins it.</para></summary>
    public static int DateKey(int unixSeconds) => DateKeys.DayKeyOfLocal(unixSeconds);

    /// <summary>"MMM d" for a unix-seconds stamp, cached by day; empty for an unknown date (never invented) and for
    /// any key the day-key family did not mint — it cannot throw.</summary>
    public static string DateLabel(int unixSeconds) => s_dates.Get(DateKey(unixSeconds), s_dateFormat);

    // ══ 3. NOW PLAYING, THE DISC, THE LINK ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Is this the playable the deck is on? A SUBSCRIBING read of <c>Playback.CurrentId</c>, compared by
    /// identity (the <see cref="Track.IsNowPlaying"/> twin).</summary>
    public static bool IsNowPlaying(Episode e)
    {
        var playing = Playback.CurrentId.Value;
        return e.Slot > 0 && e.IsValid && !playing.IsEmpty && playing == e.Id;
    }

    /// <summary>The disc's single click (the <see cref="Track.Invoke"/> twin): the now-playing episode toggles pause /
    /// resume, any other runs <paramref name="startDifferent"/>. A peek — this is a click, not a render.</summary>
    public static void Invoke(Episode e, Action startDifferent)
    {
        var playing = Playback.CurrentId.Peek();
        bool deckRow = e.Slot > 0 && e.IsValid && !playing.IsEmpty && playing == e.Id;
        if (Shell.PlayerBarRules.RowVerb(deckRow, Playback.Error.Peek()) == Shell.RowAction.Toggle)
        {
            Playback.TogglePlay();
            return;
        }
        startDifferent();
    }

    /// <summary>The row's verb (D-3): the episode page.</summary>
    public static void OpenPage(Episode e)
    {
        if (!e.IsValid || !e.Uri.IsValid) return;
        Shell.GoTo(Shell.For(e.Uri, TitleOf(e)));
    }

    /// <summary>Play next / add to queue through the queue owner's seam, with the one toast (the seam itself is quiet).</summary>
    public static void Enqueue(Episode e, bool next)
    {
        if (!e.IsValid || !e.Uri.IsValid) return;
        var s = Actions.Services;
        if ((next ? s.PlayNext : s.AddToQueue) is not { } enqueue)
        {
            Notify.Say(Loc.Get(Strings.Detail.QueueUnavailable), InfoBarSeverity.Warning);
            return;
        }
        enqueue(e.Uri);
        Notify.Say(Strings.Detail.AddedToQueue(Strings.Podcast.EpisodeCount(1)), InfoBarSeverity.Success);
    }

    // ── selectors (every one guarded: a recycled slot can hold the default item for a frame) ──

    static string TitleOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Title) ? e.Title : "";
    static bool HasDescription(Episode e) => e.IsValid && e.Knows(EpisodeFields.About) && !e.DescriptionId.IsEmpty;
    static string DescriptionOf(Episode e) => HasDescription(e) ? Entities.Strings.Resolve(e.DescriptionId) : "";
    static string? ArtOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null;
    static int DateKeyOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Published) ? DateKey(e.PublishedAt) : DateKeys.None;
    static EpisodeFlags FlagsOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Title) ? e.Flags : EpisodeFlags.None;
    static EpisodeKind KindOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Title) ? e.Kind : EpisodeKind.Full;
    static int NumberOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Title) ? e.Number : 0;
    static string NumeralText(Episode e) { int n = NumberOf(e); return n > 0 ? FormatCache.Int(n) : ""; }

    /// <summary>The row's numeral: the HOST's number when it stated one (an audiobook chapter's POSITION — the wire
    /// carries none), else the episode's own.</summary>
    static string NumeralOf(RowItem r) => r.Number > 0 ? FormatCache.Int(r.Number) : NumeralText(r.Episode);
    static string DurationLabelOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Duration) ? DurationLabel(e.DurationMs) : "";
    static bool InProgressOf(Episode e) => Rules.InProgress(PlaybackProgressOf(e).Pct);
    static bool PlayedOf(Episode e) => Rules.Played(PlaybackProgressOf(e).Pct);
    static string LeftOf(Episode e) => InProgressOf(e) ? PlaybackProgressOf(e).LeftLabel : "";
    static bool IsNew(in RowItem r) => (r.Marks & RowMarks.Fresh) != 0 && PlaybackProgressOf(r.Episode).Pct <= Rules.InProgressFloor;

    /// <summary>The wide arm's title: the number prefix stripped when the numeral carries it (cached per title).</summary>
    static string ShownTitle(Episode e)
    {
        string title = TitleOf(e);
        int n = NumberOf(e);
        return n > 0 && HasNumberPrefix(title) ? s_titles.Get((title, n), s_strip) : title;
    }

    // ══ 4. THE ROW ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The reader row (W3) for a bound slot. Build it ONCE per slot (template time) — <paramref name="narrow"/>
    /// is the width arm the slot was built for (a flip re-runs the slot's render); every per-episode value binds, and
    /// the verbs resolve the slot's CURRENT item at invocation, never one captured at build time.</summary>
    public static Element ReaderRow(in BoundItemScope<RowItem> item, RowContext ctx, bool narrow)
    {
        var bound = item;
        var content = ReaderRowContent(in bound, ctx, narrow, seed: false);
        return new SkelRegionEl(
            Pending: () => ReaderLoadState(bound.Item.Value.Episode) == LoadState.Pending,
            Failed: () => ReaderLoadState(bound.Item.Value.Episode) == LoadState.Failed,
            Content: () => content,
            ShimmerSource: () => SeedReaderRow(narrow),
            OnFailed: () => ReaderFailure(bound.Item.Value.Episode, narrow),
            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
    }

    /// <summary><see cref="RevealState"/> read off the live columns — the SUBSCRIBING read every reader gate binds. While
    /// the title is unresolved it observes the table's <c>Changed</c> signal, because a failure mark (or a seal after
    /// repeated misses) need not move the row's version: the gate must still wake for it.</summary>
    public static LoadState ReaderLoadState(Episode episode)
    {
        if (!episode.IsValid) return LoadState.Pending;
        if (episode.Knows(EpisodeFields.Title)) return RevealState(valid: true, knowsTitle: true, episode.Title.Length > 0, failed: false);
        _ = Entities.Current.Episodes.Changed.Value;
        return RevealState(valid: true, knowsTitle: false, hasTitle: false,
                           Entities.Current.Episodes.IsFailed(episode.Slot, (uint)EpisodeFields.Title));
    }

    /// <summary>THE retry: ask this row's paint groups again, whatever the scope sealed — a terminal transport failure's
    /// mark, or <c>FetchMissPolicy</c>'s seal after the wire omitted the row twice, both go with it (<c>Fetch.Refresh</c>
    /// clears <c>Asked</c> AND the miss count, then plans). One door for the row's Retry, the chip's and the hero's.</summary>
    public static void RetryRow(Episode episode)
    {
        if (episode.IsValid) Entities.Refresh(Entities.Current.Episodes, [episode.Slot], (uint)(EpisodeFields.Row | EpisodeFields.About));
    }

    /// <summary>The reveal GATE every reader surface shares: <paramref name="content"/> once <see cref="ReaderLoadState"/>
    /// says Ready, <paramref name="seed"/> (a derived shimmer of the same shape) while Pending, <paramref name="failed"/>
    /// on Failed — so a surface built over an episode whose identity is not there yet never paints an empty plate, and
    /// one whose ask failed offers a Retry instead of shimmering forever. <paramref name="episode"/> is FIXED (the head's
    /// chips and hero are rebuilt by their owner when the episode's version moves); a bound row uses
    /// <see cref="ReaderRow"/>, which reads its slot's live item.</summary>
    public static Element Reveal(Episode episode, Func<Element> content, Func<Element> seed, Func<Element> failed)
        => new SkelRegionEl(
            Pending: () => ReaderLoadState(episode) == LoadState.Pending,
            Failed: () => ReaderLoadState(episode) == LoadState.Failed,
            Content: content, ShimmerSource: seed, OnFailed: failed,
            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);

    static Element ReaderFailure(Episode episode, bool narrow)
    {
        var item = Fixed(RowItem.Of(episode));
        return RowGrid(Art(in item, narrow ? RowArtNarrow : RowArt),
            new BoxEl { Direction = 1, Grow = 1, Basis = 0, MinWidth = 0, Gap = Spacing.S,
                Children = [new TextEl(Loc.Get(Strings.Podcast.Reader.Unavailable))
                    { Size = 14, LineHeight = 19, Wrap = TextWrap.Wrap, Color = Tok.TextSecondary }] },
            Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => RetryRow(episode)));
    }

    static BoxEl RowGrid(Element art, Element copy, Element actions) => new()
    {
        Direction = 0, Gap = RowGap, AlignItems = FlexAlign.Start, MinWidth = 0,
        Padding = new Edges4(RowPadX + ToneBarWidth + 8f, RowPadY, RowPadX, RowPadY), Children = [art, copy, actions],
    };

    static Element ReaderRowContent(in BoundItemScope<RowItem> item, RowContext ctx, bool narrow, bool seed)
    {
        IReadSignal<RowItem> it = item.Item;
        Func<ColorF> tone = ctx.Tone;
        Prop<ColorF> toneInk = Prop.Of(tone);
        Action<Episode> open = ctx.Open ?? s_openPage;

        var copy = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 3f,
            Opacity = item.Opacity(static r => PlayedOf(r.Episode) ? PlayedInk : 1f),
            Children = [TitleLine(in item, it, tone, narrow, seed), Description(in item, seed), MetaLine(in item, toneInk, seed), Rule(in item, toneInk)],
        };
        var grid = RowGrid(Art(in item, narrow ? RowArtNarrow : RowArt), copy, Cluster(in item, it, ctx, narrow, seed));
        var root = new BoxEl
        {
            ZStack = true, MinWidth = 0f, Corners = CornerRadius4.All(RowRadius),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            OnClick = item.Invoke(r => open(r.Episode)),
            Children =
            [
                // The top hairline, dropped under a month header (the prototype's `.group + .erow`).
                new BoxEl
                {
                    Height = 1f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Fill = Tok.StrokeDividerDefault, Visible = item.Show(static r => (r.Marks & RowMarks.NoRule) == 0),
                },
                // NOW PLAYING: the 3-DIP tone bar on the row's left edge, inset by the row's own padding.
                new BoxEl
                {
                    Width = ToneBarWidth, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(0f, RowPadY, 0f, RowPadY), Corners = CornerRadius4.All(2f), Fill = toneInk,
                    HitTestVisible = false, Visible = Prop.Of(() => IsNowPlaying(it.Value.Episode)),
                },
                grid,
            ],
        };
        if (ctx.Selection is { } selection)
        {
            var scope = item.Row;
            var interact = scope.OnInteraction;
            root = root with
            {
                Role = AutomationRole.Button, Focusable = false, OnClick = null,
                Fill = Prop.Of(() => scope.IsSelected() ? Design.Colors.RowHover : ColorF.Transparent),
                OnFocusChanged = scope.OnFocusChanged,
                OnPointerReleased = args =>
                {
                    if (selection.Selecting.Peek() || (args.Mods & (KeyModifiers.Ctrl | KeyModifiers.Shift)) != 0)
                        interact(ItemContainerTrigger.Tap, SelectorVisualsBound.MultiSelectMods(selection.Selecting.Peek(), args.Mods));
                    else open(it.Peek().Episode);
                },
                OnKeyDown = args =>
                {
                    if (args.KeyCode == Keys.Enter)
                    {
                        if (selection.Selecting.Peek()) interact(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(true, args.Mods));
                        else open(it.Peek().Episode);
                        args.Handled = true;
                    }
                    else if (args.KeyCode == Keys.Space && !args.IsRepeat)
                    { selection.Selecting.Value = true; interact(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(true, args.Mods)); args.Handled = true; }
                    else if (args.KeyCode == Keys.Escape) { selection.Clear(); args.Handled = true; }
                },
                Children = [new BoxEl { Direction = 0, MinWidth = 0, Children =
                [SelectorVisualsBound.BoundCheckLane(() => selection.Selecting.Value, scope.IsSelected, interact, 4f),
                 new BoxEl { ZStack = true, Grow = 1, Basis = 0, MinWidth = 0, Children = root.Children }] }],
            };
        }
        if (ctx.Overlay is { } overlay && ctx.Menu is { } menu)
            root = ContextMenu.Attach(root, overlay, () => menu(it.Peek().Episode), s_menuOptions);
        return root;
    }

    static Element Numeral(in BoundItemScope<RowItem> item) => new BoxEl
    {
        Width = NumeralWidth, Height = RowArt, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
        Opacity = item.Opacity(static r => PlayedOf(r.Episode) ? PlayedInk : 1f),
        Children =
        [
            new TextEl(item.Text(static r => NumeralOf(r)))
            {
                FontFamily = DisplayFace, Size = 30f, LineHeight = RowArt, Weight = 300, CharSpacing = -30f,
                Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    static Element Art(in BoundItemScope<RowItem> item, float edge) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(ArtRadius), ClipToBounds = true,
        Opacity = item.Opacity(static r => PlayedOf(r.Episode) ? PlayedInk : 1f),
        Children =
        [
            new ImageEl
            {
                Source = item.Image(static r => ArtOf(r.Episode)), Width = edge, Height = edge, Fit = ImageFit.Cover,
                DecodePx = edge, Corners = CornerRadius4.All(ArtRadius),
                Placeholder = item.Color(static r => Design.PlaceholderFor(ArtOf(r.Episode))),
            },
        ],
    };

    /// <summary>The equalizer (now playing) · the title (tone while playing) · the badge chips, on one centred line.</summary>
    static Element TitleLine(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, Func<ColorF> tone, bool narrow, bool seed = false) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Children =
        [
            seed ? new BoxEl() : item.ShowWhen(static r => IsNowPlaying(r.Episode), () => Controls.Equalizer(Playback.IsPlaying, tone, EqualizerHeight)),
            new TextEl(seed ? SeedTitle : item.Text(static r => ShownTitle(r.Episode)))
            {
                Size = 14f, LineHeight = 19f, Weight = 600, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f, Grow = 1f, Basis = 0f, Shrink = 1f,
                Color = Prop.Of(() => IsNowPlaying(it.Value.Episode) ? tone() : Tok.TextPrimary),
            },
        ],
    };

    static Element Description(in BoundItemScope<RowItem> item, bool seed = false) => new TextEl(seed ? SeedDescription : item.Text(static r => DescriptionOf(r.Episode)))
    {
        Size = 12.5f, LineHeight = 18f, Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap,
        Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Visible = seed ? true : item.Show(static r => HasDescription(r.Episode)),
    };

    /// <summary>NEW · "23 min left" (in progress) · date · length (not in progress) · ✓ played · the transcript mark.</summary>
    static Element MetaLine(in BoundItemScope<RowItem> item, Prop<ColorF> toneInk, bool seed = false) => new BoxEl
    {
        Direction = 0, Wrap = true, Gap = 10f, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = new Edges4(0f, 2f, 0f, 0f),
        Children =
        [
            MetaText(item.Text(static r => NumeralOf(r) is { Length: > 0 } n ? "#" + n : ""))
                with { Visible = item.Show(static r => NumeralOf(r).Length > 0) },
            Controls.Chip(Loc.Get(Strings.Podcast.Badge.Bonus), tone: true)
                with { Visible = item.Show(static r => KindOf(r.Episode) == EpisodeKind.Bonus) },
            Controls.Chip(Loc.Get(Strings.Podcast.Badge.Trailer), tone: true)
                with { Visible = item.Show(static r => KindOf(r.Episode) == EpisodeKind.Trailer) },
            Controls.Chip(ExplicitMark)
                with { Visible = item.Show(static r => (FlagsOf(r.Episode) & EpisodeFlags.Explicit) != 0) },
            Controls.Chip(Loc.Get(Strings.Podcast.Badge.Video), Icons.Movie)
                with { Visible = item.Show(static r => (FlagsOf(r.Episode) & EpisodeFlags.Video) != 0) },
            Controls.Chip(Loc.Get(Strings.Podcast.Badge.Subscribers), LockGlyph)
                with { Visible = item.Show(static r => (FlagsOf(r.Episode) & EpisodeFlags.Paywalled) != 0) },
            new TextEl(Loc.Get(Strings.Podcast.New))
            {
                Size = 10.5f, LineHeight = 16f, Weight = 700, CharSpacing = 60f, Color = toneInk, MaxLines = 1,
                Visible = item.Show(static r => IsNew(in r)),
            },
            new TextEl(item.Text(static r => LeftOf(r.Episode)))
            {
                Size = 12f, LineHeight = 16f, Weight = 600, Color = toneInk, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Visible = item.Show(static r => InProgressOf(r.Episode)),
            },
            MetaText(seed ? s_dates.Get(SeedDateKey, s_dateFormat) : item.Text(static r => DateKeyOf(r.Episode), s_dates, s_dateFormat))
                with { Visible = seed ? true : item.Show(static r => DateKeys.IsDayKey(DateKeyOf(r.Episode))) },
            MetaText(seed ? DurationLabel(SeedDurationMs) : item.Text(static r => DurationLabelOf(r.Episode))) with { Visible = seed ? true : item.Show(static r => !InProgressOf(r.Episode) && r.Episode.IsValid && r.Episode.Knows(EpisodeFields.Duration) && r.Episode.DurationMs > 0) },
            new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Visible = item.Show(static r => PlayedOf(r.Episode)),
                Children = [Icon(Icons.Check, 12f) with { Color = toneInk }, MetaText(Loc.Get(Strings.Podcast.Played))],
            },
            Icon(Icons.Document, 12f) with
            {
                Color = Tok.TextTertiary, Visible = item.Show(static r => (FlagsOf(r.Episode) & EpisodeFlags.HasTranscript) != 0),
            },
        ],
    };

    /// <summary>The 3-DIP tone rule (≤ 340) under an in-progress row's meta; the fill is a compositor scale from the left.</summary>
    static Element Rule(in BoundItemScope<RowItem> item, Prop<ColorF> toneInk) => new BoxEl
    {
        Direction = 0, Height = RuleHeight, MaxWidth = RuleMaxWidth, Corners = CornerRadius4.All(RuleCorner),
        Fill = Tok.FillSubtleTertiary, ClipToBounds = true, Shrink = 0f, Margin = new Edges4(0f, 4f, 0f, 0f),
        Visible = item.Show(static r => InProgressOf(r.Episode)),
        Children =
        [
            new BoxEl
            {
                Grow = 1f, Fill = toneInk, TransformOriginX = 0f,
                Transform = item.Value(static r => Affine2D.Scale(MathF.Max(0.001f, PlaybackProgressOf(r.Episode).Pct), 1f)),
            },
        ],
    };

    /// <summary>Queue · mark · ♥ (Your Episodes) · ⋯ (the row's menu) + the disc, fading in
    /// with the row's hover; narrow: the disc alone, always visible.</summary>
    static Element Cluster(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, RowContext ctx, bool narrow, bool seed = false)
    {
        var focused = new Signal<bool>(false);
        Action<bool> focus = value => focused.Value = value;
        Element disc = Disc(in item, it, ctx, focus, seed);
        return new BoxEl
        {
            Direction = (byte)(narrow ? 1 : 0), Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children =
            [
                // ⋯ raises the row's own context request — the ONE menu the row attached above.
                seed ? ActionCell(Icon(Icons.More, 16f, Tok.TextSecondary)) : ToolTip.Wrap(ActionCell(Icon(Icons.More, 16f, Tok.TextSecondary)) with { ClickRequestsContext = true, OnFocusChanged = focus },
                             Loc.Get(Strings.Common.More)),
                disc,
            ],
        };
    }

    static BoxEl ActionCell(Element glyph) => new()
    {
        Width = ActionBox, Height = ActionBox, Shrink = 0f, Corners = CornerRadius4.All(5f),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true,
        Children = [glyph],
    };

    /// <summary>A 30-DIP action; a null <paramref name="onClick"/> draws it disabled with its name (plan D-10).</summary>
    static Element ActionButton(Element glyph, string name, Action? onClick, Action<bool>? focus = null)
    {
        BoxEl box = ActionCell(glyph) with { OnClick = onClick, OnFocusChanged = focus };
        if (onClick is null) box = box with { IsEnabled = false, Focusable = false, Cursor = null };
        return Controls.Named(box, name);
    }

    /// <summary>Mark played ⇄ unplayed — an absolute-state pair; the glyph inks in the tone and the tooltip names the
    /// verb the click will run.</summary>
    static Element MarkButton(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, Func<ColorF> tone, Action<bool>? focus = null)
    {
        BoxEl box = ActionCell(Icon(Icons.Check, 16f) with
        {
            Color = Prop.Of(() => PlayedOf(it.Value.Episode) ? tone() : Tok.TextSecondary),
        }) with
        {
            OnClick = item.Invoke(static r => Entities.MarkEpisode(r.Episode, !PlayedOf(r.Episode))),
            OnFocusChanged = focus,
        };
        return ToolTip.Wrap(box, Prop.Of<string?>(() => Loc.Get(PlayedOf(it.Value.Episode)
            ? Strings.Podcast.Menu.MarkUnplayed : Strings.Podcast.Menu.MarkPlayed)));
    }

    /// <summary>The 36 tone disc: play, or pause on the playing row (<see cref="Invoke"/> through the host's verb).</summary>
    static Element Disc(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, RowContext ctx, Action<bool>? focus = null, bool seed = false)
    {
        Func<ColorF> tone = ctx.Tone;
        Action<Episode> play = ctx.Play;
        var disc = new BoxEl
        {
            OnFocusChanged = focus,
            Width = RowDisc, Height = RowDisc, Shrink = 0f, Corners = Radii.Circle(RowDisc), Fill = Prop.Of(tone),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true,
            OnClick = item.Invoke(r => play(r.Episode)),
            Children =
            [
                Icon(Icons.Play, RowDiscGlyph) with
                {
                    Text = Prop.Of(() => IsNowPlaying(it.Value.Episode) && Playback.IsPlaying.Value ? Icons.Pause : Icons.Play),
                    Color = Prop.Of(() => ColorContrast.PickContrast(tone())),
                },
            ],
        };
        return seed ? disc : Controls.Named(disc, Loc.Get(Strings.Detail.Play));
    }

    static TextEl MetaText(Prop<string> text) => new(text)
    {
        Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
    };

    // ══ 5. THE EAGER SCOPE, THE SEED, THE TRACK ══════════════════════════════════════════════════════════════════════

    /// <summary>A FIXED scope over one row — the visit head's few eager rows reuse the bound template (so they are the
    /// same pixels); their owner re-renders them when the row's version or marks move.</summary>
    public static BoundItemScope<RowItem> Fixed(RowItem row) => new(s_fixedRow, new FixedSignal<RowItem>(row));

    static readonly RowScope s_fixedRow = new(new FixedSignal<int>(0), static () => false, static () => false,
                                              static () => true, static (_, _) => { }, static _ => { });

    sealed class FixedSignal<T>(T value) : IReadSignal<T>
    {
        public T Value => value;
        public T Peek() => value;
    }

    /// <summary>The cold skeleton's row: figure-space title and blurb bars, "Jan 1 · 3 min", the art tile — the real
    /// row's geometry, no cluster (hidden at rest), no rule (the seed invents no progress).</summary>
    static readonly RowContext s_seedContext = new() { Tone = static () => Tok.AccentDefault, Play = static _ => { } };
    internal static Element SeedReaderRow(bool narrow)
    {
        var item = Fixed(default);
        return ReaderRowContent(in item, s_seedContext, narrow, seed: true);
    }

    /// <summary>A progress track in the tone (the hero's 4 DIP): two flex halves, so the fill is exact at any width.</summary>
    public static Element ProgressRule(float pct, Func<ColorF> tone, float height = RuleHeight) => new BoxEl
    {
        Direction = 0, Height = height, Corners = CornerRadius4.All(height / 2f), Fill = Tok.FillSubtleTertiary,
        ClipToBounds = true, Shrink = 0f,
        Children =
        [
            new BoxEl { Grow = MathF.Max(0.001f, pct), Fill = Prop.Of(tone) },
            new BoxEl { Grow = MathF.Max(0.001f, 1f - pct) },
        ],
    };
}
