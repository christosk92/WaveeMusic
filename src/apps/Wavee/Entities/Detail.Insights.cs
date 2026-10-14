// ── Entities/Detail.Insights.cs ────────────────────────────────────────────────────────────────────────────────────
// THE INSIGHTS SHEET, CORE HALF — the vertical arm's host for the facts bento (approved prototype "Insights Sheet").
//
// Role: UI (the toggle, the band wrapper and the band's width claim are core; the sheet and its two entry points are the structure's)
// Owner: M
// Wave: 4.5
// Spec: the approved HTML prototype; the arm/width/open rules are Detail.InsightsSheet (Detail.cs §3b).
//
// T9a (themes plan 6.1): the sheet itself (the engine SplitView overlay, its frosted pane, the scrim and cast, the header and the body),
// the hero toolbar's glyph button and the pinned band's word are CLASSIC'S (`Themes\Classic\Structure\Views\Detail\ClassicInsights.cs`, keys
// `Detail.Insights` and `Detail.InsightsToggle`). What stays here: `InsightsToggle` (the open state and its verbs, one per mounted frame),
// `InsightsBandAction` (the band's keyed slot, called by the table), `BandActionsClaim` (the band's width claim) and the reasoning below.
//
// ── WHY THIS EXISTS ──────────────────────────────────────────────────────────────────────────────────────────────────
//
// A two-column detail page puts the facts bento in its left rail, beside the list. The VERTICAL (hero/stacked) arm has
// no rail, so the bento used to be appended under the rows as a page footer nobody scrolled to. Here it becomes a
// right-anchored sheet laid OVER the content, dismissed by Escape, the scrim, the close button, the toggle again, or
// navigating away.
//
// TWO ENTRY POINTS, ONE CONTROL: the hero's toolbar row (§6) carries it while the reader is at the top of the page, and
// the pinned 56-DIP context band (§7) carries it for the whole of the rest of the page — because the hero COLLAPSES,
// and a sheet meant to be read ALONGSIDE the list cannot be opened from chrome that scrolls away. They are never both
// legible (the band crossfades in exactly as the hero fades out) and never both interactive (input crosses with the
// pin, not with the paint). §7 carries the whole argument.
//
// ── WHAT IS ENGINE, AND WHAT IS OURS ─────────────────────────────────────────────────────────────────────────────────
//
// The overlay itself is the engine's `SplitView` in Overlay × Right: it owns the WinUI slide (350 ms open / 120 ms
// close on the 0.1,0.9 0.2,1.0 spline — reduced motion snaps it, because a LayoutTransition's position channel is
// KeepFade), the light-dismiss layer, Escape, the host-resize dismiss and the whole FOCUS contract (save the focused
// node, move focus into the pane, trap Tab inside it, restore on close). We add only what a SplitView has no opinion
// about: the SCRIM and its edge shadow (drawn inside the content slot, below the control's own dismiss layer), the
// pane's surface styling through `TemplateParts`, the header row, the scrollable body and the (deliberately inert)
// left-edge grip.
//
// The pane is composed in the SHARED FRAME (`Detail.FrameHost` decides, the structure draws), not per page: every facts-bearing page
// declares the SAME `FrameSlots.LikedFacts` builder it already declares for the rail, and the frame decides which arm hosts it.
//
// NOT WIRED, ON PURPOSE: the grip is a visual only. An edge-swipe to open/close is undecided and the right edge is
// contested by the transcript rail.
//
// NAME NOTE: inside `Detail`, the nested `Text`/`Skeleton`/`Config` classes shadow `Ui.Text` and friends — text runs
// here are `new TextEl(...)` / `Ui.*`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Contracts = Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Detail
{
    // ══ 1. THE TOGGLE (frame-owned state; the hero's toolbar row renders the button) ══════════════════════════════════

    /// <summary>The sheet's open state plus the verbs that move it — ONE instance per mounted frame, handed to the
    /// vertical arm through <see cref="VerticalSpec"/> so the toolbar button and the pane cannot disagree. Not part of
    /// any equality: it is a stable per-host object, never data.</summary>
    public sealed class InsightsToggle
    {
        public readonly Signal<bool> Open = new(false);

        /// <summary>The node a close hands focus back to: the control the reader last USED, not the last one realized.
        /// There are two entry points (the hero's toolbar button and the pinned band's word — §6/§7), only one of which
        /// owns input at any scroll position, so "the last one realized" would routinely hand focus to the one that is
        /// off screen. Each captures its own handle at realize; the click records which of the two moved the sheet.
        /// <para>It matters because the control's own saved-focus restore can have nothing to restore (the sheet was
        /// opened by a keyboard shortcut, or the opener was re-realized while the sheet was open).</para></summary>
        internal readonly NodeHandle[] ButtonNode = [NodeHandle.Null];
        readonly NodeHandle[] _heroNode = [NodeHandle.Null], _bandNode = [NodeHandle.Null];

        public InsightsToggle()
        {
            Toggle = () => { ButtonNode[0] = _heroNode[0]; Open.Value = !Open.Peek(); };
            ToggleFromBand = () => { ButtonNode[0] = _bandNode[0]; Open.Value = !Open.Peek(); };
            Close = () => { if (Open.Peek()) Open.Value = false; };
            // Seed the focus target with whichever entry point realizes first, so a sheet opened before either has been
            // clicked (a route restore) still has somewhere to hand focus back to.
            CaptureButton = h => { _heroNode[0] = h; if (ButtonNode[0].IsNull) ButtonNode[0] = h; };
            CaptureBandButton = h => { _bandNode[0] = h; if (ButtonNode[0].IsNull) ButtonNode[0] = h; };
        }

        /// <summary>The HERO toolbar button's click: open if closed, close if open.</summary>
        public readonly Action Toggle;
        /// <summary>The pinned BAND word's click — the same verb, recording the other opener for the focus return.</summary>
        public readonly Action ToggleFromBand;
        /// <summary>Close from anywhere (the × , a route change, leaving the vertical arm). Value-gated.</summary>
        public readonly Action Close;
        internal readonly Action<NodeHandle> CaptureButton;
        internal readonly Action<NodeHandle> CaptureBandButton;
    }

    // ══ 7. THE PINNED BAND'S TOGGLE ══════════════════════════════════════════════════════════════════════════════════
    //
    // ── WHY THE TOGGLE IS IN TWO PLACES, AND WHY THAT IS NOT DUPLICATION ────────────────────────────────────────────
    //
    // The hero COLLAPSES. Its toolbar row is legible only while the reader is at the top of the page, and the sheet
    // exists to be consulted WHILE READING THE LIST — so a toggle that lives only up there is gone exactly when it is
    // wanted. The vertical arm's other chrome is the pinned 56-DIP context band, whose right cluster restates the
    // page's verbs as plateless words: Find · Filter · Play. Every one of those three is ALREADY a restatement of
    // something the hero carries (the command bar's search glyph, its funnel, the hero's Play pill) — the band is not a
    // second set of controls, it is the SAME controls after the hero has scrolled away. Insights joins that cluster on
    // exactly those terms, which is why the hero button STAYS:
    //
    //   · the two are never both legible. The band's opacity ramps 0 → 1 over the LAST 44 DIP of the collapse
    //     (VerticalLayout.CompactRevealStart) while the expanded hero fades 1 → 0 over the last 96 (ExpandedFadeStart);
    //     at rest the band is fully transparent, and once stuck the hero is gone. What overlaps is a crossfade, which
    //     is the handoff, not two buttons competing for the same glance;
    //   · INPUT is exclusive by construction and follows the pin, not the paint: the band takes hits only once its
    //     chrome is stuck (the chrome sticky's engaged edge → `TableHost._compactInteractive` → the band's HitTestVisible)
    //     and the hero's
    //     presentation stops taking them at the same edge. InsightsSheet.BandToggleTakesInput / HeroToggleTakesInput
    //     state that invariant, and DetailInsightsSheetTests pins it: at every scroll position EXACTLY ONE of the two
    //     is reachable. Dropping the hero one would therefore leave the whole pre-stuck range — the top of the page,
    //     where a reader most naturally reaches for the facts — with no way to open the sheet at all.
    //
    // Position in the cluster: Find · Filter · Insights · Play. The three verbs that shape WHAT YOU SEE sit together
    // and the one primary verb stays terminal.

    /// <summary>The pinned band's entry point: the sheet's toggle as a plateless WORD in the Find · Filter · Play
    /// cluster, latched to accent ink while the sheet is open (<c>Controls.TextAction</c>'s own <c>toggledOn</c> arm —
    /// accent in that band always means "the primary verb" or "this is on").
    /// <para>Its own component, so that opening or closing the sheet re-renders ONE word: the band is built by the
    /// table's <c>HeroPartsFor</c>, and reading the open signal out there would re-render the table host — and with it
    /// the list. The toggle instance is mount-stable per frame host (a propless factory is safe here: the field cannot
    /// go stale, because a page that loses its facts unmounts this element outright rather than handing it a different
    /// toggle).</para></summary>
    public static Element InsightsBandAction(InsightsToggle toggle)
        => ViewSlots.View(Contracts.ViewKeys.DetailInsightsToggle,
               new Contracts.DetailInsightsToggleProps(toggle, Contracts.DetailInsightsToggleForm.BandWord)) with { Key = "band:insights" };

    /// <summary>What the band's search field must leave for the cluster once the toggle joins it — the label widths the
    /// band estimates, routed through the one pure rule so the field narrows instead of shoving the words off the
    /// band's right edge (<see cref="InsightsSheet.BandActionsWidth"/>).</summary>
    public static float BandActionsClaim(string find, string filter, string play, bool showsToggle)
        => InsightsSheet.BandActionsWidth(
            BandLayout.EstimateLabelWidth(find, BandLayout.ActionPadX),
            BandLayout.EstimateLabelWidth(filter, BandLayout.ActionPadX),
            BandLayout.EstimateLabelWidth(play, BandLayout.ActionPadX),
            BandLayout.EstimateLabelWidth(Loc.Get(Strings.Detail.LikedFacts.SheetOpen), BandLayout.ActionPadX),
            showsToggle);
}
