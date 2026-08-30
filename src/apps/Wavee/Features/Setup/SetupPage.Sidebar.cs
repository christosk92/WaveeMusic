using System.Collections.Generic;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 5 · Sidebar (<c>data-step="5"</c>). Three tall radio rows (<see cref="SidebarDesignPicker.Rows"/>)
/// instead of the Settings-tab card strip — a full-width row reads better than a square card in a 480-DIP decision
/// column. The two shipped layouts apply LIVE through <c>SidebarPreferences.SwitchDesign</c> (the shell behind this
/// very plate repaints on every click — <see cref="SetupLayout.CoverFor"/> keeps that shell un-dimmed for exactly
/// this page); Custom remains visible as a disabled "Coming soon" row outside the radio group and exposes no
/// template/customizer controls until that design is ready.</summary>
sealed class SetupSidebarPage : Component
{
    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;

        // Tier hysteresis (SetupPage.SignIn.cs's own pattern): below Wide there is no stage column at all, so this
        // page's own live-preview miniature would otherwise vanish entirely rather than degrade.
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        bool showsHero = SetupLayout.ShowsHero(tierSig.Value);

        // 76 (header) + 12 (header gap) + 3×100 (rows) + 2×4 (row gaps) = 396 ≤ 459 (the Wide decision lane) — pinned
        // against SetupLayout.SidebarRowPlan so a future row-height change trips this assert instead of silently
        // scrolling.
        Debug.Assert(
            SetupLayout.ColumnHeight(true, SetupLayout.SidebarRowPlan)
                <= SetupLayout.DecisionLaneHeight(SetupLayout.TargetHeight, SetupLayoutTier.Wide),
            "SetupSidebarPage's three rows must fit the Wide decision lane without scrolling.");

        var bodyChildren = new List<Element>(4)
        {
            SidebarDesignPicker.Rows(prefs, settings, allowCustom: false),
        };
        if (!showsHero)
        {
            // No stage column at this tier — fold the live-preview miniature into the body instead of losing it.
            bodyChildren.Add(Embed.Comp(() => new SidebarStageView()) with { Key = "setup:sidebar:stage:inline" });
        }
        bodyChildren.Add(SetupCompact.Spacer());
        bodyChildren.Add(SetupCompact.FinePrint(Loc.Get(Strings.Setup.Sidebar.AlsoFromMenu)));

        Element body = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Gap = SetupLayout.RowGap, AlignItems = FlexAlign.Stretch,
            Children = bodyChildren.ToArray(),
        };

        return SetupPageHost.Frame(SetupPage.Sidebar, Loc.Get(Strings.Setup.Eyebrow.Sidebar),
            Loc.Get(Strings.Sidebar.Chooser.Title), body,
            lead: Loc.Get(Strings.Setup.Sidebar.Lead),
            stage: Embed.Comp(() => new SidebarStageView()) with { Key = "setup:sidebar:stage" },
            scrollBody: false);
    }
}

/// <summary>The Sidebar page's stage-column content: a 296×312 miniature shell (title bar / two-pane body / player
/// bar) drawn from the SAME live selection the decision column's rows write to — clicking a row repaints this
/// instantly, same as it repaints the real shell behind the plate. No ctor args: reads
/// <see cref="SidebarPreferences.Slot"/> itself (falling back to the persisted setting when the service isn't
/// mounted, e.g. a standalone preview) so a page-level remount is never required to pick up a design switch made
/// elsewhere (the sidebar's own layout menu, Settings).
///
/// <para>The title bar/player bar chrome is the SHARED <see cref="SetupMiniChrome"/> (landed by the Appearance work
/// package for its own 296×312 miniature, <see cref="AppearanceStageView"/>) — this view supplies no "now playing"
/// state of its own, so its player bar's <c>lyricsMotion</c> argument is always false.</para></summary>
sealed class SidebarStageView : Component
{
    const float PaneWidth = 112f;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var design = prefs is not null
            ? prefs.Design.Value
            : SidebarDesignGating.ActiveDesign(UseContext(Services.Slot)?.Settings);
        var ink = WaveePicker.Ink.For(true);

        var m = SidebarDesignPicker.Metrics.Stage;
        Element pane = new BoxEl
        {
            Width = PaneWidth, Shrink = 0f, Direction = 1, Gap = m.Gap,
            Padding = Edges4.All(10f), ClipToBounds = true,
            Fill = Tok.FillCardSecondary,
            Children = SidebarDesignPicker.Preview(design, in m, ink),
        };

        Element body = new BoxEl
        {
            Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
            Children = [pane, VHairline(), Workspace(ink)],
        };

        Element miniature = new BoxEl
        {
            Width = SetupLayout.StageInnerWidth, Height = SetupLayout.StageMiniatureHeight, Shrink = 0f,
            Direction = 1, ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillSolidBase, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Shadow = Elevation.Card,
            Children = [SetupMiniChrome.TitleBar(ink), body, SetupMiniChrome.PlayerBar(false, ink)],
        };

        return SetupStage.Column(
            miniature,
            SetupStage.Spacer(),
            SetupStage.Caption(
                Loc.Get(Strings.Setup.Sidebar.StageCaptionTitle),
                Loc.Get(Strings.Setup.Sidebar.StageCaptionSub)));
    }

    /// <summary>A vertical hairline — the pane/workspace divider. Distinct from
    /// <see cref="Wavee.SidebarMiniature.Hairline"/> (that one is horizontal only).</summary>
    static Element VHairline() => new BoxEl
    {
        Width = 1f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Fill = Tok.StrokeDividerDefault,
    };

    /// <summary>The main-content diagram beside the sidebar pane — 3 <see cref="SidebarMiniature.Bar"/>s (a page
    /// heading + two lines) over a 2-up <see cref="SidebarMiniature.GridCell"/> row, using only the PUBLIC
    /// primitives (not the private, 220-tall <c>SidebarMiniature.Workspace</c>, which is a different, denser
    /// diagram for the template-confirmation card).</summary>
    static Element Workspace(WaveePicker.Ink ink) => new BoxEl
    {
        Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Padding = Edges4.All(10f), Gap = 6f,
        Children =
        [
            SidebarMiniature.Bar(72f, 6f, ink.Block),
            SidebarMiniature.Bar(96f, 4f, ink.Faint),
            SidebarMiniature.Bar(58f, 4f, ink.Faint),
            new BoxEl
            {
                Direction = 0, Gap = 6f, Shrink = 0f,
                Children = [SidebarMiniature.GridCell(ink.Block, ink.Faint), SidebarMiniature.GridCell(ink.Block, ink.Faint)],
            },
        ],
    };
}
