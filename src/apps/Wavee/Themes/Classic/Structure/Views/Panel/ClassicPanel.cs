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
// PER WINDOW TIER (TC2, `ClassicPanelTiers`; the core decides `PanelView.Placement` from the committed window tier): DOCKED (Standard,
// Huge) the arm is the band described above; FLOATING (Small) it is a 340-wide r8 card with the ring and the flyout shadow that slides in
// from the right; a SHEET (Compact) has r8 top corners and a 16-tall grab strip, slides UP from the window's bottom edge, and its
// now-playing arm carries its own header (close · "Playing from" · ⋯, `ClassicSheetHeader`) and the Lyrics, Devices and Queue doors under the hero (C-NowPlaying-Compact). The frame places the host and the
// scrim; a crossing that changes the placement re-seeds the slide at rest (the host's own FLIP carries the move).
//
// THE SHEET IS A MODAL SURFACE (the Compact overlays fix). Over the page and the mini bar it takes the wheel (`BlocksBackgroundScroll` on the
// arm and the scrim: the page is a sibling under the panel layer, not an ancestor, so the router's containing-scroller scan found it), focus
// moves into it with Tab trapped there (`ClassicModalScope`), Escape closes it, and its HEAD (the grab strip and the row under it) is the shared
// bottom-sheet drag (`ClassicSheetDrag`): it follows the pointer 1:1, the scrim fading with it, and a release past the threshold closes the
// panel (the slide-out runs from where the drag left it) or springs it back. The now-playing sheet is its own column (C-NowPlaying-Compact,
// `ClassicNowPlayingSheet`): the art shrinks so the title, the transport and the doors fit the window; the rest scrolls.
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
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's right panel.</summary>
internal static class ClassicPanel
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellPanel"/>.</summary>
    public static Element Template(PanelProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicPanelFrame());

    internal static CornerRadius4 RailCorners => new(Radii.Card, 0f, 0f, 0f);

    /// <summary>The art side the now-playing hero is drawn at inside the Compact sheet (null everywhere else: the hero's own rail side).</summary>
    internal static readonly Context<Func<float>?> SheetArtSide = new(null);

    /// <summary>The shared sheet drag of the primary panel host (null outside the frame's panel layer, and for the second pane).</summary>
    internal static readonly Context<ClassicSheetDrag?> SheetDragSlot = new(null);

    // ── the arm ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The arm's corners for a placement: the card radius top-left docked, every corner floating, the top two as a sheet.</summary>
    internal static CornerRadius4 CornersFor(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Float => CornerRadius4.All(ClassicPanelTiers.Corner),
        PanelPlacement.Sheet => new CornerRadius4(ClassicPanelTiers.Corner, ClassicPanelTiers.Corner, 0f, 0f),
        _ => RailCorners,
    };

    internal static BoxEl Arm(bool open, PanelPlacement placement, Element surface, Element edge, Element? head, params Element[] column)
    {
        bool floating = placement != PanelPlacement.Dock;
        // A sheet leads with its head (the grab strip and the row under it: the drag handle); the column below is the arm's own.
        Element[] kids = head is not null ? [head, .. column] : column;
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Corners = CornersFor(placement),
            HitTestVisible = open,                                            // a closed rail is laid out, off-clip, unhittable
            // Floating or a sheet it lies OVER the page: the wheel on its own non-scrolling parts must not reach the page behind.
            BlocksBackgroundScroll = open && floating,
            BorderColor = floating ? Tok.StrokeCardDefault : ColorF.Transparent,
            BorderWidth = floating ? 1f : 0f,
            Shadow = floating ? Elevation.Flyout : (ShadowSpec?)null,         // NO shadow docked
            Children = [surface, new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = kids }, edge],
        };
    }

    /// <summary>The sheet's head: the grab strip over <paramref name="row"/>, wired as the shared drag handle (the row's buttons keep their
    /// own presses).</summary>
    internal static Element SheetHead(ClassicSheetDrag? drag, Element row)
    {
        var head = new BoxEl { Key = ClassicPanelTiers.SheetHeadKey, Direction = 1, Shrink = 0f, Children = [GrabStrip(), row] };
        return drag is null ? head : drag.Head(head);
    }

    /// <summary>The sheet's grab strip: a quiet handle that says the panel is a sheet (drag it down, or the scrim, Escape and ✕ close it).</summary>
    static Element GrabStrip() => new BoxEl
    {
        Height = ClassicPanelTiers.GrabStripH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = ClassicPanelTiers.GrabBarW, Height = ClassicPanelTiers.GrabBarH,
                Corners = CornerRadius4.All(ClassicPanelTiers.GrabBarH / 2f), Fill = Tok.StrokeControlStrongDefault,
            },
        ],
    };

    /// <summary>The now-playing sheet's doors under the hero (C-NowPlaying-Compact: Lyrics, Devices and Queue, three equal 48-tall columns):
    /// Lyrics and Queue open that body in the same sheet; Devices (where <paramref name="transport"/> offers one) opens the device picker,
    /// named for the device playback is on. The set is fixed for the props (built once), so the row never re-flows.</summary>
    internal static Element SheetDoors(Action<PanelMode> show, PanelTransportProps? transport)
    {
        bool devices = transport is { Device: not null, ToggleDevicePicker: not null };
        Element lyrics = SheetDoor(WaveeIcons.Lyrics, WaveeIcons.Font, Loc.Get(Strings.Player.Lyrics), () => show(PanelMode.Lyrics));
        Element queue = SheetDoor(Icons.Queue, null, Loc.Get(Strings.Player.Queue), () => show(PanelMode.Queue));
        return new BoxEl
        {
            Key = ClassicPanelTiers.SheetDoorsKey, Direction = 0, Shrink = 0f, Gap = ClassicPanelTiers.SheetDoorGap,
            Padding = new Edges4(ClassicPanelTiers.SheetDoorInset, ClassicPanelTiers.SheetDoorPadY, ClassicPanelTiers.SheetDoorInset, ClassicPanelTiers.SheetDoorPadY),
            Children = devices
                ? [
                    lyrics,
                    // The door is a component (its anchor, its picker handle, the roster it reads); its column takes the door's share of the row.
                    new BoxEl
                    {
                        Key = ClassicPanelTiers.SheetDevicesKey, Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1,
                        Children = [Embed.Comp(transport!, static () => new ClassicSheetDeviceDoor())],
                    },
                    queue,
                  ]
                : [lyrics, queue],
        };
    }

    static Element SheetDoor(string glyph, string? font, string label, Action onClick)
        => DoorBox(glyph, font, label, onClick, latched: false, realized: null) with { Grow = 1f, Basis = 0f, MinWidth = 0f };

    /// <summary>One door: the glyph over its label (12/16), centred, the label ellipsised in the door's width. <paramref name="latched"/> inks it in the
    /// accent (the Devices door while playback is on another device).</summary>
    internal static BoxEl DoorBox(string glyph, string? font, string label, Action onClick, bool latched, Action<NodeHandle>? realized)
    {
        ColorF ink = latched ? Tok.AccentTextPrimary : Tok.TextSecondary;
        ColorF hover = latched ? Tok.AccentTextPrimary : Tok.TextPrimary;
        return new BoxEl
        {
            Height = ClassicPanelTiers.SheetDoorH,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.XS, Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
            OnRealized = realized,
            Children =
            [
                new TextEl(glyph) { Size = ClassicPanelTiers.SheetDoorGlyph, FontFamily = font ?? Tok.Definition.Fonts.Icon, Color = ink, HoverColor = hover },
                CentredLine(new TextEl(label)
                {
                    Size = ClassicPanelTiers.SheetDoorText, LineHeight = ClassicPanelTiers.SheetDoorTextLine, Color = ink,
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                }),
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>One line of text centred across its column and ellipsised when it is wider (a row that lets the text shrink: the engine's text
    /// has no alignment of its own).</summary>
    internal static Element CentredLine(TextEl text) => new BoxEl
    {
        Direction = 0, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Children = [text with { Shrink = 1f, MinWidth = 0f }],
    };

    // ── the now-playing sheet's header (C-NowPlaying-Compact) ────────────────────────────────────────────────────────

    /// <summary>A 40 x 40 glyph button of the sheet's header (the close chevron, the "⋯"); a disabled one is drawn quiet and takes no press.</summary>
    internal static BoxEl SheetGlyphButton(string glyph, Action? onClick, bool enabled) => new BoxEl
    {
        Width = ClassicPanelTiers.SheetHeaderButton, Height = ClassicPanelTiers.SheetHeaderButton, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Role = AutomationRole.Button, Focusable = enabled, AllowFocusOnInteraction = false, IsEnabled = enabled,
        Cursor = enabled ? CursorId.Hand : null, OnClick = enabled ? onClick : null,
        Children =
        [
            new TextEl(glyph) { Size = ClassicPanelTiers.SheetHeaderGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? Tok.TextPrimary : Tok.TextDisabled },
        ],
    }.Interactive(Interaction.Subtle, enabled);

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

    /// <summary>The now-playing sheet's column (C-NowPlaying-Compact): one scroll holding the hero at the side the window leaves it (its
    /// video switch over its corner when the video could dock), then the identity, the transport, the doors and the rest
    /// (<see cref="ClassicNowPlayingSheet"/>). The side is bound to the window, so a resize re-fits it in the same frame.</summary>
    internal static Element NowPlayingSheet(PanelProps props, PanelView view, IReadSignal<Size2> vp)
    {
        Func<float> side = () => ClassicPanelTiers.SheetArtSide(vp.Value.Width, vp.Value.Height, ClassicPanelTiers.SheetHeadH);
        Element art = Ctx.Provide(SheetArtSide, side, Slots.View(ViewKeys.NowPlayingHero, props.NowPlaying));
        Element hero = view.Pinned == PanelPinned.ArtWithVideoToggle
            ? new BoxEl { ZStack = true, Shrink = 0f, AlignSelf = FlexAlign.Center, Children = [art, ArtVideoToggle(props, sheet: true)] }
            : art;
        return ScrollView(new BoxEl
        {
            Direction = 1,
            Children = [hero, Embed.Comp(props, static () => new ClassicNowPlayingSheet())],
        }) with { Key = ClassicPanelTiers.SheetScrollKey, Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }

    /// <summary>The NowPlaying arm's pinned slot — ONE slot, two possible occupants, never both.</summary>
    internal static Element PinnedHero(PanelProps props, PanelView view)
    {
        Element art = Slots.View(ViewKeys.NowPlayingHero, props.NowPlaying);
        return view.Pinned switch
        {
            PanelPinned.DockedVideo => DockedCap(props, true, view.Width),
            PanelPinned.ArtWithVideoToggle => new BoxEl { ZStack = true, Shrink = 0f, Children = [art, ArtVideoToggle(props, sheet: false)] },
            _ => art,                                                      // the page stage owns the one surface, or nothing to dock
        };
    }

    /// <summary>The 2-state Art|Video toggle over the ART's top-right corner. Its offset is DERIVED from
    /// <see cref="Rail.Hero.ArtTop"/> (in the sheet, from the art's own top: the head carries the header row); each half's tooltip names the
    /// action IT performs.</summary>
    static Element ArtVideoToggle(PanelProps props, bool sheet) => new BoxEl
    {
        Height = ClassicPanelLayout.ToggleH, Shrink = 0f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End,
        Margin = sheet
            ? new Edges4(0f, ClassicPanelTiers.SheetArtTop + Spacing.XS, Spacing.XS, 0f)
            : new Edges4(0f, Rail.Hero.ArtTop + Spacing.XS, Spacing.S + Spacing.XS, 0f),
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
    PanelPlacement _seededPlacement;
    Func<ColorF>? _surfaceFill;
    Action<RectF>? _onBounds;
    Action<KeyEventArgs>? _onKey;
    Action? _close;
    readonly ClassicModalScope _scope = new();

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool open = view.Open;
        var placement = view.Placement;
        bool floating = view.Floats;
        // The width the arm is laid out at: the committed width docked, the record's floating width at Small; a sheet is the window's width.
        float railWidth = placement == PanelPlacement.Float ? ClassicPanelTiers.FloatW : view.Width;
        var body = view.Body;
        var vp = UseContextSignal(Viewport.Size);
        bool sheet = placement == PanelPlacement.Sheet;
        var hooks = UseContext(InputHooks.Current);
        // The shared bottom-sheet drag (the frame's panel layer provides one for the primary host): it moves this arm and closes the panel.
        var drag = UseContext(ClassicPanel.SheetDragSlot);
        if (!sheet) drag = null;
        drag?.Bind(Context, hooks);
        _close = props.Close;
        if (drag is not null) drag.Dismiss = _close;
        UseLayoutEffect(() => { if (drag is not null) drag.SlideNode = Context.HostNode; });   // the arm runs the open/close slide; the drag moves the host
        // A sheet is modal: focus into it with Tab trapped there, back where it was on close; Escape closes it.
        bool modal = open && sheet;
        UseLayoutEffect(() => _scope.Update(hooks, Context.Scene, Context.HostNode, modal), DepKey.From(modal));
        UseEffect(() => (Action?)(() => _scope.Lower(Context.Scene)), DepKey.Empty);
        _onKey ??= e =>
        {
            if (e.Handled || e.KeyCode != Keys.Escape || e.Mods != KeyModifiers.None || !_scope.Active) return;
            e.Handled = true;
            _close?.Invoke();
        };
        // A pane that unmounts its body when closed (the second docked pane) keeps it for the slide-out, then lets it go.
        var (lingering, setLingering) = UseState(open);
        UseLayoutEffect(() => { if (open && !lingering) setLingering(true); }, DepKey.From(open ? 1 : 0));
        UseTimeout(() => { if (!open) setLingering(false); }, ClassicPanelLayout.SlideMs, DepKey.From(open ? 1 : 0));
        bool bodyLive = !view.UnmountWhenClosed || open || lingering;
        // Off-canvas: a sheet below the window's bottom edge, a floating card past its 8-DIP inset, a docked band by its width.
        float offCanvas = sheet ? MathF.Max(1f, vp.Peek().Height) : placement == PanelPlacement.Float ? railWidth + ClassicPanelTiers.Gap : railWidth;

        // ONE retained translate track on the host node (Y for a sheet, X otherwise); a rapid reversal continues from the PRESENTED position.
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.HostNode.IsNull) return;
            var channel = sheet ? AnimChannel.TranslateY : AnimChannel.TranslateX;
            float target = open ? 0f : offCanvas;
            if (!_motionSeeded || _seededPlacement != placement)
            {
                // Seed at rest (no startup fly-in), and again when a crossing changes the placement: the host's own FLIP carries that move,
                // and the other axis is put back to rest.
                _motionSeeded = true;
                _seededPlacement = placement;
                anim.Animate(Context.HostNode, sheet ? AnimChannel.TranslateX : AnimChannel.TranslateY, 0f, 0f, 1f, Easing.Linear);
                anim.Animate(Context.HostNode, channel, target, target, 1f, Easing.Linear);
                return;
            }
            // Where it is DRAWN: a running track (the slide, a drag's last pose, its spring back) first, else the painted pose.
            float painted = Context.Scene is { } scene
                ? (sheet ? scene.Paint(Context.HostNode).LocalTransform.Dy : scene.Paint(Context.HostNode).LocalTransform.Dx)
                : (open ? offCanvas : 0f);
            float from = ClassicOverlayMotion.From(Context, channel, painted);
            anim.Animate(Context.HostNode, channel, from, target, ClassicPanelLayout.SlideMs, ClassicPanelLayout.SlideEasing);
        }, DepKey.From(open ? 1 : 0, (int)MathF.Round(offCanvas) * 4 + (int)placement));

        // The binds are built once per instance: a fresh thunk per render would re-register them for nothing.
        _surfaceFill ??= () => props.InlineAndOpen() ? ColorF.Transparent : R.Layer.FileArea;
        _onBounds ??= r => props.BodyHeightChanged(r.H);

        var corners = ClassicPanel.CornersFor(placement);
        Element surface = new BoxEl
        {
            Grow = 1f, Corners = corners, ClipToBounds = true,
            Fill = Prop.Of(_surfaceFill),
            OnBoundsChanged = _onBounds,
        };
        // The ONE docked hairline, LEFT + TOP, drawn topmost so content cannot cover it. Floating and as a sheet: the ring is the arm's.
        Element edge = new BoxEl
        {
            Margin = new Edges4(0f, 0f, -1f, -1f), HitTestVisible = false, Corners = corners,
            BorderWidth = floating ? 0f : 1f, BorderColor = Tok.StrokeCardDefault,
        };

        BoxEl arm;
        if (body == PanelMode.NowPlaying)
            arm = sheet
                ? ClassicPanel.Arm(open, placement, surface, edge,
                    // The sheet's own header (C-NowPlaying-Compact): close · "Playing from" · ⋯, in a column so the component's anchor stretches.
                    ClassicPanel.SheetHead(drag, new BoxEl
                    {
                        Direction = 1, Children = [Embed.Comp(props, static () => new ClassicSheetHeader())],
                    }),
                    ClassicPanel.NowPlayingSheet(props, view, vp))
                : ClassicPanel.Arm(open, placement, surface, edge, null,
                    ClassicPanel.PinnedHero(props, view), ClassicPanel.GrowSlot(ClassicPanel.Body(props, PanelMode.NowPlaying)));
        else
        {
            Element header = body == PanelMode.Lyrics ? Slots.View(ViewKeys.PanelLyricsHeader, props.LyricsHeader) : ClassicPanel.Header(props, body);
            Element bodySlot = ClassicPanel.GrowSlot(bodyLive ? ClassicPanel.Body(props, body) : new BoxEl { Grow = 1f, HitTestVisible = false });
            arm = sheet
                ? ClassicPanel.Arm(open, placement, surface, edge, ClassicPanel.SheetHead(drag, header),
                    ClassicPanel.DockedCap(props, view.DockedHere, railWidth), bodySlot)
                : ClassicPanel.Arm(open, placement, surface, edge, null,
                    ClassicPanel.DockedCap(props, view.DockedHere, railWidth), header, bodySlot);
        }
        return sheet ? arm with { OnKeyDown = _onKey } : arm;
    }
}

/// <summary>The Compact sheet's scrim (TC2, C-Queue-Compact / C-NowPlaying-Compact: black at 0.4 from the title bar down): it fades with
/// the sheet's open state and a press on it closes the sheet. Nothing (transparent, never hit) at every other placement.</summary>
internal sealed class ClassicPanelScrim : Component
{
    bool _mounted;
    Action<NodeHandle>? _dimRealized;

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool show = view.Open && view.Placement == PanelPlacement.Sheet;
        var drag = UseContext(ClassicPanel.SheetDragSlot);
        float ms = Design.Reduced ? 0f : Design.Motion.Fast;
        float target = show ? 1f : 0f;
        UseTransition(AnimChannel.Opacity, _mounted ? ClassicOverlayMotion.From(Context, AnimChannel.Opacity, 1f - target) : target, target, ms, Easing.Linear,
            DepKey.From(show));
        _mounted = true;
        // The dim a sheet drag fades lives on an inner layer, so the open/close fade above and the drag's share multiply (never fight); it is
        // put back at full strength as the sheet opens, while the layer above is still clear.
        UseLayoutEffect(() => { if (show) drag?.ResetDim(); }, DepKey.From(show));
        if (drag is not null) _dimRealized ??= h => drag.DimNode = h;
        return new BoxEl
        {
            Grow = 1f, Opacity = target, HitTestVisible = show, OnClick = show ? props.Close : null, TabStop = false,
            // The page is a sibling under the panel layer, never an ancestor: without this the wheel over the dim found it.
            BlocksBackgroundScroll = show,
            Children = [new BoxEl { Grow = 1f, Fill = ClassicPanelTiers.Scrim, HitTestVisible = false, OnRealized = _dimRealized }],
        };
    }
}
