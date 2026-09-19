using System;

namespace Wavee.Features.Detail;

[Flags]
public enum DetailTrackInlineCommand : byte
{
    None = 0,
    Shuffle = 1,
    Sort = 2,
    Density = 4,
    Select = 8,
}

/// <summary>Measured labeled widths for the commands whose presence the adaptive resolver controls.</summary>
public readonly record struct DetailTrackCommandWidths(
    float Play,
    float Tune,
    float Shuffle,
    float Sort,
    float Density,
    float Select);

public readonly record struct DetailTrackCommandBarFit(
    DetailTrackInlineCommand Inline,
    bool SearchExpanded,
    float SearchWidth)
{
    public bool Has(DetailTrackInlineCommand command) => (Inline & command) != 0;
    internal int Richness =>
        (SearchExpanded ? 8 : 0)
        + (Has(DetailTrackInlineCommand.Shuffle) ? 4 : 0)
        + (Has(DetailTrackInlineCommand.Sort) ? 2 : 0)
        + (Has(DetailTrackInlineCommand.Density) ? 1 : 0)
        + (Has(DetailTrackInlineCommand.Select) ? 1 : 0);
}

/// <summary>Pure priority fit for Wavee's playlist command bar. It never returns a layout wider than its input.</summary>
public static class DetailTrackCommandBarLayout
{
    public const float MoreWidth = 32f;
    // The resting affordance is deliberately TWO adjacent buttons: query + filter. The field expands only after the
    // user invokes query; spare width is for commands/column readability, not an always-open empty text box.
    public const float SearchIconWidth = 66f;
    public const float SearchMinExplicit = 160f;
    public const float SearchPreferred = 240f;
    public const float SearchMax = 280f;
    public const float Gap = 2f;
    public const float SearchGap = 8f;
    public const float GroupSeparatorWidth = 17f;
    public const float PromotionHysteresis = 16f;

    public static DetailTrackCommandBarFit Resolve(
        float available,
        in DetailTrackCommandWidths widths,
        bool vertical,
        bool hasTune,
        bool hasSelect,
        bool explicitSearch,
        DetailTrackCommandBarFit? previous = null)
    {
        available = MathF.Max(0f, available);
        var candidate = ResolveCore(available, widths, vertical, hasTune, hasSelect, explicitSearch);
        // Hysteresis applies in EVERY mode. It used to be skipped whenever search was open, which is exactly when it
        // matters most: opening the field evicts commands, their exit animation re-measures them, those measurements
        // re-enter here, and the promoted set oscillates while the width tween is still mid-flight.
        if (previous is not { } old || candidate.Richness <= old.Richness)
            return candidate;

        // Promote only with a small reserve. Narrowing remains immediate, so nothing can clip while the pane contracts.
        return ResolveCore(MathF.Max(0f, available - PromotionHysteresis),
            widths, vertical, hasTune, hasSelect, explicitSearch);
    }

    static DetailTrackCommandBarFit ResolveCore(
        float available,
        in DetailTrackCommandWidths widths,
        bool vertical,
        bool hasTune,
        bool hasSelect,
        bool explicitSearch)
    {
        float mandatory = MoreWidth;
        int mandatoryCount = 1; // More
        if (!vertical) { mandatory += widths.Play; mandatoryCount++; }
        if (hasTune) { mandatory += widths.Tune; mandatoryCount++; }
        mandatory += MathF.Max(0, mandatoryCount - 1) * Gap;

        bool expanded = explicitSearch;
        float reservedSearch = expanded ? SearchMinExplicit : SearchIconWidth;

        DetailTrackInlineCommand inline = DetailTrackInlineCommand.None;
        float used = mandatory + SearchGap + reservedSearch;

        void Add(DetailTrackInlineCommand command, float width, bool viewCommand)
        {
            float extra = Gap + width;
            bool firstView = viewCommand
                && (inline & (DetailTrackInlineCommand.Sort | DetailTrackInlineCommand.Density | DetailTrackInlineCommand.Select)) == 0;
            if (firstView && (!vertical || hasTune || mandatoryCount > 1)) extra += GroupSeparatorWidth;
            if (used + extra > available) return;
            used += extra;
            inline |= command;
        }

        if (!vertical) Add(DetailTrackInlineCommand.Shuffle, widths.Shuffle, viewCommand: false);
        Add(DetailTrackInlineCommand.Sort, widths.Sort, viewCommand: true);
        Add(DetailTrackInlineCommand.Density, widths.Density, viewCommand: true);
        if (hasSelect) Add(DetailTrackInlineCommand.Select, widths.Select, viewCommand: true);

        float searchWidth = reservedSearch;
        if (expanded)
        {
            float spare = MathF.Max(0f, available - used);
            searchWidth = Math.Clamp(reservedSearch + spare, SearchMinExplicit, SearchMax);
        }
        return new DetailTrackCommandBarFit(inline, expanded, searchWidth);
    }
}
