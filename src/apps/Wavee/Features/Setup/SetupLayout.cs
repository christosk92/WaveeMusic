using System;

namespace Wavee;

/// <summary>The setup plate's width-pressure ladder. Wide is the approved 896×576 composition with the 344-DIP
/// stage rail; Compact drops that tertiary rail; Narrow stacks dense page-specific rows; UltraNarrow also stacks
/// the footer commands. Pure by construction so resize hysteresis is pinned in Wavee.Tests.</summary>
enum SetupLayoutTier { Wide, Compact, Narrow, UltraNarrow }

/// <summary>What the shell BEHIND the setup plate should look like while a page is open (the <c>SetupSession.Covering</c>
/// reader, work package I). <c>Live</c> = the shell paints un-dimmed so a page whose stage is a genuine live preview
/// (Appearance, Sidebar) reads as "this window IS the preview"; <c>Dim</c> = the ordinary modal scrim; <c>None</c> = no
/// scrim at all (the pre-auth bare mount, where the engine's own Modal scrim already paints over bare Mica).</summary>
enum SetupCover { None, Dim, Live }

/// <summary>All setup sizing and tier decisions in one place. Narrowing happens immediately; widening needs a
/// 24-DIP recovery band so a live resize cannot chatter across a boundary.
///
/// <para>The Wide-tier geometry below is the "stage + decision" composition: an 896×576 plate divides into a 344-DIP
/// STAGE column (the live-preview/roadmap/caption rail — <see cref="SetupStage"/> builds its content) and a 480-DIP
/// DECISION column (the page's actual rows/cards — <see cref="SetupCompact"/>/<see cref="SetupDecision"/> build its
/// content), separated by a 24-DIP gap. Every function here is a pure arithmetic fact about that grid, pinned by
/// <c>SetupLayoutTests</c> so a page's row plan can be checked against its budget WITHOUT running the engine.</para></summary>
static class SetupLayout
{
    public const float TargetWidth = 896f;
    public const float TargetHeight = 576f;
    public const float MinWidth = 300f;
    public const float MinHeight = 200f;
    public const float ViewportMargin = 32f;

    public const float HeroEnterWidth = 700f;
    public const float NarrowEnterWidth = 520f;
    public const float UltraNarrowEnterWidth = 360f;
    public const float TierHysteresisDip = 24f;

    public const float FooterHeight = 80f;
    public const float NarrowFooterHeight = 108f;
    public const float UltraNarrowFooterHeight = 144f;
    public const float ProgressLaneWidth = 210f;
    public const float ProgressWidth = 162f;
    public const float CompactPairingWidth = 196f;
    public const float CompactQrSize = 138f;
    public const float CompactDividerWidth = 40f;
    public const float SignInBodyMinHeight = 336f;

    /// <summary>Kept even though the agreement moved out of <c>SetupTermsPage.Agreement()</c>'s own inline flow in the
    /// stage/decision rework — <c>SetupPage.Terms.cs</c> still uses it to size the <c>SettingsExpander</c> header that
    /// hosts "Read the full agreement". Do not delete without checking that call site first.</summary>

    // ── the stage column (344 DIP) ──────────────────────────────────────────────────────────────────────────────────
    public const float StageWidth = 344f;
    public const float HeroArtSize = 192f;
    public const float StageInset = 24f;
    public const float StageInnerWidth = StageWidth - 2f * StageInset;   // 296
    public const float StageMiniatureHeight = 312f;
    public const float StageGap = 12f;
    public const float StageCaptionHeight = 34f;

    // ── the frame's own outer padding (Edges4(24,24,24,12), SetupPageHost.cs) — both columns sit inside this ─────────
    public const float ColumnPadTop = 24f;
    public const float ColumnPadBottom = 12f;
    public const float DecisionGap = 24f;

    // ── the decision column's pinned header (eyebrow + title + one-line lead) ───────────────────────────────────────
    public const float HeaderHeight = 80f;   // eyebrow 16 + XS 4 + title 36 + XS 4 + lead 20
    public const float HeaderGap = 12f;
    public const float LeadLineHeight = 20f;

    // ── compact rows (SetupCompact.Row/ChipRow/ClickRow) ────────────────────────────────────────────────────────────
    public const float RowHeight = 44f;
    public const float RowSubHeight = 52f;
    public const float RowGap = 4f;
    public const float LabelLane = 150f;
    public const float LabelLaneSub = 200f;
    public const float RowPadX = 12f;
    public const float RowPadY = 6f;
    public const float ControlGap = 8f;
    public const float SegmentedHeight = 32f;
    public const float ChipHeight = 28f;
    public const float SwatchSize = 22f;

    // ── the sidebar-design rows (SetupStage/SidebarDesignPicker.Rows) ───────────────────────────────────────────────
    public const float SidebarRowHeight = 100f;
    public const float SidebarThumbW = 120f;
    public const float SidebarThumbH = 84f;

    public const float VersionListMaxHeight = 256f;
    public const float InfoCardHeight2 = 68f;
    public const float InfoCardHeight3 = 84f;
    public const float FinePrintLine = 17f;

    /// <summary>Appearance's seven compact rows (six plain + one two-line), for the fold-hero-only fit theory: adding
    /// an eighth 44-DIP row would push the column past the 459-DIP lane.</summary>
    public static readonly float[] AppearanceRowPlan = [44f, 44f, 44f, 44f, 44f, 52f, 44f];
    /// <summary>The sidebar chooser's three 100-DIP design rows.</summary>
    public static readonly float[] SidebarRowPlan = [100f, 100f, 100f];

    public static float PlateWidth(float viewportWidth) => viewportWidth > 0f
        ? Math.Clamp(TargetWidth, MinWidth, Math.Max(MinWidth, viewportWidth - ViewportMargin))
        : TargetWidth;

    public static float PlateHeight(float viewportHeight) => viewportHeight > 0f
        ? Math.Clamp(TargetHeight, MinHeight, Math.Max(MinHeight, viewportHeight - ViewportMargin))
        : TargetHeight;

    public static SetupLayoutTier NominalTierFor(float plateWidth) => plateWidth switch
    {
        >= HeroEnterWidth => SetupLayoutTier.Wide,
        >= NarrowEnterWidth => SetupLayoutTier.Compact,
        >= UltraNarrowEnterWidth => SetupLayoutTier.Narrow,
        _ => SetupLayoutTier.UltraNarrow,
    };

    public static SetupLayoutTier TierFor(float plateWidth, SetupLayoutTier current, bool initialized = true)
    {
        if (plateWidth <= 0f) return current;
        if (!initialized) return NominalTierFor(plateWidth);

        SetupLayoutTier nominal = NominalTierFor(plateWidth);
        if (nominal > current) return nominal; // pressure increased: drop structure immediately
        if (nominal == current) return current;

        // Pressure decreased: re-admit each rung only after its own 24-DIP recovery band.
        return current switch
        {
            SetupLayoutTier.Compact when plateWidth >= HeroEnterWidth + TierHysteresisDip
                => SetupLayoutTier.Wide,
            SetupLayoutTier.Narrow when plateWidth >= NarrowEnterWidth + TierHysteresisDip
                => NominalTierFor(plateWidth),
            SetupLayoutTier.UltraNarrow when plateWidth >= UltraNarrowEnterWidth + TierHysteresisDip
                => NominalTierFor(plateWidth),
            _ => current,
        };
    }

    public static bool ShowsHero(SetupLayoutTier tier) => tier == SetupLayoutTier.Wide;
    public static bool StacksSignIn(SetupLayoutTier tier) => tier >= SetupLayoutTier.Narrow;
    public static bool StacksFooter(SetupLayoutTier tier) => tier >= SetupLayoutTier.Narrow;
    public static bool StacksFooterActions(SetupLayoutTier tier) => tier == SetupLayoutTier.UltraNarrow;
    public static float FooterHeightFor(SetupLayoutTier tier) => tier switch
    {
        SetupLayoutTier.UltraNarrow => UltraNarrowFooterHeight,
        SetupLayoutTier.Narrow => NarrowFooterHeight,
        _ => FooterHeight,
    };

    /// <summary>The decision column's width at Wide: the plate minus its own 48-DIP horizontal padding (24 each
    /// side), the 344-DIP stage and the 24-DIP gap between them. 896 → 480.</summary>
    public static float DecisionWidth(float plateWidth) => plateWidth - 48f - StageWidth - DecisionGap;

    /// <summary>The full vertical lane both columns share: the plate minus the footer, the 1-px hairline above it,
    /// and the frame's own top/bottom padding. 576 → 459 at Wide.</summary>
    public static float DecisionLaneHeight(float plateHeight, SetupLayoutTier tier) =>
        plateHeight - FooterHeightFor(tier) - 1f - ColumnPadTop - ColumnPadBottom;

    /// <summary>What's left of the (Wide, <see cref="TargetHeight"/>) lane for a decision column's OWN rows once the
    /// pinned header (eyebrow + title + lead) and its gap are subtracted. <paramref name="leadLines"/> beyond one adds
    /// <see cref="LeadLineHeight"/> per extra line (a two-line lead costs 20 DIP of body budget). 367 for a one-line
    /// lead, 347 for two.</summary>
    public static float DecisionBodyBudget(int leadLines) =>
        DecisionLaneHeight(TargetHeight, SetupLayoutTier.Wide) - (HeaderHeight + LeadLineHeight * (leadLines - 1)) - HeaderGap;

    /// <summary>The stacked height of <paramref name="plainRows"/> 44-DIP rows and <paramref name="subRows"/> 52-DIP
    /// (label+sub) rows, plus a 4-DIP gap between every pair. Zero rows is zero height, not a negative gap.</summary>
    public static float RowsHeight(int plainRows, int subRows)
    {
        int total = plainRows + subRows;
        if (total <= 0) return 0f;
        return RowHeight * plainRows + RowSubHeight * subRows + RowGap * (total - 1);
    }

    /// <summary>The total height of a decision column built from <paramref name="rows"/> row heights (gapped by
    /// <see cref="RowGap"/>), optionally under the pinned header band (<paramref name="lead"/> = the column sits below
    /// the frame's own header, so <see cref="HeaderHeight"/> + <see cref="HeaderGap"/> count toward the total). Compare
    /// against <see cref="DecisionLaneHeight"/> (the FULL lane, header included) — not <see cref="DecisionBodyBudget"/>,
    /// which is already header-subtracted and is for pages that measure their body in isolation instead (sign-in,
    /// local playback, terms).</summary>
    public static float ColumnHeight(bool lead, ReadOnlySpan<float> rows)
    {
        float sum = 0f;
        for (int i = 0; i < rows.Length; i++) sum += rows[i];
        if (rows.Length > 1) sum += RowGap * (rows.Length - 1);
        return (lead ? HeaderHeight + HeaderGap : 0f) + sum;
    }

    /// <summary>Does a body-only height (a sign-in phase, a runtime facet — everything BELOW the frame's own pinned
    /// header) fit the budget left over for it at <paramref name="leadLines"/>?</summary>
    public static bool FitsWide(float height, int leadLines) => height <= DecisionBodyBudget(leadLines);

    /// <summary>What's left of a <see cref="SetupCompact.Row"/>'s width for its trailing control once the row's own
    /// horizontal padding and label lane are subtracted. 298 for a plain row (150-DIP label), 248 with a sub-label
    /// (200-DIP label).</summary>
    public static float ControlLane(float columnWidth, bool sub) =>
        columnWidth - 2f * RowPadX - (sub ? LabelLaneSub : LabelLane) - ControlGap;

    /// <summary>The local-playback progress/detail bar's width per tier — narrower at Wide/Narrow (the decision column
    /// is 480/full-width respectively but the bar sits beside other chrome), full-width at Compact (no stage column to
    /// share the row with), narrowest at UltraNarrow.</summary>
    public static float RuntimeBarWidth(SetupLayoutTier tier) => tier switch
    {
        SetupLayoutTier.Compact => 412f,
        SetupLayoutTier.UltraNarrow => 220f,
        _ => 296f,
    };

    /// <summary>Whether the shell behind the plate should stay un-dimmed (a genuine live preview — Appearance and
    /// Sidebar are the two pages whose stage IS a miniature of that shell), the ordinary dim scrim, or no scrim at all
    /// when there is no shell mounted behind the plate to begin with (the pre-auth bare mount).</summary>
    public static SetupCover CoverFor(SetupPage page, bool shellBehind) => !shellBehind
        ? SetupCover.None
        : page is SetupPage.Appearance or SetupPage.Sidebar ? SetupCover.Live : SetupCover.Dim;
}
