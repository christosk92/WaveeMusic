// ── Themes/WinUi/Structure/Views/Stage/WinUiStageRoot.cs — WinUI's `Stage.Root` (P6 W4b) ─────────────────────────────────────────────
//
// The core surface keeps every hook, effect and state machine of the fullscreen stage (the window, the focus scope, the idle machine, the layout
// signal and its classes, the key map); it hands this template a `StageRootView` each render. WinUI keeps the stack Classic draws (the layering and
// its motion are the core's recipe) and changes the chrome (W-Stage, W-Lyrics, W-Tiers):
//
//   backdrop (the sheet's fill at Compact) · hero (W4c's slot) · face · smoke (Card) · face scrim · spectrum · now-playing card · identity · artist
//   name (W4c's slot) · pane · Up next column · caption · chrome (the bar · the transport card and its lead column · the gallery · the sheet's
//   grabber) · lyric facts · hero facts · clock
//
//   · the BAR and the TRANSPORT CARD are visible at rest: they are not gated on the idle chrome (the boards keep both; the idle machine still
//     hides the cursor), so there is no hairline either.
//   · the GALLERY lives behind the options (every tier's metrics open it on request only: `GalleryOnDemand`), never on a first entry.
//   · the transport's LEAD column (the cover and the titles at Standard, the heart and the source elsewhere) is drawn by the placement over the
//     card's left column: it needs the identity's facts as well as the transport's.
//
// ZStack placement: vertical = AlignSelf, horizontal = JustifySelf. A Flow.Show boundary and a component are transparent anchors (flex columns),
// so every Show's child is a full-bleed `Stage.Layer` and the placed box sits one level down (see `Stage.Layer`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.WinUi.WinUiStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's fullscreen stage root.</summary>
internal static class WinUiStageRoot
{
    /// <summary>The registered template of <see cref="ViewKeys.StageRoot"/>: the core surface, drawn by <see cref="Draw"/>.</summary>
    public static Element Template(StageRootProps props, in ViewContext ctx) => props.Mount(Draw);

    /// <summary>The root box and the layer stack for one render of the surface. Static: its delegate is cached.</summary>
    static Element Draw(StageRootView v)
    {
        var layers = v.Layers;
        var fade = new EnterExit(Opacity: 0f, Active: true);
        return new BoxEl
        {
            Width = v.Width, Height = v.Height, ZStack = true, ClipToBounds = true, Focusable = true,
            OnRealized = v.OnRealized,
            OnHoverMove = v.OnHoverMove,
            OnPointerExit = v.OnPointerExit,
            OnPointerWheel = v.OnPointerWheel,
            OnKeyDown = v.OnKeyDown,
            OnFocusChanged = v.OnFocusChanged,
            Children =
            [
                Slots.View(ViewKeys.StageBackdrop, v.Backdrop) with { Key = "stage:backdrop" },
                Flow.Show(v.ShowHeroLayer, Stage.Layer with
                {
                    Key = "stage:hero", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageHero, v.Hero)],
                }),
                Flow.Show(v.ShowFace, new BoxEl
                {
                    Key = "stage:face", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Enter = new EnterExit(Sx: G.FaceEnterScale, Sy: G.FaceEnterScale, Opacity: 0f, Active: true),
                    Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageFace, v.Face)],
                }),
                new BoxEl
                {
                    Key = "stage:smoke", Height = Stage.Tone.SmokeH, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(1f, Shade(Stage.Tone.SmokeA))),
                    Visible = Prop.Of(v.ShowSmoke),
                },
                Slots.View(ViewKeys.StageFaceScrim, v.FaceScrim) with { Key = "stage:facescrim" },
                Slots.View(ViewKeys.StageSpectrum, v.Spectrum) with { Key = "stage:spectrum" },
                Slots.View(ViewKeys.StageNowPlayingCard, v.NowPlayingCard) with { Key = "stage:npc" },
                Slots.View(ViewKeys.StageIdentity, v.Identity) with { Key = "stage:identity" },
                Flow.Show(v.ShowArtistName, Stage.Layer with
                {
                    Key = "stage:artistname", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children = [Slots.View(ViewKeys.StageArtistName, v.ArtistName)],
                }),
                layers.Pane() with { Key = "stage:pane" },
                Flow.Show(() => layers.UpNext is not null && v.Stage.Layout.Value.UpNextShown(v.Stage.Mode.Value), UpNext(v)),
                Flow.Show(v.ShowCaption, Stage.Layer with
                {
                    Key = "stage:caption", HitTestVisible = false, HitTestPassThrough = false, RepaintBoundary = true,
                    Enter = new EnterExit(Dy: G.CaptionEnterDy, Opacity: 0f, Active: true), Exit = fade, Transition = MotionTok.ControlNormal,
                    Children = [layers.Caption()],
                }),
                Chrome(v),
                layers.LyricFacts() with { Key = "stage:facts" },
                Flow.Show(v.ShowHeroFacts, layers.HeroFacts() with { Key = "stage:herofacts" }),
                layers.Clock() with { Key = "stage:clock" },
            ],
        };
    }

    /// <summary>The chrome: the bar, the transport card (with its lead column) and the gallery. The bar and the card are not gated on the idle
    /// chrome (visible at rest). The wrapper is pass-through: empty chrome yields to the stage root's pointer handlers.</summary>
    static Element Chrome(StageRootView v)
    {
        var L = v.Stage.Layout.Value;
        bool sheet = L.Aspect == Stage.Aspect.Portrait;
        return Stage.Layer with
        {
            Key = "stage:chrome",
            Children =
            [
                Stage.Layer with
                {
                    Key = "chrome:top",
                    Children = [Embed.Comp(new WinUiStageHead.Props(v.TopBar), static () => new WinUiStageHead())],
                },
                Stage.Layer with
                {
                    Key = "chrome:transport:motion",
                    Children = [Embed.Comp(new WinUiStageTransportPlace.Props(v.Stage, v.Transport, v.Identity), static () => new WinUiStageTransportPlace())],
                },
                Flow.Show(v.ShowGallery, Stage.Layer with
                {
                    Key = "chrome:gallery",
                    Enter = GalleryMotion(sheet), Exit = GalleryMotion(sheet), Transition = MotionTok.StandardEnter,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0,
                            AlignSelf = sheet ? FlexAlign.End : FlexAlign.Start, JustifySelf = sheet ? FlexAlign.Stretch : FlexAlign.End,
                            Margin = sheet ? new Edges4(0f, 0f, 0f, L.TransportH + G.SheetGutters * Stage.Layout.Pad) : new Edges4(0f, Stage.Layout.GalleryTop, Stage.Layout.GalleryRight, L.GalleryBottomInset),
                            Layout = s_galleryFoot,
                            Children = [Slots.View(ViewKeys.StageGallery, v.Gallery)],
                        },
                    ],
                }),
            ],
        };
    }

    static EnterExit GalleryMotion(bool sheet) => new(Dx: sheet ? 0f : G.GallerySlide, Dy: sheet ? G.GallerySlide : 0f, Opacity: 0f, Active: true);

    static readonly LayoutTransition s_galleryFoot = new(TransitionChannels.Bounds, TransitionDynamics.Tween(G.GalleryYieldMs, Easing.FluentDecelerate), SizeMode.Reveal);

    static readonly EnterExit s_upNextMotion = new(Dx: G.UpNextSlide, Opacity: 0f, Active: true);

    /// <summary>The Up next column beside the lyrics at Huge (W-Lyrics-Huge): the acrylic r8 pane at the allocator's rect, the core's queue host inside.</summary>
    static Element UpNext(StageRootView v)
    {
        var r = v.Stage.Layout.Value.UpNextRect;
        var build = v.Layers.UpNext;
        return Stage.Layer with
        {
            Key = "stage:upnext",
            Enter = s_upNextMotion, Exit = s_upNextMotion, Transition = MotionTok.StandardEnter,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = r.W, Height = r.H, Margin = new Edges4(r.X, r.Y, 0f, 0f),
                    Direction = 1, Padding = Edges4.All(G.UpNextPad), Corners = Radii.CardAll, ClipToBounds = true,
                    Acrylic = Ink.Card, BorderWidth = 1f, BorderColor = Ink.Stroke,
                    Children = build is null ? [] : [build()],
                },
            ],
        };
    }

    static ColorF Shade(float a) => Ink.Veil with { A = a };
}

/// <summary>The stage's head: the top bar's slot placed at the top, the window tier's bar tall (48, 56 at Huge), and the sheet's grabber over it at
/// Compact. A component: it reads the tier.</summary>
internal sealed class WinUiStageHead : Component
{
    /// <summary>The placed bar's key (the tests find it).</summary>
    public const string HeadKey = "winui-stage-head";

    public sealed record Props(StageTopBarProps TopBar);

    // The bar's height glides across a tier edge (52 sheet header, 48, 56 at Huge): no snap on a crossing
    static readonly LayoutTransition s_fit = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(G.BarFitMs, Easing.FluentDecelerate), SizeMode.Relayout);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var t = WinUiStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
        var bar = new BoxEl
        {
            Key = HeadKey, Height = t.BarH, Layout = s_fit, Shrink = 0f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, Direction = 0,
            Children = [Slots.View(ViewKeys.StageTopBar, p.TopBar)],
        };
        // one shape at every tier (a full-bleed layer: the bar placed at the top, the grabber over it in the sheet), so a crossing re-mounts nothing
        return Stage.Layer with
        {
            Children = t.Sheet
                ? [bar, new BoxEl
                  {
                      Key = "winui-stage-grabber", Width = G.GrabberW, Height = G.GrabberH, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Center, Margin = new Edges4(0f, G.GrabberTop, 0f, 0f),
                      Corners = Radii.Circle(G.GrabberH), Fill = Ink.Ink with { A = G.GrabberAlpha }, HitTestVisible = false,
                  }]
                : [bar],
        };
    }
}

/// <summary>Where the transport card stands (the bottom edge, at the tier's gutter, a gutter short of a docked gallery that runs to the foot) and its
/// lead column drawn over the card's left column. A component, so a gallery toggle re-renders the placement and not the stage's root.</summary>
internal sealed class WinUiStageTransportPlace : Component
{
    public sealed record Props(Stage.StageCtx Stage, StageTransportProps Transport, StageIdentityProps Identity);

    static readonly LayoutTransition s_yield = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(G.GalleryYieldMs, Easing.FluentDecelerate), SizeMode.Relayout);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var sc = p.Stage;
        var L = sc.Layout.Value;
        var t = WinUiStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
        bool gallery = sc.Mode.Value == Stage.Mode.Visualizer && sc.GalleryShown.Value;
        var kids = new List<Element>(2) { Slots.View(ViewKeys.StageTransport, p.Transport) };
        if (t.Form != WinUiTransportForm.Stack)
        {
            var (_, side, _) = WinUiStageTransport.Columns(in L, t, gallery);
            kids.Add(new BoxEl
            {
                Key = "transport:lead:place", Width = side, Height = L.TransportH - t.TransportPadding.Top - t.TransportPadding.Bottom,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(t.TransportPadding.Left, t.TransportPadding.Top, 0f, 0f),
                Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true, HitTestPassThrough = true,
                Children = [Embed.Comp(new WinUiStageTransportLead.Props(sc, p.Transport, p.Identity, t), static () => new WinUiStageTransportLead())],
            });
        }
        return Stage.Layer with
        {
            Key = "chrome:transport",
            Children =
            [
                new BoxEl
                {
                    Key = "stage:transport:card", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, ZStack = true, Height = L.TransportH,
                    Margin = new Edges4(L.TransportLeft, 0f, L.TransportRight(gallery), L.TransportInset),
                    Layout = s_yield,
                    Children = kids.ToArray(),
                },
            ],
        };
    }
}

/// <summary>The transport card's lead column, by the tier's form: Standard's identity (the 80 / 56 cover, the title, the artists, "Playing from", the
/// chips in Visualizer mode); Small's heart and one word ("Synced lyrics", or the translation toggle in Lyrics mode); Huge's heart, "Playing from"
/// and the chips. A component: it reads the track, the facts and the chips inside its own render.</summary>
internal sealed class WinUiStageTransportLead : Component
{
    public sealed record Props(Stage.StageCtx Stage, StageTransportProps Transport, StageIdentityProps Identity, WinUiStageLayout Tier);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var sc = p.Stage;
        var t = p.Tier;
        var f = p.Transport.Facts();
        var set = sc.Accent.Value;
        var mode = sc.Mode.Value;
        bool viz = mode == Stage.Mode.Visualizer;
        switch (t.Form)
        {
            case WinUiTransportForm.HeartLead:
            {
                Element word;
                if (mode == Stage.Mode.Lyrics && sc.HasSecondLine())
                {
                    bool on = sc.SecondLineOn();
                    word = ToolTip.Wrap(new BoxEl
                    {
                        Key = "transport:translate", Width = t.ButtonBox, Height = t.ButtonBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                        Fill = on ? Ink.GlassHover : ColorF.Transparent, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = sc.ToggleSecondLine,
                        Children = [new TextEl(Icons.LocaleLanguage) { Size = t.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink }],
                    }, Loc.Get(Strings.Theme.Winui.Stage.Romanization));
                }
                else
                    word = new TextEl(sc.HasTimedLyrics.Value ? Loc.Get(Strings.Stage.SyncedLyrics) : f.PlayingFrom)
                    { Size = G.LeadMeta, LineHeight = G.LeadMetaLine, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };
                return Row(G.BarButtonGap, WinUiStageTransport.Heart(f.Saved, f.Like, set.Fill, t), word);
            }
            case WinUiTransportForm.HeartSource:
            {
                var col = new List<Element>(2) { Source(f, G.SourceBig, G.SourceBigLine, set) };
                if (t.ChipH > 0f) col.Add(Chips(p.Identity, t));
                return Row(G.LeadGap, WinUiStageTransport.Heart(f.Saved, f.Like, set.Fill, t),
                    new BoxEl { Direction = 1, Gap = G.SourceGap, MinWidth = 0f, Shrink = 1f, Children = col.ToArray() });
            }
            default:
            {
                var track = sc.RowValue();
                string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
                float art = viz ? t.LeadArtVisualizer : t.LeadArtOther;
                var meta = p.Identity.Meta(0);
                var col = new List<Element>(4)
                {
                    new TextEl(p.Identity.Title())
                    {
                        Size = viz ? G.LeadTitleBig : G.LeadTitle, LineHeight = viz ? G.LeadTitleBigLine : G.LeadTitleLine, Weight = G.LeadTitleWeight,
                        FontFamily = viz ? WinUiStageLayerGeometry.DisplayFace : null, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    new TextEl(meta.Line) { Size = viz ? G.LeadMetaBig : G.LeadMeta, LineHeight = viz ? G.LeadMetaBigLine : G.LeadMetaLine, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    Source(f, G.LeadMeta, G.LeadMetaLine, set),
                };
                if (viz && t.ChipH > 0f) col.Add(Chips(p.Identity, t));
                return Row(G.LeadGap,
                    new BoxEl
                    {
                        Key = "transport:lead:art", Width = art, Height = art, Shrink = 0f, Corners = CornerRadius4.All(G.LeadArtCorners), ClipToBounds = true, HitTestVisible = false,
                        Children = [Controls.Artwork(url.Length > 0 ? url : null, art, art, G.LeadArtCorners, decodePx: 256)],
                    },
                    new BoxEl { Direction = 1, Gap = G.LeadLineGap, MinWidth = 0f, Shrink = 1f, Grow = 1f, Children = col.ToArray() });
            }
        }
    }

    static BoxEl Row(float gap, Element a, Element b) => new()
    {
        Key = "transport:lead", Direction = 0, AlignItems = FlexAlign.Center, Gap = gap, MinWidth = 0f, Grow = 1f, HitTestPassThrough = true,
        Children = [a, b],
    };

    /// <summary>"Playing from" and the source's name in the accent text (W-Stage: `Playing from <a>The Stereo Love Show</a>`).</summary>
    static Element Source(StageTransportFacts f, float size, float line, AccentSet set)
    {
        if (f.PlayingFrom.Length == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
        return new TextEl(f.PlayingFrom) { Size = size, LineHeight = line, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
    }

    /// <summary>The chips (the format, "Synced lyrics", BPM) on the subtle plate, 20 / 24 tall.</summary>
    static Element Chips(StageIdentityProps identity, WinUiStageLayout t)
    {
        var chips = identity.Chips();
        var kids = new Element[chips.Count];
        for (int i = 0; i < kids.Length; i++)
            kids[i] = new BoxEl
            {
                Height = t.ChipH, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(t.ChipPadX, 0f, t.ChipPadX, 0f), Shrink = 0f,
                Corners = Radii.ControlAll, Fill = Ink.GlassHover,
                Children = [new TextEl(chips[i].Text) { Size = G.ChipText, LineHeight = G.ChipLine, Color = Ink.InkSecondary, MaxLines = 1 }],
            };
        return new BoxEl { Key = "transport:chips", Direction = 0, Gap = G.LeadChipGap, Margin = new Edges4(0f, G.LeadChipsTop, 0f, 0f), HitTestVisible = false, Children = kids };
    }
}
