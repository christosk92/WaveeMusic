// ── Themes/Zune/Structure/Views/Artist/ZuneArtistHero.cs — Zune's artist hero (themes plan 6.3; P5 Z6a2) ──────────────────────────────
//
// Board Z-Artist (and -Compact / -Small / -Huge for the tier records). The hero is a PANORAMA TITLE over the cover's blend wash: no photograph of
// its own (the artist's header is the material's bleed behind the band, published through the page chrome by the core host), no veil, no
// collapse (the band is the frame's). From the top: the "verified artist" eyebrow, the flexible air, the name (the panorama voice, 120/130 at the
// light 200; the 64 compact voice below 640; 150 at Huge) standing `NameNudge` left of the gutter so its glyph edge lines up with the copy, the
// caption ("#3 in the world" in the accent · monthly listeners · followers) and, at Huge, the secondary row.
//
// THE HEIGHT HUGS. The record's height is a floor (`MinHeight`), not a clip: the copy is a flow (eyebrow, the spring, name, caption, the secondary
// row), so a long caption that wraps pushes the body down instead of running under it. The copy stands in the system page column (audit A1), so
// the eyebrow, the name and the caption share the band's and the body's x at every width; the wash behind them stays full bleed.
//
// THE NAME. The `panoramaFit` option decides how it meets the window edge: Bleed lets it run off the hero's edge (the hero clips it), Fit trims it
// with an ellipsis. It is ONE text node whose trim is a live prop, never two keyed arms. A name keeps its own casing (never lowercased); the
// eyebrow and the caption's words are labels, lowercased by the case role.
//
// THE ACTIONS: one home per verb (audit A4). From Small up play, shuffle and follow are the band's row 2 (the core host publishes them); the hero
// keeps only what the band does not carry, radio and "...", as unlabelled 36 rings (`ZuneArtistLayout.Secondary`: at its bottom right at Small, a
// row under the caption at Huge). Compact has no band actions: all of them stand in a 40 row under the hero (`ZuneArtistPage` places that one).
// The follow pill is a 2-dip outline (the accent while following).
//
// The hero is built for the live page AND for the derived shimmer (a null `p` is the skeleton arm: the actions are never skeleton content).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using FluentGpu.Signals;
using Rules = Wavee.Themes.Zune.ZuneArtistRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's artist hero and its action parts.</summary>
internal static class ZuneArtistHero
{
    /// <summary>The hero's verbs (all null on the skeleton arm).</summary>
    internal readonly record struct Acts(Action? Play, Action? Shuffle, Action? Radio, Func<ContextMenuModel?>? Menu);

    /// <summary>The hero (<paramref name="nameKnown"/>: the skeleton arm draws the real name): eyebrow, name, caption and (Huge) the secondary row in the page column, at least the record's height; the Small corner rings
    /// ride a layer over it.</summary>
    public static Element Build(in Artist.HeroText text, string uri, ZuneArtistLayout lay, ColorF accent, in Acts acts, bool nameKnown = false)
    {
        float gutter = lay.Gutter;
        bool shimmer = acts.Play is null;

        BoxEl eyebrow = text.Verified
            ? new BoxEl { Direction = 1, MinWidth = 0f, Children = [Lines(Run(Loc.Get(Strings.Artist.Verified), Tok.TextSecondary), shimmer)] }
            : new BoxEl();
        eyebrow = eyebrow with { Key = Rules.EyebrowKey };

        TextEl name = lay.CompactName ? R.Type.PanoramaCompact(text.Name) : R.Type.Panorama(text.Name);
        if (lay.NameSize > 0f) name = name with { Size = lay.NameSize, LineHeight = lay.NameLine, CharSpacing = lay.NameTracking };
        // The name keeps its tier's step (a short window included) and WRAPS by whole words over up to NameLines lines; the hero grows to hug it, so a
        // long name takes more room instead of a smaller size or a one-line ellipsis.
        name = name with
        {
            Color = Tok.TextPrimary, MaxLines = Rules.NameLines, Wrap = TextWrap.WrapWholeWords, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis,
        };
        // The name stands NameNudge left of the column's gutter, so the light face's glyph edge lines up with the copy under it. A tier step (a docked
        // panel narrowing the content, a crossing) changes its size: the SLOT's height eases with the name riding its foot, and the name itself hugs
        // its words and scales from its bottom-left, so it never leaves the caption and nothing around it jumps.
        Element nameBox = new BoxEl
        {
            Key = Rules.NameKey, Direction = 1, MinWidth = 0f, Margin = new Edges4(-Rules.NameNudge, 0f, 0f, Rules.NameCaptionGap),
            Layout = ZuneMotion.TitleSlot,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Start, Animate = ZuneMotion.TitleScale,
                    TransformOriginX = ZuneMotion.TitleOriginX, TransformOriginY = ZuneMotion.TitleOriginY,
                    Children = [Lines(name, shimmer)],
                },
            ],
        };
        // Z-State-Loading: a name already known (the search row, a link) stands in its real display type on the skeleton, so it never changes under the reveal.
        if (shimmer && nameKnown) nameBox = nameBox.Skel(nameBox);   // the override is the real name, drawn as is (Skeletonized(false) would blank it)

        // The air above the eyebrow and under the caption are spacers, not padding: they ease (padding cannot), so a tier step moves no copy.
        // The spring is empty space, never a shimmer block (the deriver would paint a page-wide bar there while the page loads).
        var kids = new List<Element>(7)
        {
            ZuneMotion.Air(Rules.AirPrefix + "top", lay.EyebrowTop), eyebrow, (new BoxEl { Grow = 1f, MinHeight = 0f }).Skeletonized(false), nameBox, Caption(in text, accent, shimmer),
        };
        // Huge's secondary row stands in a slot that is ALWAYS mounted: its room (the gap above the rings and the rings) opens and closes as an
        // easing height while the row fades, so the name and the caption over it glide when Huge comes or goes (two docked panes take the content
        // to Standard). The rings fade inside it.
        // A Short window folds the band's row 2 at rest: play, shuffle and follow open the same slot (before the secondary rings where the tier has them),
        // so the follow state stays visible at rest and nothing that was there remounts.
        bool primary = lay.Actions == ZuneArtistActions.Hero && !shimmer;
        bool secondaryRow = lay.Secondary == ZuneArtistSecondary.Row && !shimmer;
        bool row = primary || secondaryRow;
        var rowKids = new List<Element>(2);
        if (primary) rowKids.Add(Actions(text.Name, uri, lay, acts, labels: false, radio: false, more: false) with { Key = Rules.ActionsKey + ":primary" });
        if (secondaryRow) rowKids.Add(Secondary(uri, lay, acts));
        kids.Add(new BoxEl
        {
            Key = Rules.ActionsKey + ":row", Direction = 1, MinWidth = 0f, MinHeight = 0f, Shrink = 0f, ClipToBounds = true,
            Height = row ? Rules.ActionsTop + lay.Target : 0f, Layout = ZuneMotion.HeightResize,
            Children = row
                ?
                [
                    new BoxEl
                    {
                        Direction = 0, MinWidth = 0f, Gap = Rules.ActionsGap, AlignItems = FlexAlign.Center, Margin = new Edges4(0f, Rules.ActionsTop, 0f, 0f),
                        Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
                        Children = rowKids.ToArray(),
                    },
                ]
                : [],
        }.Skeletonized(false));
        kids.Add(ZuneMotion.Air(Rules.AirPrefix + "foot", lay.CaptionBottom));

        // The copy in the page column (the band's x); it fills the hero's height, so the spring parks the name and the caption at its foot.
        Element column = ZuneDetailKit.CentredPage(new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children = kids.ToArray(),
        }, gutter);

        // The record's height is a FLOOR strut under the copy (the ZStack is as tall as its tallest layer), not the hero's MinHeight: a MinHeight
        // would clamp the eased height on a grow (Small 140 to Standard 340 when a panel undocks), the strut eases both ways.
        var layers = new List<Element>(3) { ZuneMotion.Air(Rules.AirPrefix + "floor", lay.HeroH), column };
        if (lay.Secondary == ZuneArtistSecondary.Corner && !shimmer)
            layers.Add(new BoxEl
            {
                Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End, HitTestPassThrough = true,
                Padding = new Edges4(0f, 0f, gutter, Rules.CornerInsetY),
                Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
                Children = [Secondary(uri, lay, acts)],
            });

        // A floor, not a Height: the copy's flow can exceed the record's height (a wrapping name or caption pushes the body down). A tier step
        // (Standard 340 to Small 140 when a panel docks) eases the floor through real layout, so the copy at the foot and the body under the hero
        // ride it instead of jumping.
        return new BoxEl
        {
            Key = Rules.HeroKey, Shrink = 0f, MinWidth = 0f, ZStack = true, ClipToBounds = true, Children = layers.ToArray(),
        };
    }

    /// <summary>The Compact row under the hero: play · shuffle · follow, the more ring at the end (40 targets).</summary>
    public static Element BelowRow(string name, string uri, ZuneArtistLayout lay, in Acts acts)
        => new BoxEl
        {
            Key = Rules.ActionsKey + ":below", Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(lay.Gutter, Rules.BelowRowTop, lay.Gutter, 0f),
            Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
            Children = [Actions(name, uri, lay, acts, labels: false, radio: false, spread: true)],
        };

    /// <summary>"#N in the world" (accent) · monthly listeners · followers; a zero stat drops its run.</summary>
    static Element Caption(in Artist.HeroText text, ColorF accent, bool shimmer)
    {
        int count = (text.Rank > 0 ? 1 : 0) + (text.Monthly > 0 ? 1 : 0) + (text.Followers > 0 ? 1 : 0);
        var runs = new Element[count];
        int k = 0;
        if (text.Rank > 0)
            runs[k++] = Lines(Run(Strings.Artist.WorldRank(text.Rank.ToString(System.Globalization.CultureInfo.CurrentCulture)), accent), shimmer);
        if (text.Monthly > 0)
            runs[k++] = Lines(Run(Detail.Text.CompactCount(text.Monthly) + " " + Loc.Get(Strings.Artist.MetaMonthly), Tok.TextSecondary), shimmer);
        if (text.Followers > 0)
            runs[k++] = Lines(Run(Detail.Text.CompactCount(text.Followers) + " " + Loc.Get(Strings.Artist.MetaFollowers), Tok.TextSecondary), shimmer);
        return new BoxEl
        {
            Key = Rules.CaptionKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.HeadInlineGap, MinWidth = 0f, Wrap = true,
            Children = runs,
        };
    }

    static TextEl Run(string text, Prop<ColorF> color) => R.Type.DenseMeta(text) with
    {
        Color = color, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f,
    };

    /// <summary>A text run as the loaded hero lays it out, for the skeleton arm: one line box of the run's own line height, so the deriver's
    /// bar stands where the words will. The live arm is the run itself.</summary>
    static Element Lines(TextEl run, bool shimmer)
    {
        if (!shimmer || !(run.LineHeight > 0f)) return run;
        return new BoxEl
        {
            Height = run.LineHeight, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [run with { MaxLines = 1, Wrap = TextWrap.NoWrap }],
        };
    }

    // ══ the actions ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The 36 ring (the 40 target at Compact) with its lowercase word beside it where <paramref name="showLabel"/>.</summary>
    static Element Ring(string key, string glyph, string label, bool showLabel, bool primary, Action? onClick, float target)
    {
        // The clickable node is the kit's ring box: its hit edge is the tier's target (40 at Compact, the ring's own 36 elsewhere), the disc centred in it.
        Element ring = K.Ring(key, glyph, label, showLabel, primary, onClick ?? s_noop, target: target);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinHeight = target, MinWidth = showLabel ? 0f : target,
            Shrink = 0f, Children = [ring],
        };
    }

    static readonly Action s_noop = static () => { };

    /// <summary>The verbs the band does not carry: radio and "...", unlabelled rings (Small's corner, Huge's row under the caption).</summary>
    static Element Secondary(string uri, ZuneArtistLayout lay, in Acts acts)
    {
        var kids = new List<Element>(2) { Ring("radio", Icons.RadioTower, Loc.Get(Strings.Artist.ArtistRadio), false, primary: false, acts.Radio, lay.Target) };
        if (acts.Menu is { } menu) kids.Add(Embed.Comp(new MoreProps(lay.Target, menu), static () => new MoreRing()) with { Key = "zune-artist-more:" + uri });
        return new BoxEl
        {
            Key = Rules.ActionsKey + ":secondary", Direction = 0, Gap = Rules.ActionsGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = kids.ToArray(),
        }.Skeletonized(false);
    }

    /// <summary>Play · shuffle · follow (· radio) · more: the Compact row's, where the band carries no actions. <paramref name="spread"/> parks the more
    /// ring at the end of the row.</summary>
    static Element Actions(string name, string uri, ZuneArtistLayout lay, in Acts acts, bool labels, bool radio, bool spread = false, bool more = true)
    {
        float target = lay.Target;
        var kids = new List<Element>(7)
        {
            Ring("play", Icons.Play, Loc.Get(Strings.Artist.Play), labels, primary: true, acts.Play, target),
            Ring("shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), labels, primary: false, acts.Shuffle, target),
            Embed.Comp(() => new FollowPill { Uri = uri, Name = name, Height = target }) with { Key = "zune-artist-follow:" + uri },
        };
        if (radio) kids.Add(Ring("radio", Icons.RadioTower, Loc.Get(Strings.Artist.ArtistRadio), labels, primary: false, acts.Radio, target));
        if (spread) kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        if (more && acts.Menu is { } menu) kids.Add(Embed.Comp(new MoreProps(target, menu), static () => new MoreRing()) with { Key = "zune-artist-more:" + uri });
        return new BoxEl
        {
            Key = Rules.ActionsKey, Direction = 0, Gap = Rules.ActionsGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Grow = spread ? 1f : 0f, Children = kids.ToArray(),
        }.Skeletonized(false);
    }

    sealed record MoreProps(float Size, Func<ContextMenuModel?> Menu)
    {
        public bool Equals(MoreProps? o) => o is not null && Size == o.Size;
        public override int GetHashCode() => Size.GetHashCode();
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

    /// <summary>The more ring: the artist's menu, built at open from the newest factory.</summary>
    sealed class MoreRing : Component, IPropsHost
    {
        MoreProps? _latest;
        readonly Signal<MoreProps?> _props = new(null);
        readonly Func<ContextMenuModel?> _menu;

        public MoreRing() => _menu = () => _latest?.Menu();

        public void ApplyProps(object props)
        {
            _latest = (MoreProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            var overlay = UseContext(Overlay.Service);
            BoxEl ring = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinHeight = p.Size, MinWidth = p.Size, Shrink = 0f,
                Children = [K.RingDisc(Icons.More, primary: false)],
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, BlocksDragArm = true,
            };
            if (!Controls.IsNullOverlay(overlay)) ring = ContextSheets.Attach(ring, overlay, _menu, s_moreMenu);
            return ToolTip.Wrap(ring, Loc.Get(Strings.Common.More));
        }
    }

    /// <summary>The follow pill: a 2-dip outline, the word in the same ink (lowercase), the accent while following. Keyed on the uri by its caller
    /// (the uri freezes at mount).</summary>
    internal sealed class FollowPill : Component
    {
        public required string Uri { get; init; }
        public string? Name { get; init; }
        public float Height { get; init; }

        public override Element Render()
        {
            var lib = Controls.Library;
            if (lib is null) return new BoxEl();
            bool on = lib.IsSaved(Uri);
            string uri = Uri;
            string? name = Name;
            Prop<ColorF> ink = on ? Prop.Of(static () => R.Ink.AccentDecor) : Tok.TextPrimary;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f,
                Height = Height, Padding = new Edges4(Rules.PillPadX, 0f, Rules.PillPadX, 0f), Corners = CornerRadius4.All(Radii.Full),
                BorderWidth = Rules.PillBorder, BorderColor = ink,
                HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, BlocksDragArm = true,
                OnClick = () => lib.ToggleSaved(uri, name),
                Children =
                [
                    R.Type.TrackTitle(Loc.Get(on ? Strings.Artist.Following : Strings.Artist.Follow)) with
                    {
                        Color = ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                ],
            };
        }
    }
}
