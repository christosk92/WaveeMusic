// ── Home/Items.Rules.cs — pure rules extracted out of the Items.UI.cs templates ─────────────────────────────────────
//
// Role: CORE
// Owner: Wave 2, agent A (Items.UI.cs)
// Spec: docs/plans/wavee/home-redesign-remediation.md §3.3
//
// Engine-free, zero-alloc, no signals/tables/I-O — the one rule Items.UI.cs's templates needed pulled out from under
// a render function so it is unit-testable without a source-text test (CLAUDE.md "No source-text tests": a test
// never reads/greps production source; it exercises a pure class instead — the `DaypartRules`/`EpisodeCaption`
// pattern this file follows). The seventh pass adds the daylist card's rules (`DaylistForm`, `DaypartTimeline`,
// `DaylistCountdownLine`) beside its `DaylistArt`.

namespace Wavee.HomeUi;

/// <summary>The wide-tile shelf card's tertiary meta line (Sixth pass, `Zones.UI.cs`'s WideTiles/VideoTiles cell
/// builder): "Spotify · 200 songs" when both an owner and a track count are known, "200 songs" alone when only the
/// count is, "Spotify" alone when only the owner is, and <c>null</c> when neither is — a bare <see cref="CardData.Meta"/>
/// line, so a caller with nothing to say draws no line at all rather than an empty one.</summary>
public static class CardMeta
{
    public static string? Of(string? owner, int trackCount)
    {
        bool hasOwner = owner is { Length: > 0 };
        bool hasCount = trackCount > 0;
        if (hasOwner && hasCount) return $"{owner} · {Strings.Detail.SongCount(trackCount)}";
        if (hasCount) return Strings.Detail.SongCount(trackCount);
        if (hasOwner) return owner;
        return null;
    }
}

/// <summary>The daylist card's art source (`Daylist.UI.cs`): the daylist's header image, else its square cover, else
/// null (no art layer and no veil: the plain card). The art cover-fills the WHOLE card, so its SHAPE is the card's,
/// never the picture's — no aspect or width cap rides along.</summary>
public static class DaylistArt
{
    public static string? Of(string? headerImage, string? image)
        => headerImage is { Length: > 0 } ? headerImage : image is { Length: > 0 } ? image : null;
}

/// <summary>The daylist card's form (`Daylist.UI.cs`, hero fade variant A): the header art fills the whole card under
/// the artist hero's horizontal veil, and the copy column (eyebrow · title · tags · meta · actions · clock) sits on the
/// veil's opaque left side. <c>inner</c> is the card's content width — the card minus the copy's side insets
/// (<c>Daylist.CopyPadding</c>) — and <see cref="CopyWidth"/> is the copy's text width at it: half of it, held between
/// <see cref="TextMin"/> and <see cref="CopyMax"/>, and all of it once it is narrower than <see cref="SplitMin"/>.</summary>
public static class DaylistForm
{
    /// <summary>The copy's floor: the 40/52 title's line plus the ONE action row (Play at its primary floor, Shuffle,
    /// the heart and "…") fit without wrapping. Also the clock's timeline fallback width.</summary>
    public const float TextMin = 340f;
    /// <summary>The copy's ceiling on a wide card: the veil's opaque plate (its 0.92 stop sits at 30 % of the card)
    /// keeps the long lines readable, and the art keeps the rest.</summary>
    public const float CopyMax = 560f;
    /// <summary>The copy's share of the content width between the floor and the ceiling.</summary>
    public const float CopyShare = 0.5f;
    /// <summary>The narrowest content width that still splits into the copy and art beside it (the copy's 340 floor
    /// plus 192 of art); below it the copy takes the whole width and the veil washes the art under all of it.</summary>
    public const float SplitMin = 532f;
    /// <summary>The narrowest copy that keeps the 40/52 hero title rung; below it the title steps down to the 28/36
    /// display rung so a typical daylist name stays on one line and the card holds its 320 floor.</summary>
    public const float HeroTitleMinText = 480f;

    /// <summary>The copy's text width at content width <paramref name="inner"/>: <see cref="CopyShare"/> of it, clamped
    /// to [<see cref="TextMin"/>, <see cref="CopyMax"/>]; below <see cref="SplitMin"/> the whole (never negative)
    /// width.</summary>
    public static float CopyWidth(float inner)
        => inner < SplitMin ? Math.Max(0f, inner) : Math.Clamp(inner * CopyShare, TextMin, CopyMax);

    /// <summary>Whether a text column <paramref name="textWidth"/> wide keeps the 40/52 hero title (inclusive at
    /// <see cref="HeroTitleMinText"/>).</summary>
    public static bool UseHeroTitle(float textWidth) => textWidth >= HeroTitleMinText;
}

/// <summary>The daylist card's five-segment daypart timeline (Seventh pass, `Daylist.UI.cs`'s `DaylistClock`): one
/// determinate bar per <see cref="Daypart"/>, done parts full, the current one at the window's elapsed fraction,
/// future ones empty; the labels row shares the bars' equal cells.</summary>
public static class DaypartTimeline
{
    /// <summary>One segment per <see cref="Daypart"/>.</summary>
    public const int Segments = 5;
    /// <summary>The gap between two cells (== <c>Spacing.XS</c>).</summary>
    public const float Gap = 4f;

    /// <summary>A segment's fill: 1 before <paramref name="current"/>, the clamped <paramref name="elapsed"/> AT it,
    /// 0 after it.</summary>
    public static float Fill(int segment, int current, float elapsed)
        => segment < current ? 1f : segment == current ? Math.Clamp(elapsed, 0f, 1f) : 0f;

    /// <summary>The daypart the card is in: the daylist title's trailing token when it names one, else the local
    /// hour's window (<see cref="DaypartRules"/>).</summary>
    public static Daypart Current(string? title, int localHour)
        => DaypartRules.TryFromTitle(title, out var fromTitle) ? fromTitle : DaypartRules.OfHour(localHour);

    /// <summary>One equal cell's width in a timeline <paramref name="width"/> wide (five cells, four gaps), never
    /// negative.</summary>
    public static float CellWidth(float width) => Math.Max(0f, (width - Gap * (Segments - 1)) / Segments);

    /// <summary>The loc key of segment <paramref name="segment"/>'s label.</summary>
    public static string LabelKey(int segment) => (Daypart)segment switch
    {
        Daypart.EarlyMorning => Strings.Home.Daypart.Early,
        Daypart.Morning => Strings.Home.Daypart.Morning,
        Daypart.Afternoon => Strings.Home.Daypart.Afternoon,
        Daypart.Evening => Strings.Home.Daypart.Evening,
        _ => Strings.Home.Daypart.Night,
    };
}

/// <summary>Splits the localized "Next daylist in {t}" line around its count, so the count can be its own semibold
/// span while the sentence stays the translator's: format the template with <see cref="Slot"/> as the count, then
/// cut at it. A template that lost the slot keeps its whole text before the count.</summary>
public static class DaylistCountdownLine
{
    /// <summary>The stand-in count (a private-use code point no translation contains).</summary>
    public const string Slot = "";

    public static (string Before, string After) Split(string formatted)
    {
        int i = formatted.IndexOf(Slot, StringComparison.Ordinal);
        return i < 0 ? (formatted, "") : (formatted[..i], formatted[(i + Slot.Length)..]);
    }
}

public static class ItemsRules
{
    /// <summary>One bar's height for the Recently-played "Listening history" week sparkline (<see cref="Items.HistoryItem"/>):
    /// <paramref name="dayCount"/> scaled against the week's peak (<paramref name="weekMax"/>), floored at 1 DIP so a
    /// silent day still draws a sliver rather than vanishing. <paramref name="weekMax"/> is clamped to at least 1 so an
    /// entirely silent week (every day 0) never divides by zero; a negative <paramref name="dayCount"/> (should not
    /// happen, but the caller is a raw int off a signal) floors to 0 rather than drawing a negative-height bar.</summary>
    public static float WeekBarHeight(int dayCount, int weekMax, float maxHeight)
    {
        int max = weekMax < 1 ? 1 : weekMax;
        int count = dayCount < 0 ? 0 : dayCount;
        float h = count / (float)max * maxHeight;
        return h < 1f ? 1f : h;
    }
}
