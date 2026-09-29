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
/// null (the art column still shows, as the watched placeholder). The column's SHAPE is the card's, never the
/// picture's — the art is cover-cropped into it — so no aspect or width cap rides along.</summary>
public static class DaylistArt
{
    public static string? Of(string? headerImage, string? image)
        => headerImage is { Length: > 0 } ? headerImage : image is { Length: > 0 } ? image : null;
}

/// <summary>The daylist card's form (Seventh pass, `Daylist.UI.cs`): the text column holds its
/// <see cref="TextMin"/> (the 40/52 title's line plus the one-row action strip), the art takes what is left of the
/// card's content width after the <see cref="Gap"/>, and below <see cref="ArtMin"/> of art the card drops the art
/// and is the text column alone. <c>inner</c> is the card's content width (after its padding).</summary>
public static class DaylistForm
{
    /// <summary>The text column's floor while the art shows.</summary>
    public const float TextMin = 340f;
    /// <summary>The text ↔ art gap (== <c>Spacing.XXXL</c>, which the card's row uses).</summary>
    public const float Gap = 32f;
    /// <summary>The narrowest art worth showing.</summary>
    public const float ArtMin = 160f;

    public static bool ShowArt(float inner) => inner - Gap - TextMin >= ArtMin;

    /// <summary>The text column's MinWidth at <paramref name="inner"/>: <see cref="TextMin"/> beside the art; alone,
    /// never more than the card offers (a floor wider than the card would overflow it).</summary>
    public static float TextMinFor(float inner)
        => ShowArt(inner) ? TextMin : Math.Clamp(inner, 0f, TextMin);
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
