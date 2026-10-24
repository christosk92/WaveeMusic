// ── Themes/Zune/Structure/Views/Detail/ZuneDetailFrame.cs — Zune's detail frame (themes plan 6.3; P5 Z6a1) ─────────────────────────────────────
//
// Boards Z-Album / -Small / -Huge / -Huge4K (Standard, Small, Huge, Huge 4K: the two-column arm), Z-Album-Compact (the vertical arm, whose
// picture is the hero, `ZuneDetailHero.cs`). The scaffold of every album / playlist / Liked / show / episode page under Zune:
//
//   · the VERTICAL arm (the page column below 640): [tint, tone, page] where the page is the notice over the content column (a show's fixed
//     header over its episodes, or the table that hosts the hero). A TRACK page's vertical arm is ALWAYS wrapped in the insights sheet, open or
//     not, so the tree under the list never changes when the facts arrive (the list keeps its scroll);
//   · the TWO-COLUMN arm: the PANORAMA band (the page's title across the top, its height the record's, folded on a short window), then ONE row
//     of [identity column | list | aside], all in the system PAGE COLUMN (`ZuneFrameRules.PageColumn`: the gutter, capped at 2400 and centred past
//     it, P5 decision 2), so the page x is the band's x at every width (audit A1). Every kind's panorama is the record's step (a long title fits one
//     smaller line, or two at the floor, and the band hugs it); an editable playlist's panorama IS its inline editor (the Title slot). The identity column is the
//     record's width (Z-Album 240), not the core's resizable rail: Zune draws no grip. A collapse stored by another theme shows Zune's strip
//     (`Detail.CompactRail`) with its expand, never a dead end. The list column leads with the "songs" heading on a track page and carries the
//     insights sheet over its rows (open or not);
//   · the HEAD arm of a page with no rail (Liked Songs): the panorama and the meta line over the list, on the same gutter;
//   · the ASIDE: the record's column right of the list (Z-Album 260 "more by"; Huge 400 with the artist and the fans). The core docks it by the
//     WINDOW tier (`DetailTierLayout.AsideDocked`); Zune lays it out by the CONTENT tier, so where a docked panel leaves the page Small the frame
//     folds the aside and provides `Detail.AsideDocked = false` around the page: the album's "more by", artist and fans return to the band under
//     the rows instead of vanishing. The slot is in the row at every tier (width 0 where none docks) and eases its width, so a crossing never
//     re-parents the list. An aside that holds the insights ALONE (Liked Songs) takes the record's insights width (320 / 420 / 440, `InsightsW`);
//     one with a body takes the record's aside. The insights rest open from Huge (the tier policy's `InsightsRestOpen`);
//   · a SHORT window (under 700 tall) folds the panorama band; the identity column leads with the title (`RailTitle`), and the HEAD arm, which has no
//     column, states the title over its meta.
//
// THE ART WASH is the material's: the frame places the core's tint claim and tone plane (Zune's material draws them as the wash, `ZuneMaterial`).
// MOTION: a mode crossing cross-fades the arms (keyed Exit / Enter); the panorama band and the aside ease their sizes; the entrance of the page's
// late rows (fade-up, the position FLIP) is the rail's and the hero's own.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Z = Wavee.Themes.Zune.ZuneDetailRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's detail frame (<see cref="ViewKeys.DetailFrame"/>).</summary>
internal static class ZuneDetailFrame
{
    /// <summary>Provided around the aside's sections (true): the album's "more by" stack draws its aside form (a grid at Huge, rows at Standard)
    /// rather than the band's.</summary>
    public static readonly Context<bool> InAside = new(false);

    /// <summary>Provided (true) around the vertical arm's content: a body that draws its own words full width there (the episode reader) puts them on
    /// the frame's gutter, as the hero and the show header pad theirs; the two-column arm's page column already pads it. A constant per arm, so a
    /// crossing re-renders nothing through it.</summary>
    public static readonly Context<bool> InVerticalArm = new(false);

    /// <summary>The registered template of <see cref="ViewKeys.DetailFrame"/>.</summary>
    public static Element Template(DetailFrameProps p, in ViewContext ctx)
    {
        var enter = ZuneMotion.SwapEnter(ZuneMotion.Rendered(ctx.Hooks));
        var facts = ctx.Tier();
        var lay = ZuneDetailTiers.Of(facts);
        float gutter = ZuneTiers.FrameOf(facts).Gutter;
        IReadSignal<Design.PageAccent>? accent = p.PageAccent;
        bool coreDocked = ctx.Hooks.UseContext(Detail.AsideDocked);   // the core's answer by the window tier, read before any early return

        // ── a TRACK page: one tree at every width (audit B9) ──
        if (p.Vertical && p.VerticalTracks) return OneTree(p, ctx, lay, gutter, accent, coreDocked);

        // ── a show or an episode page: one tree at every width too (audit B9) ──
        return ShowTree(p, ctx, lay, gutter, accent, coreDocked, enter);
    }

    /// <summary>A show's or an episode's frame, ONE tree at every width (audit B9): the notice over the page column of the panorama band over the body, which
    /// stands [identity | gap | episodes] as a row at the two-column form and stacks them (the identity as the fixed header, full width over the episodes)
    /// below the mode edge. The identity is ONE view in both forms (the rail, drawn stacked or as the column: the cover, the facts, the ledger and the rings
    /// keep their nodes and glide), and the episodes column keeps its node, its reader and its scroll. The panorama is the band's in both forms: the
    /// vertical record's on the frame's gutter (folded on a short window), the content tier's inside the page column.</summary>
    static Element ShowTree(DetailFrameProps p, in ViewContext ctx, ZuneDetailLayout lay, float gutter, IReadSignal<Design.PageAccent>? accent, bool coreDocked,
        EnterExit? enter)
    {
        var tc = p.TwoColumn;
        bool stacked = p.Vertical;
        var facts = ctx.Tier();
        bool fold = facts.Policy is not null && facts.Height == HeightTier.Short;
        var hero = ZuneDetailTiers.Hero;
        var panorama = stacked ? (fold ? hero with { PanoramaH = 0f, RailTitle = true } : hero) : lay;
        Element band = (tc?.Rail?.Spec ?? tc?.CompactRail?.Spec) is { } spec
            ? K.PanoramaBand(spec.Identity, spec.Slots, panorama, stacked ? gutter : 0f, enter, Z.PanoramaShift)
            : new BoxEl { Key = Z.PanoramaKey, Height = panorama.PanoramaH, Shrink = 0f, Animate = Z.PanoramaFold };

        // The identity: the column (the record's width, eased across a crossing) or, stacked, the fixed header across the page; the collapsed strip in
        // its place where a collapse stored by another theme reaches the two-column form.
        Element identity;
        if (tc is { Collapsed: true } && !stacked)
            identity = new BoxEl
            {
                Key = Z.CompactRailKey, Direction = 1, Width = tc.CompactWidth, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
                Enter = Z.ArmFade, Exit = Z.ArmFade, Transition = Z.ArmFadeMotion,
                Children = [ctx.View(ViewKeys.DetailCompactRail, tc.CompactRail!)],
            };
        else if (tc?.Rail is { } rail)
            identity = new BoxEl
            {
                // The record's width at the two-column form, the page's at the stacked one: one node, laid out at its new place and size at once (the
                // episodes beside or under it lay out once too) while its presented box slides and reveals there.
                Key = Z.RailKey, Direction = 1, Width = stacked ? float.NaN : lay.RailW, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
                Opacity = stacked ? 1f : tc.RailOpacity, Animate = ZuneMotion.GroupResize,
                Children = [ctx.View(ViewKeys.DetailRail, rail with { RailWidth = stacked ? 0f : lay.RailW, Stacked = stacked })],
            };
        else
            identity = new BoxEl { Key = Z.RailKey, Width = 0f, Shrink = 0f };
        // The gap between the identity column and the episodes eases (a margin cannot): none stacked.
        Element columnGap = new BoxEl { Key = Z.ColumnGapKey, Width = stacked ? 0f : lay.ColumnGap, Shrink = 0f, HitTestVisible = false };
        // The episodes' column keeps its node in both forms and slides to its new place (beside or under the identity); a body that pads its own words on
        // the gutter (the episode reader) learns the form through a value, never a wrapper that comes and goes.
        Element right = Right(p, Ctx.Provide(InVerticalArm, stacked, p.Content)) with { Animate = ZuneMotion.GroupResize };
        Element body = new BoxEl
        {
            Key = "zune.show.body", Direction = (byte)(stacked ? 1 : 0), Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
            AlignItems = FlexAlign.Stretch,
            Children = [identity, columnGap, right],
        };
        Element page = new BoxEl
        {
            Key = "detail:page", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                p.Notice,
                K.CentredPage(new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, DropTarget = p.Drop,
                    Children = [band, body],
                }, stacked ? 0f : gutter) with { Grow = 1f, Shrink = 1f, MinHeight = 0f },
            ],
        };
        return Ctx.Provide(Detail.AsideDocked, coreDocked, Root(p, accent, "page", page));
    }

    /// <summary>A track page's frame, ONE tree at every width (audit B9): the notice over the page column, in which the table stands with the insights layer over
    /// its list column. The table hosts the hero as its item 0 in both forms, and the hero arranges the panorama, the identity column, the list head and the
    /// aside (`ZuneDetailHero`); the frame only eases the page column's gutters (none at the hero form, where the rows run full bleed and the hero stands on
    /// the gutter itself) and lays the insights over the list column (beside the identity column at the columns form). Nothing here is keyed by the form.</summary>
    static Element OneTree(DetailFrameProps p, in ViewContext ctx, ZuneDetailLayout lay, float gutter, IReadSignal<Design.PageAccent>? accent, bool coreDocked)
    {
        bool columns = p.Form == DetailForm.Columns;
        bool dock = columns && ZuneDetailHero.Docks(p.Aside, lay);
        // The insights sheet's layer over the list column (a pass-through layer while the aside holds the insights): the same children either way, so the
        // list never re-parents. At the columns form it starts beside the identity column, as the list does.
        Element layer;
        if (p.Sheet is { } sheet && !dock)
        {
            var open = sheet.Toggle.Open;
            layer = new BoxEl
            {
                Key = "insights:layer", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                HitTestVisible = Prop.Of(() => open.Value),
                Children = [ctx.View(ViewKeys.DetailInsights, sheet with { Page = new BoxEl { Grow = 1f, HitTestVisible = false } })],
            };
        }
        else
            layer = new BoxEl { Key = "insights:layer", Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, HitTestVisible = false };
        float lead = columns && p.TwoColumn is { } tc ? (tc.Collapsed ? tc.CompactWidth : lay.RailW) + lay.ColumnGap : 0f;
        Element host = new BoxEl
        {
            Key = "insights:host", ZStack = true, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                p.Content,
                new BoxEl
                {
                    Key = "insights:layer-row", Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = 0f, HitTestPassThrough = true,
                    Children = [ZuneFrameRules.Gutter(lead), layer],
                },
            ],
        };
        Element page = new BoxEl
        {
            Key = "detail:page", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                p.Notice,
                K.CentredPage(new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, DropTarget = p.Drop,
                    Children = [Right(p, host)],
                }, columns ? gutter : 0f) with { Grow = 1f, Shrink = 1f, MinHeight = 0f },
            ],
        };
        // The core docked the aside by the window tier; the content tier is narrower here (a docked panel's page): the aside folds and what it held returns
        // to the band under the rows. Always provided (a value, never a wrapper that comes and goes).
        return Ctx.Provide(Detail.AsideDocked, p.Aside is not null && !dock ? false : coreDocked, Root(p, accent, "page", page));
    }

    /// <summary>The frame's root: the tint claim, the tone plane (the art wash) and the arm, the page's accent provided around them.</summary>
    static Element Root(DetailFrameProps p, IReadSignal<Design.PageAccent>? accent, string arm, Element body)
        => Ctx.Provide(Design.AccentCtx.Slot, accent, new BoxEl
        {
            Key = Z.FrameKey, ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            OnBoundsChanged = p.OnMeasured, ClipToBounds = true,
            Children = [p.Tint, p.Tone, Arm(arm, body)],
        });

    /// <summary>One arm under its own key: a mode crossing swaps the key and the arms cross-fade in the frame's z-stack.</summary>
    static Element Arm(string arm, Element root) => new BoxEl
    {
        Key = "detail:arm:" + arm, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
        Enter = Z.ArmFade, Exit = Z.ArmFade, Transition = Z.ArmFadeMotion,
        Children = [root],
    };

    /// <summary>The content column under the host's key (it survives a collapse, so the table reconciles in place).</summary>
    static BoxEl Right(DetailFrameProps p, Element body) => new()
    {
        Key = p.ContentKey, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = p.ContentMinWidth, MinHeight = 0f, Direction = 1,
        Children = [body],
    };
}
