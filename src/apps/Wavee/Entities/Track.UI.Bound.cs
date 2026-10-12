// ── Entities/Track.UI.Bound.cs ─────────────────────────────────────────────────────────────────────────────────────
// the table's BOUND row: the pure decisions its cells make (`RowCellRules`), the slot's inputs (`BoundRow`, `RowPresentation`)
// and the data helpers a row template reads. The row's PICTURE moved to the structure (T8a): the bound grid of lanes, built
// ONCE per slot with every per-item value a bound Prop over the slot's presentation memo (fluentgpu rule 13), frequent flips
// as `Visible` binds and the rare, heavy branches (spinner, equalizer, marquee, the withheld # cell) as `Flow.Show`, is
// `Themes\Classic\Structure\Lists\ClassicTrackTable.cs` (Modern and Classic templates, registered under
// `StyleKeys.TrackTable`). A recycle rebinds; it never rebuilds an element. `Grid` stays the one-shot row for the eager rows and
// the drawers (T8c).
//
// Role: UI
// Owner: M
// Wave: 4.5
// Budget: 700 lines
// Spec: ch 01 §9

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Track
{
    // ══ 1. THE PURE DECISIONS (Wavee.Tests/TrackRowCellRulesTests.cs) ═══════════════════════════════════════════════

    /// <summary>What the bound cells decide per item, engine-free: the # cell's rest state and its hover reveal, the
    /// chart mark, the duration lane's arm, the metadata line's presence and the span-index → link target maps.</summary>
    public static class RowCellRules
    {
        public enum Rest : byte { Empty, Spinner, Volume, Equalizer, Star, Number }
        public enum ChartMark : byte { None, Up, Down, New }
        public enum DurationKind : byte { Clock, Unavailable, ReleaseDate, Dash }

        /// <summary>A span click that resolves to the container (album / show) link.</summary>
        public const int Container = -1;
        /// <summary>A span click that resolves to nothing (a separator, a plain-text name, a lane with no album).</summary>
        public const int None = -2;

        /// <summary>The # cell at rest (ch 01 §0.2): buffering ring first; Classic states now-playing with the Volume mark
        /// and nothing else; Modern shows the equalizer, the album's top-track star, else the number.</summary>
        public static Rest RestOf(in RowState st, bool classic)
        {
            if (st.IsBuffering) return Rest.Spinner;
            if (classic) return st.IsNow ? Rest.Volume : Rest.Empty;
            if (st.IsNow) return Rest.Equalizer;
            if (st.IsTop) return Rest.Star;
            return Rest.Number;
        }

        /// <summary>Does row hover reveal the transport? Never on a withheld row (not out / ruled unavailable, ch 01 W6),
        /// except that a buffering ring still shows.</summary>
        public static bool Reveal(in RowState st, bool withheld) => st.IsBuffering || !withheld;

        /// <summary>The chart byte as <c>Spotify.Decode.ChartStatus</c> writes it: 2 Up · 3 Down · 4 New; Equal and Unknown
        /// draw nothing (ch 01 W5).</summary>
        public static ChartMark ChartMarkOf(byte status) => status switch
        {
            2 => ChartMark.Up,
            3 => ChartMark.Down,
            4 => ChartMark.New,
            _ => ChartMark.None,
        };

        /// <summary>The duration lane's arm. Unplayable is tested FIRST: with no release instant the not-yet-out predicate
        /// also holds, and it would print a dash where the verdict has a word.</summary>
        public static DurationKind DurationKindOf(bool unplayable, bool notYetOut, int availableAt, long nowUnixSeconds)
            => unplayable ? DurationKind.Unavailable
                : notYetOut ? (availableAt > nowUnixSeconds ? DurationKind.ReleaseDate : DurationKind.Dash)
                : DurationKind.Clock;

        /// <summary>The Modern metadata subline is present when it has anything to say.</summary>
        public static bool ShowMeta(bool classic, bool artistInTitle, bool showAlbumInMeta, bool badge)
            => !classic && (artistInTitle || showAlbumInMeta || badge);

        /// <summary>Span count of a link run: [artist0 ", " artist1 …] [" · " container].</summary>
        public static int LinkSpanCount(int artistCount, bool container)
        {
            int artistSpans = artistCount == 0 ? 0 : artistCount * 2 - 1;
            return artistSpans + (container ? (artistSpans > 0 ? 2 : 1) : 0);
        }

        /// <summary>A click on span <paramref name="spanIndex"/> of a link run: artist i sits at span 2i; the container is
        /// the LAST span (only when <paramref name="album"/>). Separators resolve to <see cref="None"/>.</summary>
        public static int LinkTarget(int spanIndex, int artistCount, bool album)
        {
            int artistSpans = artistCount == 0 ? 0 : artistCount * 2 - 1;
            if (spanIndex < artistSpans) return (spanIndex & 1) == 0 ? spanIndex / 2 : None;
            if (!album) return None;
            // FillLinkSpans: [artists…] then " · " (only when artists precede) then the container.
            int containerIndex = artistCount == 0 ? 0 : artistSpans + 1;
            return spanIndex == containerIndex ? Container : None;
        }

        /// <summary>A click on a Classic folded title run — title 600 · [" " film] · "  ·  " · linked artists: the artist
        /// index, or <see cref="None"/> for the head, the glyph and every separator.</summary>
        public static int ClassicArtistOf(int spanIndex, bool showVideo, int artistCount)
        {
            int first = 1 + (showVideo ? 2 : 0) + 1;
            int rel = spanIndex - first;
            return artistCount > 0 && rel >= 0 && (rel & 1) == 0 ? rel / 2 : None;
        }
    }

    // ══ 2. THE SLOT'S INPUTS ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What one row paints, as one equality-gated value: a recycle recomputes it once and re-renders the row
    /// only when its visual state really changed (a title landing is the row's Version; a like, the Liked edge). Built by
    /// <c>TableHost.Present</c>; read by the structure's row template through <see cref="BoundRow"/>.</summary>
    internal readonly record struct RowPresentation(
        Track Track, int Display, RowState State, uint TrackVersion, uint AlbumVersion, ulong ArtistStamp,
        int AddedAt, User AddedBy, uint UserVersion, byte Chart, bool Marquee, bool ShowTrackArtist,
        bool ShowAlbumColumn, bool Open);

    /// <summary>One slot's bound inputs, created once at mount: the presentation memo every cell reads, the like-edge
    /// memo, the row hover signal, the playback flag the equalizer animates on, the page accent and the three handlers (each
    /// resolves the current item itself). The structure's row template binds its cells to these and never builds one.</summary>
    internal sealed class BoundRow(IReadSignal<RowPresentation> presentation, IReadSignal<bool> likePop,
                                   IReadSignal<bool> hovered, IReadSignal<bool> playing, Func<ColorF> accent,
                                   Action play, Action like, Action toggleExpand)
    {
        public readonly IReadSignal<RowPresentation> Presentation = presentation;
        public readonly IReadSignal<bool> LikePop = likePop;
        public readonly IReadSignal<bool> Hovered = hovered;
        /// <summary>Is the deck playing (the equalizer's pulse)? The core's signal, so a template never names `Playback`.</summary>
        public readonly IReadSignal<bool> Playing = playing;
        public readonly Func<ColorF> Accent = accent;
        public readonly Action Play = play, Like = like, ToggleExpand = toggleExpand;

        /// <summary>The current presentation — a subscribing read, for bind thunks only.</summary>
        public RowPresentation P => Presentation.Value;
    }

    internal static long NowUnix => Store.ToUnix(Entities.Now);

    /// <summary>The ONE not-yet-out / unavailable predicate: the dim, the play gate and the duration lane never disagree.</summary>
    internal static bool Withheld(in RowPresentation p, long now) => p.Track.NotYetOut(now) || p.Track.Unplayable();

    internal static bool ArtistInTitle(in RowPresentation p, bool setArtist) => p.ShowTrackArtist && !setArtist;

    internal static bool ShowAlbumInMeta(in RowPresentation p, bool classic, bool setAlbum)
        => !classic && p.ShowAlbumColumn && !setAlbum;

    internal static bool Badge(in RowPresentation p) => p.ShowAlbumColumn && p.Track.ForDisplay.IsExplicit;

    // The Tempo figure is the one row format `Track.Format` did not already cache. (The Added-by id cache went with
    // the id fallback itself, below.)
    internal static readonly FormatCache<ushort> s_tempoLabels = FormatCache.Create<ushort>();
    internal static readonly Func<ushort, string> s_tempoFormat = Format.TempoLabel;

    /// <summary>The Added-by name when identity is known, and "" until it is - never the raw membership id, which
    /// reads as a real name when it is only a guess. The twin of `Track.UI.AddedByCell` and of the deleted
    /// `Playlist.NameOf` fallback.</summary>
    internal static string AddedByLabel(User by)
    {
        if (by.Slot <= 0) return "";
        return by.Knows(UserFields.Identity) ? Entities.Strings.Resolve(by.NameId) : "";
    }
}
