// ── Themes/Classic/Structure/Views/Panel/ClassicPanel.cs — Classic's `Shell.Panel` (themes plan 6.1, T6b) ─────────────────────
//
// From `Shell/Rail.UI.cs`: the right rail's frame (docked and floating), its header, the docked video cap's body and the arm of
// each mode. The core keeps what decides (`Shell.PanelHost`: open, mode, committed width, floating or inline; the width and fit
// rules; the rail <-> docked-video coupling; the seam's splitter, which the FRAME places) and hands this template one decided
// `PanelView` per render (`PanelProps.Read`).
//
// THE RAIL (ch 21 §1-§6). One panel, two mutually exclusive ARMS (never one tree with a flag):
//
//   NowPlaying arm   surface · column[ PinnedHero (the docked cap | the hero tile + Art|Video toggle), NPV body ] · edge
//   every other arm  surface · column[ DockedCap (mounts UNCONDITIONALLY; the card's own gate hides it), header, body ]
//
// ONE coat per rung (§4.1): docked the surface is Transparent and the shell's reservation band paints FileArea; closing
// takes the coat back for the 300 ms slide-out (the Fill bind includes the open flag); floating paints its own FileArea, a
// uniform ring and Elevation.Flyout. Open/close is ONE TranslateX track on the host node, never an opacity track.
//
// The frame is a Component (as `Rail.FrameCore` was): the slide is a layout effect on the component's rendered root, its
// seeded-once flag is a field, and the arms are the same elements in the same positions, so a mode switch reconciles exactly as
// it did. The lyrics body and the queue body are core builders on the props (T7b and T7d's bodies are core hosts that mount their views); the video body is `ClassicVideoPanelBody` (T7c); the friends
// body, the hero and the lyrics header (`Panel.LyricsHeader`, `ClassicLyricsHeader`, P3) are keys of their own.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's right panel.</summary>
internal static class ClassicPanel
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellPanel"/>.</summary>
    public static Element Template(PanelProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicPanelFrame());

    internal static CornerRadius4 RailCorners => new(Radii.Card, 0f, 0f, 0f);

    // ── the arm ──────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static BoxEl Arm(bool open, bool floating, Element surface, Element edge, params Element[] column) => new()
    {
        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Corners = RailCorners,
        HitTestVisible = open,                                            // a closed rail is laid out, off-clip, unhittable
        BorderColor = floating ? Tok.StrokeCardDefault : ColorF.Transparent,
        BorderWidth = floating ? 1f : 0f,
        Shadow = floating ? Elevation.Flyout : (ShadowSpec?)null,         // NO shadow docked
        Children = [surface, new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = column }, edge],
    };

    internal static BoxEl GrowSlot(Element body) => new() { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] };

    // ── the bodies ───────────────────────────────────────────────────────────────────────────────────────────────────

    internal static Element Body(PanelProps props, PanelMode body) => body switch
    {
        PanelMode.Lyrics => props.LyricsBody(),
        PanelMode.Queue => props.QueueBody(),
        PanelMode.Friends => Slots.View(ViewKeys.PanelFriends, props.Friends),
        PanelMode.Video => Embed.Comp(props, static () => new ClassicVideoPanelBody()),
        _ => Embed.Comp(props.NowPlaying, static () => new ClassicNowPlayingBody()),
    };

    // ── the docked cap (the card is the video lane's; this is the height, the letterbox and the splitter) ─────────────

    /// <summary>The rail's ONE docked card, reached from both arms. Its Height is the SAME FloatSignal the splitter
    /// writes — a stable bind, never a fresh thunk (that left the height NaN and collapsed the ZStack to the strip).</summary>
    internal static Element DockedCap(PanelProps props, bool docked, float railWidth)
    {
        Element video = props.DockedCard();
        if (!docked) return video;                                         // the card's own gate: zero size, no reflow
        return new BoxEl
        {
            Direction = 1, ZStack = true, Shrink = 0f, ClipToBounds = true,
            Height = props.DockedVideoHeight, Fill = Tok.MediaLetterbox,
            Children =
            [
                video,
                new BoxEl
                {
                    Grow = 1f, Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true,
                    Children =
                    [
                        Splitter.Create(props.DockedVideoHeight, props.CommitDockedVideo, new Splitter.SplitterOptions
                        {
                            Min = props.DockedVideoMin(railWidth),
                            Max = props.DockedVideoMax(),
                            Axis = SplitterAxis.Vertical, ShowIndicator = false,
                        }),
                    ],
                },
            ],
        };
    }

    /// <summary>The NowPlaying arm's pinned slot — ONE slot, two possible occupants, never both.</summary>
    internal static Element PinnedHero(PanelProps props, PanelView view)
    {
        Element art = Slots.View(ViewKeys.NowPlayingHero, props.NowPlaying);
        return view.Pinned switch
        {
            PanelPinned.DockedVideo => DockedCap(props, true, view.Width),
            PanelPinned.ArtWithVideoToggle => new BoxEl { ZStack = true, Shrink = 0f, Children = [art, ArtVideoToggle(props)] },
            _ => art,                                                      // the page stage owns the one surface, or nothing to dock
        };
    }

    /// <summary>The 2-state Art|Video toggle over the ART's top-right corner. Its offset is DERIVED from
    /// <see cref="Rail.Hero.ArtTop"/>; each half's tooltip names the action IT performs.</summary>
    static Element ArtVideoToggle(PanelProps props) => new BoxEl
    {
        Height = ClassicPanelLayout.ToggleH, Shrink = 0f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End,
        Margin = new Edges4(0f, Rail.Hero.ArtTop + Spacing.XS, Spacing.S + Spacing.XS, 0f),
        Direction = 0, Corners = Radii.ControlAll, ClipToBounds = true, Fill = R.OnMedia.GlassHover,
        Children =
        [
            ToggleHalf(Icons.Picture, selected: true, Loc.Get(Strings.Player.SwitchToAudio), props.ShowArt),
            ToggleHalf(Icons.Movie, selected: false, Loc.Get(Strings.Player.SwitchToVideo), props.ShowVideoDocked),
        ],
    };

    static Element ToggleHalf(string glyph, bool selected, string tip, Action onClick) => ToolTip.Wrap(new BoxEl
    {
        Width = ClassicPanelLayout.ToggleHalfBox, Height = ClassicPanelLayout.ToggleHalfBox,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll, Fill = selected ? Tok.OnMediaPrimary with { A = ClassicPanelLayout.TogglePressedAlpha } : ColorF.Transparent,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick,
        Children = [new TextEl(glyph) { Size = ClassicPanelLayout.ToggleGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = selected ? Tok.OnMediaPrimary : Tok.OnMediaSecondary }],
    }, tip);

    // ── the header ───────────────────────────────────────────────────────────────────────────────────────────────────

    internal static BoxEl HeaderBox(Element[] kids) => new()
    {
        Direction = 0, Height = Design.Size.NavItemH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
        Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f), Children = kids,
    };

    internal static Element Header(PanelProps props, PanelMode body)
    {
        if (body != PanelMode.Video) return HeaderBox([TitleText(Title(body)), CloseButton(props.Close)]);
        // The header's ▣ / ⛶ are the one ALWAYS-visible way to move a docked video (W23b) — keep all three.
        return HeaderBox(
        [
            TitleText(Title(body)),
            HeaderButton(Icons.BackToWindow, Loc.Get(Strings.Player.VideoMiniPlayer), () =>
            {
                FluentGpu.Input.Announcer.Say(Loc.Get(Strings.Player.VideoMiniPlayer));
                props.VideoToMiniPlayer();
            }),
            HeaderButton(Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), () =>
            {
                FluentGpu.Input.Announcer.Say(Loc.Get(Strings.Player.VideoFullScreen));
                props.VideoToFullScreen();
            }),
            CloseButton(props.Close),
        ]);
    }

    /// <summary>The header's title, ellipsised in the row's free width (the lyrics header titles itself "Lyrics" or "Transcript").</summary>
    internal static Element TitleText(string title) => R.Type.RailHeader(title) with
    {
        Grow = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The title of a body the panel heads itself (the lyrics arm mounts its own header).</summary>
    static string Title(PanelMode m) => m switch
    {
        PanelMode.Queue => Loc.Get(Strings.Player.Queue),
        PanelMode.Friends => Loc.Get(Strings.Friends.Title),
        PanelMode.Video => Loc.Get(Strings.Player.Video),
        _ => Loc.Get(Strings.Player.NowPlaying),
    };

    /// <summary>A glyph button in the header. <paramref name="active"/> is the stateful arm (the accent tint is the only
    /// affordance a 32-DIP glyph has to say "on").</summary>
    internal static Element HeaderButton(string glyph, string tip, Action onClick, bool active = false) => ToolTip.Wrap(GlyphBox(glyph, onClick, active), tip);

    static BoxEl GlyphBox(string glyph, Action onClick, bool active) => new BoxEl
    {
        Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        OnClick = onClick,
        Children =
        [
            new TextEl(glyph)
            {
                Size = ClassicPanelLayout.HeaderGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                Color = active ? Tok.AccentTextPrimary : Tok.TextSecondary,
                HoverColor = active ? Tok.AccentTextPrimary : Tok.TextPrimary,
            },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>✕ closes the RAIL for every mode, video included — the coupling demotes a docked video. No tooltip: a close
    /// glyph is universal.</summary>
    internal static Element CloseButton(Action close) => GlyphBox(Icons.ChromeClose, close, active: false);
}

/// <summary>The panel's frame: the surface, the edge, the slide and the arm of the body on show. A component of the theme because
/// the slide targets the rendered root (the arm) and keeps its seeded-once flag across renders.</summary>
internal sealed class ClassicPanelFrame : Component
{
    bool _motionSeeded;
    Func<ColorF>? _surfaceFill;
    Action<RectF>? _onBounds;

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool open = view.Open;
        float railWidth = view.Width;
        bool floating = view.Floats;
        var body = view.Body;

        // ONE retained translate track on the host node; a rapid reversal continues from the PRESENTED position.
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.HostNode.IsNull) return;
            float x = open ? 0f : railWidth;
            if (!_motionSeeded)
            {
                _motionSeeded = true;                                  // seed closed off-canvas: no startup fly-in
                anim.Animate(Context.HostNode, AnimChannel.TranslateX, x, x, 1f, Easing.Linear);
                return;
            }
            float from = Context.Scene is { } scene ? scene.Paint(Context.HostNode).LocalTransform.Dx : (open ? railWidth : 0f);
            anim.Animate(Context.HostNode, AnimChannel.TranslateX, from, x, ClassicPanelLayout.SlideMs, ClassicPanelLayout.SlideEasing);
        }, DepKey.From(open ? 1 : 0, (int)MathF.Round(railWidth)));

        // The binds are built once per instance: a fresh thunk per render would re-register them for nothing.
        _surfaceFill ??= () => props.InlineAndOpen() ? ColorF.Transparent : R.Layer.FileArea;
        _onBounds ??= r => props.BodyHeightChanged(r.H);

        Element surface = new BoxEl
        {
            Grow = 1f, Corners = ClassicPanel.RailCorners, ClipToBounds = true,
            Fill = Prop.Of(_surfaceFill),
            OnBoundsChanged = _onBounds,
        };
        // The ONE docked hairline, LEFT + TOP, drawn topmost so content cannot cover it. Floating: the ring is the arm's.
        Element edge = new BoxEl
        {
            Margin = new Edges4(0f, 0f, -1f, -1f), HitTestVisible = false, Corners = ClassicPanel.RailCorners,
            BorderWidth = floating ? 0f : 1f, BorderColor = Tok.StrokeCardDefault,
        };

        if (body == PanelMode.NowPlaying)
            return ClassicPanel.Arm(open, floating, surface, edge,
                ClassicPanel.PinnedHero(props, view), ClassicPanel.GrowSlot(ClassicPanel.Body(props, PanelMode.NowPlaying)));

        Element header = body == PanelMode.Lyrics ? Slots.View(ViewKeys.PanelLyricsHeader, props.LyricsHeader) : ClassicPanel.Header(props, body);
        return ClassicPanel.Arm(open, floating, surface, edge,
            ClassicPanel.DockedCap(props, view.DockedHere, railWidth), header, ClassicPanel.GrowSlot(ClassicPanel.Body(props, body)));
    }
}
