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
//     it, P5 decision 2), so the page x is the band's x at every width (audit A1). Every kind's panorama is the record's step (a long title wraps
//     and the band hugs it); an editable playlist's panorama IS its inline editor (the Title slot). The identity column is the
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

    /// <summary>The registered template of <see cref="ViewKeys.DetailFrame"/>.</summary>
    public static Element Template(DetailFrameProps p, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var lay = ZuneDetailTiers.Of(facts);
        float gutter = ZuneTiers.FrameOf(facts).Gutter;
        IReadSignal<Design.PageAccent>? accent = p.PageAccent;

        // ── the vertical / hero system ──
        if (p.Vertical)
        {
            Element content = new BoxEl
            {
                Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, DropTarget = p.Drop,
                Children = p.ShowHeader is { } header ? [ctx.View(ViewKeys.DetailShowHeader, header), Right(p, p.Content)] : [Right(p, p.Content)],
            };
            Element verticalPage = new BoxEl
            {
                Key = "detail:vertical", Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
                Children = [p.Notice, content],
            };
            Element verticalRoot = p.VerticalTracks && p.Sheet is { } sheet
                ? ctx.View(ViewKeys.DetailInsights, sheet with { Page = verticalPage })
                : verticalPage;
            return Root(p, accent, "vertical", verticalRoot);
        }

        bool dock = p.Aside is not null && lay.AsideW > 0f;
        // The panorama is the content tier's record for every kind (the album's step); a long title wraps and the band hugs it.
        Element band = (p.TwoColumn?.Rail?.Spec ?? p.TwoColumn?.CompactRail?.Spec ?? p.Head?.Spec) is { } spec
            ? K.PanoramaBand(spec.Identity, spec.Slots, lay, gutter: 0f)
            : new BoxEl { Key = Z.PanoramaKey, Height = lay.PanoramaH, Shrink = 0f, Animate = Z.PanoramaFold };

        // ── the list column: the "songs" heading on a track page, the list, and the insights sheet's layer over it (composed open or not) ──
        Element listHost = ListHost(p, ctx, dock);
        BoxEl right = Right(p, listHost) with { Margin = new Edges4(p.Head is null ? lay.ColumnGap : 0f, 0f, 0f, 0f) };

        Element arm;
        string armKey;
        if (p.Head is { } head)
        {
            // ── a page with no rail (Liked Songs): the panorama and the meta over the list ──
            armKey = "head";
            arm = Page(p, gutter, band, [
                new BoxEl
                {
                    // A short window folds the panorama and this arm has no identity column to lead with the title, so the head states it (one line,
                    // the heading voice as written: a name), over the meta.
                    Key = Z.HeadKey, Direction = 1, Shrink = 0f, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, lay.RailGap),
                    Children = lay.RailTitle
                        ? [K.NamedHeading(head.Spec.Identity.Title) with { Key = "head:title" }, new BoxEl { Key = "head:meta", Direction = 1, MinWidth = 0f, Children = [K.Meta(head.Spec.Identity, float.NaN, lay)] }]
                        : [new BoxEl { Key = "head:meta", Direction = 1, MinWidth = 0f, Children = [K.Meta(head.Spec.Identity, float.NaN, lay)] }],
                },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
                    Children = [right, AsideSlot(p, lay, dock)],
                },
            ]);
        }
        else
        {
            // ── the panorama over [identity | list | aside] ──
            var tc = p.TwoColumn!;
            Element identity = tc.Collapsed
                ? new BoxEl
                {
                    Key = Z.CompactRailKey, Direction = 1, Width = tc.CompactWidth, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
                    Enter = Z.ArmFade, Exit = Z.ArmFade, Transition = Z.ArmFadeMotion,
                    Children = [ctx.View(ViewKeys.DetailCompactRail, tc.CompactRail!)],
                }
                : new BoxEl
                {
                    // The record's width (eased across a crossing); the resist cue the core binds is kept (paint-bound).
                    Key = Z.RailKey, Direction = 1, Width = lay.RailW, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
                    Opacity = tc.RailOpacity, Animate = Z.ColumnReflow,
                    Enter = Z.ArmFade, Exit = Z.ArmFade, Transition = Z.ArmFadeMotion,
                    Children = [ctx.View(ViewKeys.DetailRail, tc.Rail! with { RailWidth = lay.RailW })],
                };
            armKey = "two-column";
            arm = Page(p, gutter, band, [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
                    Children = [identity, right, AsideSlot(p, lay, dock)],
                },
            ]);
        }

        Element root = Root(p, accent, armKey, arm);
        // The core docked the aside by the window tier; the content tier is narrower here (a docked panel's page): the aside folds and what it held
        // returns to the band under the rows.
        return p.Aside is not null && !dock ? Ctx.Provide(Detail.AsideDocked, false, root) : root;
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

    /// <summary>The two-column page: the notice, then the system page column (<see cref="ZuneFrameRules.PageColumn"/>: the gutter, capped at 2400 and
    /// centred) of the panorama band over the body rows. The column stands in a row that centres it and stretches it to the page's height (the list
    /// fills it), so it fills its cap instead of hugging its content.</summary>
    static Element Page(DetailFrameProps p, float gutter, Element band, Element[] body)
    {
        var kids = new Element[body.Length + 1];
        kids[0] = band;
        Array.Copy(body, 0, kids, 1, body.Length);
        return new BoxEl
        {
            Key = "detail:page", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                p.Notice,
                K.CentredPage(new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, DropTarget = p.Drop,
                    Children = kids,
                }, gutter) with { Grow = 1f, Shrink = 1f, MinHeight = 0f },
            ],
        };
    }

    /// <summary>The content column under the host's key (it survives a collapse, so the table reconciles in place).</summary>
    static BoxEl Right(DetailFrameProps p, Element body) => new()
    {
        Key = p.ContentKey, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = p.ContentMinWidth, MinHeight = 0f, Direction = 1,
        Children = [body],
    };

    /// <summary>The list column's body: on a track page the "songs" heading, then the list with the insights sheet's layer over it (a
    /// pass-through layer while the sheet is closed, or while the aside holds the insights: the same two children either way, so the list never
    /// re-parents).</summary>
    static Element ListHost(DetailFrameProps p, in ViewContext ctx, bool dock)
    {
        Element layer;
        if (p.Sheet is { } sheet && !dock)
        {
            var open = sheet.Toggle.Open;
            layer = new BoxEl
            {
                Key = "insights:layer", Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                HitTestVisible = Prop.Of(() => open.Value),
                Children = [ctx.View(ViewKeys.DetailInsights, sheet with { Page = new BoxEl { Grow = 1f, HitTestVisible = false } })],
            };
        }
        else
            layer = new BoxEl { Key = "insights:layer", Grow = 1f, HitTestVisible = false };

        Element host = new BoxEl
        {
            Key = "insights:host", ZStack = true, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [p.Content, layer],
        };
        // Liked Songs (the head arm) has no "songs" heading: the meta under its title leads straight into the chips and the table (Z-Liked).
        if (p.Sheet is null || p.Head is not null) return host;
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                new BoxEl
                {
                    Key = Z.SongsHeadingKey, Direction = 0, AlignItems = FlexAlign.Center, Height = Z.HeadingRowH, Shrink = 0f, MinWidth = 0f,
                    Padding = new Edges4(Z.SongsHeadingInset, 0f, Z.SongsHeadingInset, 0f),
                    Children = [K.Heading(Loc.Get(Strings.Theme.Zune.Detail.Songs))],
                },
                host,
            ],
        };
    }

    /// <summary>The aside's slot, the row's last child at every tier: width 0 where none docks, else the column gap and the record's column with its
    /// own scroller (the page's aside body under <see cref="InAside"/>, then the insights on a facts page). An aside that holds only the insights
    /// follows their toggle (0 closed). The slot may yield a few DIP inside a crossing's hold band rather than squeeze the list under its floor.</summary>
    static Element AsideSlot(DetailFrameProps p, ZuneDetailLayout lay, bool dock)
    {
        if (!dock || p.Aside is not { } aside)
            return new BoxEl { Key = Z.AsideKey, Width = 0f, Shrink = 0f, MinHeight = 0f, Animate = Z.ColumnReflow };
        var insights = p.Sheet is { Facts: not null } sheet ? sheet : null;
        var kids = new List<Element>(2);
        if (aside.Body is { } body) kids.Add(Ctx.Provide(InAside, true, body));
        // An aside that holds the insights ALONE takes the bento's own width (320 / 420 / 440); one that holds a body takes the record's column.
        float asideW = aside.Body is null && lay.InsightsW > 0f ? lay.InsightsW : lay.AsideW;
        if (insights is { } i) kids.Add(ZuneInsights.AsideSection(i.Toggle, i.Facts!, asideW));
        float slotW = lay.ColumnGap + asideW;
        Prop<float> width = slotW;
        if (aside.Body is null && insights is { } only)
        {
            var open = only.Toggle.Open;
            width = Prop.Of(() => open.Value ? slotW : 0f);
        }
        return new BoxEl
        {
            Key = Z.AsideKey, Direction = 1, Width = width, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Padding = new Edges4(lay.ColumnGap, 0f, 0f, 0f), Animate = Z.ColumnReflow,
            Children =
            [
                Ui.ScrollView(new BoxEl
                {
                    Key = Z.AsideColumnKey, Direction = 1, Gap = Z.AsideSectionGap, MinWidth = 0f,
                    Padding = new Edges4(0f, 0f, 0f, Z.SectionBottom),
                    Children = kids.ToArray(),
                }) with { Key = "aside:scroll:" + aside.RouteKey, Grow = 1f, Shrink = 1f, MinHeight = 0f },
            ],
        };
    }
}
