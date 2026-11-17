// ── Themes/Zune/Structure/Views/Home/ZuneHomeZones.Spotlight.cs — Zune's Release Spotlight ("from artists you follow") ──────────────────────
//
// The concept's Zune skin, in Zune's own language (the Spine Shelf's flat colour block, the album page's panorama title, the discovery kit's
// captions and rings): the whole card is a flat block of the cover's own colour (made legible for the theme's ink: the hue stays), the cover
// flat and square from edge to edge on its left, the canvas edge to edge on its right; between them the eyebrow and the meta in the label
// case, the title huge and light in the heading case on one line that bleeds off the words' edge under a fade (display type, never shrunk), an
// EP's songs as light lowercase lines, and the rings (play, save, sound). The heading carries the page counter ("01/03") while it pages.
// Mechanics, preview and motion are core (Home/ReleaseSpotlight); numbers are ZuneSpotlightRules.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using S = Wavee.Themes.Zune.ZuneSpotlightRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneHomeZones
{
    /// <summary>"from artists you follow" as the Release Spotlight: the heading (with the page counter while it pages) over the card. The section
    /// owns its pager.</summary>
    static Element Spotlight(Zone zone, ZuneHomeLayout lay)
        => Embed.Comp(new SpotlightProps(zone, lay), static () => new SpotlightSection()) with { SkeletonProxy = () => SpotlightTree(zone, lay, null) };

    sealed record SpotlightProps(Zone Zone, ZuneHomeLayout Layout);

    /// <summary>Owns the section's ONE pager controller (stable for its life) and registers it for the inspector.</summary>
    sealed class SpotlightSection : Component
    {
        readonly ShelfController _controller = new();

        public override Element Render()
        {
            var p = UseProps<SpotlightProps>();
            string key = p.Zone.Key;
            UseEffect(() => GridFill.Register(key, _controller));
            return SpotlightTree(p.Zone, p.Layout, _controller);
        }
    }

    static Element SpotlightTree(Zone zone, ZuneHomeLayout lay, ShelfController? controller)
    {
        var body = ReleaseSpotlightView.Of(zone, S.Of(lay.Tier, lay), s_spotlight, controller, Icons.ChevronLeft, Icons.ChevronRight);
        return K.Section(ClustersHeading(zone, lay, ReleaseSpotlightView.Pages(zone) ? controller : null), body);
    }

    static readonly ReleaseSpotlightSkin s_spotlight = new(SpotFrame, SpotCover, SpotCopy, SpotTrackRow, SpotNextCover,
        static () => Tok.FillSubtleSecondary, static () => Tok.AccentDefault, ZuneMotion.SwapFade, ZuneMotion.SwapThroughMotion, ZuneMotion.RowsResize);

    static ZuneSpotlightTier SpotTier(ReleaseSpotlightGeometry g) => (ZuneSpotlightTier)g.Tier!;

    // ── the card: a flat block of the cover's colour ──

    /// <summary>The cover's own colour, a shade deeper or lighter where the theme's ink would not reach AA on it: the hue stays, every word and
    /// ring reads. Every word on the block is in that one ink (the primary): a secondary grey on a coloured block fell to 1.9 : 1.</summary>
    static ColorF BlockColour(SpotlightItem item)
    {
        var ink = Tok.TextPrimary;
        if (item.Accent == 0u) return Tok.FillSolidBase;
        return ColorContrast.EnsureContrast(ColorScience.ToColor(item.Accent), ink);
    }

    static Element SpotFrame(SpotlightItem item, ReleaseSpotlightGeometry g, Element content) => new BoxEl
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, ClipToBounds = true, Fill = BlockColour(item), AlignItems = FlexAlign.Stretch,
        Children = [content],
    };

    static Element SpotCover(SpotlightItem item, ReleaseSpotlightGeometry g)
    {
        var c = item.Card;
        return new BoxEl
        {
            ZStack = true, Grow = 1f, MinWidth = 0f, ClipToBounds = true, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink,
            OnClick = item.IsBlank ? null : () => HomeCardNav.Open(in c),
            Children = [Controls.CoverFill(item.Cover, 0f, g.Narrow ? g.NarrowCoverH * 2f : g.Cover * 2f)],
        };
    }

    // ── the words ──

    static Element SpotCopy(SpotlightItem item, SpotlightFacts facts, ReleaseSpotlightGeometry g, Element? tracks)
    {
        var t = SpotTier(g);
        var lay = t.Lay;
        var c = item.Card;
        // The eyebrow: the lead words, the artist (a link), the tail words, in the label case.
        string uri = facts.ArtistUri, name = facts.Artist;
        var eyebrow = new List<Element>(3);
        if (facts.Lead.Length > 0) eyebrow.Add(K.Caption(facts.Lead.TrimEnd()) with { Color = Tok.TextPrimary, Shrink = 0f, Trim = TextTrim.None });
        eyebrow.Add(new BoxEl
        {
            Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = uri.Length > 0,
            OnClick = uri.Length > 0 ? () => HomeVerbs.OpenEntity(uri, name) : null,
            Children = [K.Name(name) with { HoverColor = Tok.TextPrimary, Shrink = 1f }],
        });
        if (facts.Tail.Length > 0) eyebrow.Add(K.Caption(facts.Tail.TrimStart()) with { Color = Tok.TextPrimary, Shrink = 0f, Trim = TextTrim.None });

        // The title: one light line that bleeds under the fade at the words' edge, never shrunk, never trimmed.
        var title = new BoxEl
        {
            Direction = 0, MinWidth = 0f, ClipToBounds = true, EdgeFade = new EdgeFadeSpec(EdgeMask.Right, S.TitleFade),
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = !item.IsBlank,
            OnClick = item.IsBlank ? null : () => HomeCardNav.Open(in c),
            Children = [R.Type.Hero(item.Title) with
            {
                Size = t.TitleSize, LineHeight = t.TitleSize, Case = R.Case.For(LabelKind.Heading),
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.None, Shrink = 0f,
            }],
        };
        var kids = new List<Element>(5)
        {
            new BoxEl { Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = eyebrow.ToArray() },
            title,
            K.Caption(facts.Meta) with { Color = Tok.TextPrimary },
        };
        if (tracks is { } list) kids.Add(new BoxEl { Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, Spacing.XS, 0f, 0f), Children = [list] });
        string key = item.Key, titleText = item.Title;
        kids.Add(new BoxEl
        {
            Direction = 0, Gap = lay.RingsGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f, Margin = new Edges4(0f, Spacing.S, 0f, 0f),
            Children =
            [
                K.Ring("zune-spot-play:" + key, Icons.Play, Loc.Get(Strings.Home.Play), showLabel: false, primary: true,
                       () => HomeCardNav.Play(in c), lay.RingSize, lay.RingTarget),
                item.IsBlank
                    ? new BoxEl { Width = lay.RingSize, Height = lay.RingSize, Shrink = 0f }
                    : ZuneHomeDaylist.SaveRing(key, titleText, lay with { RingLabels = false }, "zune-spot-save:" + key),
                item.SongCount == 0 ? new BoxEl { Visible = false } : Embed.Comp(new ShelfSoundProps(lay), static () => new ShelfSound()) with { Key = "zune-spot-sound:" + key },
            ],
        });
        return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>A listed song: its number and its title in the light lowercase heading voice, the duration at the end in the caption voice.</summary>
    static Element SpotTrackRow(ShelfSong song, int index, string duration, ReleaseSpotlightGeometry g) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f, HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = S.NumberW, Shrink = 0f, Direction = 0,
                Children = [K.Caption((index + 1).ToString(CultureInfo.InvariantCulture), cased: false) with { Color = Tok.TextPrimary }],
            },
            R.Type.Heading(song.IsBlank ? SpotlightModel.Hold(SpotlightModel.SongHold) : song.Title) with
            {
                Size = S.TrackTitleSize, LineHeight = S.TrackTitleLine, Case = R.Case.For(LabelKind.Heading),
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f, Grow = 1f,
            },
            K.Caption(duration.Length > 0 ? duration : " ", cased: false) with { Color = Tok.TextPrimary, Shrink = 0f },
        ],
    };

    static Element SpotNextCover(SpotlightItem item, ReleaseSpotlightGeometry g)
        => Controls.Artwork(item.Cover, g.NextCover, g.NextCover, 0f);
}
