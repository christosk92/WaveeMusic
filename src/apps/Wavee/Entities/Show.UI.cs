// ── Entities/Show.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the show READER's core hosts (W1-W3): the sticky word rail's body, the visit head, the "episodes N" header and the foot —
// each a small component that owns its stamp memo and its hooks and hands the decided values to the structure's
// `ShowReaderStyle` (`Theming\Contracts\ShowReaderStyle.cs`, Classic's `Themes\Classic\Structure\Views\Show\`), which draws them.
// What stayed here: the tone, the month word's culture text, the meta line's words and the plumbing from the snapshot to the
// style's args. The library pane's show header IS the album pane's (`ClassicLibraryPaneKit.PaneHeader`: one geometry for both kinds, library rework §5.4) — the
// show pane calls it directly
//
// Role: UI (the hosts are core; the picture is the structure's)
// Owner: P
// Wave: P3 (the podcast rework)
// Spec: podcast-show-rework-implementation.md §2 W1/W2 (the head's three first screens), §3.1, §4, §6.1 (the head waits
//       for the settled progress), §7 (doors/hero lift 150 ms, the head swap FadeOnly) · the prototype
//       podcast-show-episode-mica.html (.wordrail, .sechead h3, .doors/.door, .resume, .upnext/.mini, .group, .caught,
//       .about/.facts, .empty)
//
// ── HOW THE PIECES RE-RENDER ─────────────────────────────────────────────────────────────────────────────────────────
//
// The components here are slots of the reader's bound list, and each re-renders on a small STAMP memo of its own over
// the page's one snapshot (never on every snapshot): the head on (visit, width arm, a fold of every episode it shows,
// the tone), the header on (view, total, filtered, empty), the foot on (paging), the rail body on its width arm only —
// every word, count and the find box inside it are binds over the page's own Signal instances. Builders read the
// snapshot with Peek, so a render reads the state its stamp was computed from.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary>The reader's bottom reserve: the last row clears the player dock by 24 (ch 09 item 11).</summary>
    public static float BottomReserve => R.Space.DockReserve + Spacing.XXL;

    // ── the reader's rhythm: the gutters, the sticky plane (also the list's shared top clip, `ScrollOptions.ItemClipTopInset`, a
    // MOUNT-TIME value: the toolbar is ONE row at every rung of `ShowToolbarLayout`'s collapse ladder, so these never move) and the seed ──
    const float ReaderPad = ShowReaderRules.Pad, ReaderPadNarrow = ShowReaderRules.PadNarrow;
    const float RailHeight = ShowReaderRules.RailExtent, ItemEstimate = ShowReaderRules.RowSeed;

    /// <summary>ONE cache and ONE thunk for every month word the reader prints: the group header and the sticky month
    /// say the same thing about the same <see cref="DateKeys"/> month key, so they share the string.
    /// <para>The month name is the CURRENT culture's own text, as written ("August 2026"): its case is the style's voice, so a structure
    /// cases it at draw (Zune lowers it through its case roles, Classic draws it as written). A key that is not a month key — the
    /// undated run's -1, a recycled slot's default, a key from anywhere else — reads "", because
    /// <see cref="DateKeys.MonthLabel"/> is TOTAL: this thunk runs on the render path and may not throw.</para></summary>
    static readonly FormatCache<int> s_monthWords = new();
    static readonly Func<int, string> s_monthWord = static key => DateKeys.MonthLabel(key, CultureInfo.CurrentCulture);

    /// <summary>The month word of a <see cref="DateKeys"/> month key (the sticky month's text; "" for a key that is not one).</summary>
    internal static string MonthWordOf(int monthKey) => s_monthWords.Get(monthKey, s_monthWord);

    /// <summary>The structure's show style (<c>StyleKeys.ShowReader</c>), read from the ambient registry; null (and one log line) without one.</summary>
    static ShowReaderStyle? StyleOf(global::FluentGpu.Theming.ViewRegistry? registry)
        => ListStyleLookup.Of(registry, StyleKeys.ShowReader);

    // ══ 1. THE TONE ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The show's own tone when the provider stated one (<c>ShowFields.Rating</c>'s <c>Tone</c>, D-7), else
    /// the frame's cover-derived accent. Reads <paramref name="fallback"/> FIRST either way, so a consumer memo stays
    /// subscribed to the frame's accent (a theme flip re-derives it).</summary>
    static ColorF ToneOf(uint argb, ColorF fallback) => argb != 0 ? Design.Palette.ChromeFromPayload(argb) : fallback;

    static string Joined(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;

    // ══ 2. THE RAIL BODY (item 0's content — the sticky root itself is a RAW element, Show.Page.cs) ═════════════════

    /// <summary>The reader's toolbar host: re-renders only when the STAGE moves (<see cref="ShowToolbarLayout.For"/> over
    /// the toolbar's own measured width and what its rails measured, computed by the reader host's memo), the collapsed
    /// find toggles, or the width arm flips. Every word, count and the find box itself bind the page's own signals; the
    /// structure draws them (<see cref="ShowReaderStyle.Toolbar"/>).</summary>
    sealed class RailBody(ReaderHost host) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var stage = host.Toolbar?.Value ?? ToolbarStage.Full;
            bool finding = host.FindOpen.Value;
            bool compact = host.Narrow?.Value ?? false;
            if (host.Style is not { } style) return new BoxEl();
            var m = host.Model;
            return style.Toolbar(new ShowToolbarArgs
            {
                Stage = stage, Finding = finding, Compact = compact,
                FilterWords = host.FilterWords!, SortWords = host.SortWords!,
                Status = m.Status, Order = m.Order, Find = m.Find, StatusCount = host.StatusCountOf,
                Tone = host.Tone, IsSelecting = host.IsSelecting,
                OpenFind = host.OpenFind, CloseFind = host.CloseFind, ToggleSelecting = host.ToggleSelecting,
                OnFiltersMeasured = host.OnFiltersMeasured, OnSortMeasured = host.OnSortMeasured, OnToolbarBounds = host.OnToolbarBounds,
                Overlay = overlay,
            });
        }
    }

    // ══ 3. THE VISIT HEAD (item 1) ═══════════════════════════════════════════════════════════════════════════════════

    /// <param name="Shown">Is the head painted (false in results mode — the box is then empty, mounted at the same list
    /// position, and its height corrects to 0)?</param>
    /// <param name="Doors">The door and up-next column counts (<see cref="ShowReaderRules.HeadColumns"/>): the head
    /// re-renders when they change, never per resize frame.</param>
    readonly record struct HeadStamp(ShowReaderRules.Head Kind, bool Narrow, ulong Fold, ColorF Tone, bool Shown, int Doors, int Minis);

    /// <summary>The first screen the visitor needs (W1/W2): New → start-here doors + the full about; Returning → the
    /// continue hero, up next, new since you were here; CaughtUp → one line with the usual release day; Unavailable →
    /// the hydrate failed and nothing local says otherwise. Until the membership answered AND progress settled it is a
    /// hero-height shimmer (§6.1: never flash New at a Returning listener), revealed with a plain fade (§7). The head's
    /// shell and its three arms are the structure's (<see cref="ShowReaderStyle.HeadShell"/>, <see cref="ShowReaderStyle.Head"/>).</summary>
    sealed class VisitHead : Component
    {
        readonly ReaderHost _host;
        readonly Func<HeadStamp> _stamp;
        readonly Func<bool> _pending;
        readonly Func<Element> _content;

        public VisitHead(ReaderHost host)
        {
            _host = host;
            _stamp = () =>
            {
                var s = host.Model.Read();
                var (doors, minis) = ShowReaderRules.HeadColumns(host.MeasuredWidth(subscribe: true));
                return new HeadStamp(s.Head, host.Narrow?.Value ?? false, s.HeadFold, host.Tone(), s.HeadShown, doors, minis);
            };
            _pending = () => host.Model.Read().Head == ShowReaderRules.Head.Pending;
            _content = HeadBody;
        }

        public override Element Render()
        {
            var stamp = UseComputed(_stamp).Value;
            // Results mode: nothing painted, the slot stays mounted (and keeps its place in the list).
            if (!stamp.Shown) return new BoxEl();
            return _host.Style is { } style ? style.HeadShell(_pending, _content) : new BoxEl();
        }

        /// <summary>The head's content, built when the reveal region asks: the snapshot's decided visit and the facts the arms print.</summary>
        Element HeadBody()
        {
            var h = _host;
            var s = h.Model.Peek();
            var (doors, minis) = ShowReaderRules.HeadColumns(h.MeasuredWidth(subscribe: false));
            var show = s.Show;
            // The full about is the NEW head's (W2): a returning listener's rail already carries it.
            string? about = s.Head == ShowReaderRules.Head.New && show.IsValid && show.Knows(ShowFields.About)
                            && (!show.DescriptionId.IsEmpty || !show.HtmlDescriptionId.IsEmpty)
                ? Entities.Strings.Resolve(!show.HtmlDescriptionId.IsEmpty ? show.HtmlDescriptionId : show.DescriptionId) : null;
            return h.Style!.Head(new ShowHeadArgs
            {
                Kind = s.Head, Narrow = h.Narrow?.Peek() ?? false, Tone = h.Tone, Doors = doors, Minis = minis,
                Serial = s.Order == ConsumptionOrder.Sequential,
                FirstSlot = s.FirstSlot, LatestSlot = s.LatestSlot, TrailerSlot = s.TrailerSlot, ResumeSlot = s.ResumeSlot,
                UpNext = s.UpNext, Fresh = s.Fresh, About = about, Meta = MetaOf(s.Total, s.Cadence, s.OldestYear),
                Played = s.Ledger.Played, InProgress = s.Ledger.InProgress, ToGo = s.Ledger.ToGo, Day = s.Day,
                RowCtx = h.HeadRowCtx!, EpisodeStyle = h.EpisodeStyle,
                Play = h.PlayEpisode, PlayAlone = h.PlayAlone, MarkFreshPlayed = h.Model.MarkFreshPlayed,
            });
        }
    }

    // ══ 4. THE HEADER (item 2), THE MONTHS, THE FOOT ═════════════════════════════════════════════════════════════════

    readonly record struct HeaderStamp(int View, int Total, bool Filtered, bool IsEmpty, bool EmptyShow);

    /// <summary>"episodes 128" (or "episodes 3 of 128" under a filter or a find) and the empty arm under it, drawn by the
    /// structure (<see cref="ShowReaderStyle.EpisodesHeader"/>); this host owns the stamp.</summary>
    sealed class EpisodesHeader : Component
    {
        readonly ReaderHost _host;
        readonly Func<HeaderStamp> _stamp;

        public EpisodesHeader(ReaderHost host)
        {
            _host = host;
            _stamp = () =>
            {
                var s = host.Model.Read();
                return new HeaderStamp(s.ViewCount, s.Total, s.Filtered, s.IsEmpty, s.EmptyShow);
            };
        }

        public override Element Render()
        {
            var st = UseComputed(_stamp).Value;
            return _host.Style is { } style
                ? style.EpisodesHeader(new ShowEpisodesHeaderArgs
                {
                    View = st.View, Total = st.Total, Filtered = st.Filtered, IsEmpty = st.IsEmpty, EmptyShow = st.EmptyShow, Reset = _host.ResetView,
                })
                : new BoxEl();
        }
    }

    /// <summary>A month header (the prototype's <c>.group</c>), bound to its slot's item: the air above it (none right under the "episodes"
    /// head) and the culture's month word; the structure draws it (<see cref="ShowReaderStyle.Group"/>).</summary>
    static Element GroupLabel(ShowReaderStyle style, BoundItemScope<ReaderItem> item) => style.Group(new ShowGroupArgs
    {
        Air = item.Value(static it => (it.Row.Marks & Episode.RowMarks.NoRule) != 0 ? 0f : ShowReaderRules.GroupAir),
        Month = item.Text(static it => it.GroupKey, s_monthWords, s_monthWord),
    });

    readonly record struct FootStamp(bool CanLoadMore, bool Paging);

    /// <summary>The list's end: the load-more pill above the dock reserve while the membership pages (a pill, never an
    /// infinite-scroll sentinel), else the reserve alone (<see cref="ShowReaderStyle.Foot"/>).</summary>
    sealed class ReaderFoot : Component
    {
        readonly ReaderHost _host;
        readonly Func<FootStamp> _stamp;

        public ReaderFoot(ReaderHost host)
        {
            _host = host;
            _stamp = () =>
            {
                var s = host.Model.Read();
                return new FootStamp(s.CanLoadMore, s.Paging);
            };
        }

        public override Element Render()
        {
            var st = UseComputed(_stamp).Value;
            // The request is the READER's (ReaderHost.Similar): this foot is remounted whenever the list's items move, and
            // a request it owned went out again each time.
            return _host.Style is { } style
                ? style.Foot(new ShowFootArgs
                {
                    CanLoadMore = st.CanLoadMore, Paging = st.Paging, LoadMore = _host.Model.LoadMore,
                    Similar = Embed.Comp(new PodcastReaderUI.SimilarProps(_host.Similar), static () => new PodcastReaderUI.SimilarShelf()),
                })
                : new BoxEl();
        }
    }

    // ══ 5. THE META LINE ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The meta line: "128 episodes · weekly · since 2023" — the cadence only when the rule names one, the
    /// year only when the membership is complete (the oldest resident is then the first).</summary>
    internal static string MetaOf(int total, ShowCadence.Kind cadence, int sinceYear)
    {
        string meta = Strings.Podcast.EpisodeCount(total);
        string word = cadence switch
        {
            ShowCadence.Kind.Daily => Loc.Get(Strings.Podcast.Cadence.Daily),
            ShowCadence.Kind.Weekly => Loc.Get(Strings.Podcast.Cadence.Weekly),
            ShowCadence.Kind.Fortnightly => Loc.Get(Strings.Podcast.Cadence.Fortnightly),
            ShowCadence.Kind.Monthly => Loc.Get(Strings.Podcast.Cadence.Monthly),
            _ => "",
        };
        meta = Joined(meta, word);
        return sinceYear > 0 ? Joined(meta, Strings.Podcast.Since(sinceYear.ToString(CultureInfo.InvariantCulture))) : meta;
    }
}
