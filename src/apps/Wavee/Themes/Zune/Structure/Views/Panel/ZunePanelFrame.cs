// ── Themes/Zune/Structure/Views/Panel/ZunePanelFrame.cs — the frame of Zune's `Shell.Panel` (themes plan 6.3; P5 Z2a) ──────────────────
//
// Boards Z-Part-Panel, Z-Frame-Panel (inline) and Z-Frame-Narrow (floating). The core keeps what decides (`PanelHost`: open, mode, committed
// width, docked, floating or a sheet; the rail <-> docked-video coupling) and hands this template one decided `PanelView` per render. The
// frame places the host (`ZuneFrame`); this template draws the panel itself:
//
//   surface   design B (`ZunePanelWash.cs`): no plate. Docked and open the frame's full-height surface (the page wash under a scrim, the cover
//             glow, the edge) lies under the transparent panel; a card (floating, the sheet, sliding out) paints the same picture itself, opaque.
//             Floating it carries the flyout shadow and no ring, docked no shadow. Square everywhere.
//   column    the docked video card (mounted unconditionally; its own gate hides it) · the PIVOT HEAD · the body of the mode on show. Now
//             playing pins the card under the head instead (in the hero's place): the column reorders the two and both glide, so the card keeps
//             ONE parent, its node and its video surface across a mode switch (audit Z-OWNER, B10; the surface re-places its DComp child from the
//             card's transformed rect every frame it moves, so a FLIP of the card is no tear).
//   head      the pivots as words in the heading voice (26/32/300), 18 apart, lowercase (the style's navigation case), 44-tall hit boxes, on ONE line
//             (the owner, 2026-10-24: never wrapped, never shrunk): queue · lyrics · now playing · friends (where the capabilities offer the friends
//             panel) and video while the video body is up, as a CAROUSEL: the current word leads on the gutter in the primary ink, the rest follow
//             round the cycle in the tertiary ink and run off under a soft right fade (`ZunePivotCarousel`). A word shows its body in the same panel
//             (`PanelProps.ShowMode`); a pane that offers none (a docked pane beside the primary: the queue, friends at Huge 4K) draws its one word.
//             The close glyph (40 x 40) ends the row.
//   pivoting  a change slides the words like a Zune / Windows Phone pivot (the strip moves the new lead to the gutter; the words that pass it wrap
//             round) and the new pane slides in from the side of travel while the old one slides out the other way (`SlidePanes`).
//   docked    (ZT2, Z-Part-Panel "docked2" / "docked3") where panes dock beside the primary, the primary's head leaves out the bodies they
//             show (`PanelView.Beside`: lyrics · now playing · friends at Huge, lyrics · now playing at Huge 4K), and a LINKED pane draws no
//             close (it closes with the panel; the board draws none on the docked columns, the primary keeps its one, as inline).
//   bodies    queue (the core host of `Queue.Panel`), lyrics (`Panel.LyricsHeader` over the core host of `Lyrics.View`), now playing (the
//             hero `Player.NowPlayingHero`, or the docked video, then "up next" over `Panel.NextUp`), friends (`Panel.Friends`), video (the
//             playing track's title and line, then "up next"). The bodies are the player lane's views (Z4a): this frame gives them the full
//             width and lets them carry their own insets.
//   motion    ONE retained translate track on the host node: X docked and floating (the panel's width, plus nothing: it is flush right),
//             Y as a sheet (the window's height); 300 ms on the smooth-out curve; seeded at rest on mount and on a placement change (the host's
//             own FLIP carries that move). A rapid reversal continues from where it is drawn.
//   keys      Escape closes a floating panel or a sheet.
//
// The undrawn arms (now playing, video, the sheet) are built from Zune's parts (owner Q18).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's right panel.</summary>
internal static class ZunePanel
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellPanel"/>.</summary>
    public static Element Template(PanelProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZunePanelFrame());

    /// <summary>The pivots' cycle (the head draws it from the selected word on, `Rotated`).</summary>
    internal static readonly PanelMode[] PivotOrder = [PanelMode.Queue, PanelMode.Lyrics, PanelMode.NowPlaying, PanelMode.Friends];

    internal static string Word(PanelMode mode) => mode switch
    {
        PanelMode.Queue => Loc.Get(Strings.Player.Queue),
        PanelMode.Lyrics => Loc.Get(Strings.Player.Lyrics),
        PanelMode.Friends => Loc.Get(Strings.Friends.Title),
        PanelMode.Video => Loc.Get(Strings.Player.Video),
        _ => Loc.Get(Strings.Player.NowPlaying),
    };

    /// <summary>The words the head offers, in the panel's cyclic order (<see cref="PivotOrder"/>, then video while the video body is up), but the
    /// bodies the linked panes beside it show; a pane that offers none (no <see cref="PanelProps.ShowMode"/>) draws its one word.</summary>
    internal static PanelMode[] Modes(PanelProps props, PanelView view, PanelMode body, bool friends)
    {
        if (props.ShowMode is null) return [body];
        var modes = new List<PanelMode>(5);
        foreach (var mode in PivotOrder)
        {
            if (mode == PanelMode.Friends && !friends) continue;
            if (mode != body && view.Beside.Contains(mode)) continue;   // on screen in its own docked pane
            modes.Add(mode);
        }
        if (body == PanelMode.Video) modes.Add(PanelMode.Video);
        return modes.ToArray();
    }

    /// <summary><paramref name="modes"/> as the head draws them: the selected one first, then the rest in their cyclic order (the carousel).</summary>
    internal static PanelMode[] Rotated(PanelMode[] modes, PanelMode body)
    {
        int lead = Array.IndexOf(modes, body);
        if (lead <= 0) return modes;
        var order = new PanelMode[modes.Length];
        for (int i = 0; i < modes.Length; i++) order[i] = modes[(lead + i) % modes.Length];
        return order;
    }

    /// <summary>The pivot head: the words on ONE line in <paramref name="pivots"/>' order (the selected one on the gutter), then the close glyph (none
    /// on a linked pane). Every word keeps its size: the ones that do not fit the strip of a panel <paramref name="paneWidth"/> wide run off under its
    /// right edge fade, never wrapped, shrunk or cut mid-word (the owner, 2026-10-24). The strip reaches the panel's left edge and carries the gutter
    /// as its own inset, so a word that slides out to the left fades over the empty gutter while the lead never stands under the fade.</summary>
    internal static Element Head(PanelProps props, PanelView view, PanelMode body, ZunePivotCarousel<PanelMode> pivots, ZunePanelInk? ink, float paneWidth, bool sheet)
    {
        var order = pivots.Order;
        var words = new Element[order.Length];
        // A closed panel (laid out off-canvas) offers no tab stop.
        for (int i = 0; i < order.Length; i++) words[i] = Pivot(order[i], order[i] == body, props.ShowMode is not null, view.Open, pivots, ink, sheet);
        float strip = paneWidth - ZuneFrameRules.PanelHeadPadding.Right - (view.Linked ? 0f : ZuneFrameRules.Button);
        var fade = WordsOverflow(order, strip - ZuneFrameRules.PanelGutter, sheet) ? EdgeMask.Left | EdgeMask.Right : EdgeMask.Left;

        var row = new List<Element>(2)
        {
            new BoxEl
            {
                Key = ZuneFrameRules.PanelPivotsKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneFrameRules.PanelPivotGap,
                MinHeight = ZuneFrameRules.PanelHeadH, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                Padding = new Edges4(ZuneFrameRules.PanelGutter, 0f, 0f, 0f),
                EdgeFade = new EdgeFadeSpec(fade, ZuneFrameRules.PanelGutter, 0f, ZuneFrameRules.PanelEdgeFade, 0f),
                OnKeyDown = pivots.OnKey, OnRealized = pivots.OnStrip,
                Children = words,
            },
        };
        if (!view.Linked) row.Add(Close(props.Close));
        return new BoxEl
        {
            Key = ZuneFrameRules.PanelHeadKey, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
            Padding = ZuneFrameRules.PanelHeadPadding,
            Children = row.ToArray(),
        };
    }

    /// <summary>A pivot word as drawn (the heading voice, the navigation case; the Compact sheet's 40/48 page-title voice, Z-Frame-Panel-Compact); the
    /// selection's ink change cross-fades on the pivot slide.</summary>
    private static TextEl WordRun(string text, bool current, ZunePanelInk? ink, bool sheet) => (sheet ? R.Type.PageHero(text) : R.Type.Heading(text)) with
    {
        Color = current ? Tok.TextPrimary : ink?.Tertiary ?? Tok.TextTertiary, HoverColor = Tok.TextPrimary,
        Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f, BrushTransitionMs = ZuneMotion.PivotSlideMs,
    };

    /// <summary>A word's width on one line, in its drawn case (NaN with no text seam).</summary>
    private static float OneLine(string text, bool sheet)
    {
        var run = WordRun(text, current: false, ink: null, sheet);
        string drawn = run.Case switch { TextCase.Lower => text.ToLowerInvariant(), TextCase.Upper => text.ToUpperInvariant(), _ => text };
        return Controls.NaturalWidth(drawn, run);
    }

    /// <summary>The words on one line each, 18 apart, run past <paramref name="lane"/> (false where the lane or a width is unknown).</summary>
    private static bool WordsOverflow(PanelMode[] modes, float lane, bool sheet)
    {
        if (!float.IsFinite(lane)) return false;
        float total = 0f;
        for (int i = 0; i < modes.Length; i++)
        {
            float w = OneLine(Word(modes[i]), sheet);
            if (float.IsNaN(w)) return false;
            total += (i > 0 ? ZuneFrameRules.PanelPivotGap : 0f) + w;
        }
        return total > lane + ZuneFrameRules.PanelPivotStill;
    }

    /// <summary>One word: a tab (the selected one is the open head's one tab stop; Left / Right on it move the selection and the focus follows the
    /// selection to the front), the head's whole height (the
    /// touch floor of 40 holds at every tier). A click on a trailing word brings it to the front. One element shape for every state, so a word
    /// keeps its node as the carousel moves it.</summary>
    private static Element Pivot(PanelMode mode, bool current, bool pivots, bool open, ZunePivotCarousel<PanelMode> carousel, ZunePanelInk? ink, bool sheet) => new BoxEl
    {
        Key = ZuneFrameRules.PanelPivotKeyPrefix + mode, Shrink = 0f, MinHeight = ZuneFrameRules.PanelHeadH, Direction = 0, AlignItems = FlexAlign.Center,
        Role = AutomationRole.Tab, Focusable = pivots && open, TabStop = pivots && open && current, AllowFocusOnInteraction = false,
        Cursor = pivots && !current ? CursorId.Hand : CursorId.Arrow,
        OnClick = pivots && !current ? carousel.ClickOf(mode) : null,
        OnRealized = carousel.RealizedOf(mode),
        Children = [WordRun(Word(mode), current, ink, sheet)],
    };

    /// <summary>✕ closes the panel whatever it shows (a docked video is demoted by the coupling).</summary>
    private static Element Close(Action close) => ToolTip.Wrap(new BoxEl
    {
        Key = ZuneFrameRules.PanelCloseKey,
        Width = ZuneFrameRules.Button, Height = ZuneFrameRules.Button, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = R.Shape.All(ShapeRole.Control),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
        OnClick = close,
        Children =
        [
            new TextEl(Icons.ChromeClose)
            {
                Size = ZuneFrameRules.PanelCloseGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary,
            },
        ],
    }, Loc.Get(Strings.Theme.Zune.Frame.ClosePanel));

    /// <summary>The body of <paramref name="body"/> (never the docked video card: the column holds the one card in every mode).</summary>
    internal static Element Body(PanelProps props, PanelView view, PanelMode body) => body switch
    {
        PanelMode.Queue => props.QueueBody(),
        PanelMode.Lyrics => new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f,
            Children = [Slots.View(ViewKeys.PanelLyricsHeader, props.LyricsHeader), GrowSlot(props.LyricsBody())],
        },
        PanelMode.Friends => Slots.View(ViewKeys.PanelFriends, props.Friends),
        PanelMode.Video => Embed.Comp(props, static () => new ZunePanelVideoArm()),
        _ => NowPlaying(props, view),
    };

    private static BoxEl GrowSlot(Element body) => new() { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] };

    /// <summary>The now-playing arm: the pinned hero over "up next"; where the docked video takes the hero's place, the column pins the card over
    /// this arm (under the head), so the arm is "up next" alone.</summary>
    private static Element NowPlaying(PanelProps props, PanelView view)
    {
        var arm = Embed.Comp(props.NowPlaying, static () => new ZuneNowPlayingArm());
        return ScrollView(new BoxEl
        {
            Direction = 1,
            Children = view.Pinned != PanelPinned.DockedVideo ? [Slots.View(ViewKeys.NowPlayingHero, props.NowPlaying), arm] : [arm],
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }

    /// <summary>The docked video card with its splitter: the height cell the splitter writes and the card binds (a stable bind).</summary>
    internal static Element DockedCap(PanelProps props, bool docked, float width)
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
                            Min = props.DockedVideoMin(width),
                            Max = props.DockedVideoMax(),
                            Axis = SplitterAxis.Vertical, ShowIndicator = false,
                        }),
                    ],
                },
            ],
        };
    }

    /// <summary>"up next" over the shared rows, or the empty queue's vacancy.</summary>
    internal static Element[] UpNext(bool has, NextUpProps rows) =>
    [
        R.Type.Heading(Loc.Get(Strings.Player.VideoUpNext)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Heading) },
        has
            ? Slots.View(ViewKeys.PanelNextUp, rows)
            : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.QueueEmpty), subtitle: ""),
    ];
}

/// <summary>The panel's frame: the surface, the slide, the head and the body on show. A component because the slide targets the rendered
/// root and keeps its seeded-once flag across renders.</summary>
internal sealed class ZunePanelFrame : Component
{
    bool _motionSeeded;
    PanelPlacement _seededPlacement;
    static readonly Element[] s_noLayers = [];
    Action<RectF>? _onBounds;
    Action<KeyEventArgs>? _onKey;
    Action? _close;
    bool _escapes;
    readonly ZunePivotCarousel<PanelMode> _pivots = new() { Gap = ZuneFrameRules.PanelPivotGap, Gutter = ZuneFrameRules.PanelGutter };
    readonly Dictionary<PanelMode, NodeHandle> _panes = new();

    /// <summary>The panes' slide (PANES: the owner's pivot, 2026-10-24): <paramref name="enter"/> comes in from the side of <paramref name="travel"/>,
    /// fading in, while <paramref name="leave"/> (still mounted) goes the other way, fading out, from where it is drawn; the frame drops it when the
    /// slide has run. Reduced motion: a plain cross-fade.</summary>
    static void SlidePanes(SceneStore scene, AnimEngine anim, NodeHandle enter, NodeHandle leave, int travel, bool reduced)
    {
        float ms = ZuneMotion.PivotSlideMs, shift = reduced ? 0f : ZuneFrameRules.PanelPaneShift * (travel < 0 ? -1f : 1f);
        if (!enter.IsNull && scene.IsLive(enter))
        {
            ref var paint = ref scene.Paint(enter);
            float dx = paint.LocalTransform.Dx, alpha = paint.Opacity;
            // A pane coming back mid-slide continues from where it is drawn.
            bool fresh = alpha >= 1f && dx == 0f;
            anim.Animate(enter, AnimChannel.TranslateX, fresh ? shift : dx, 0f, ms, Easing.SmoothOut);
            anim.Animate(enter, AnimChannel.Opacity, fresh ? 0f : alpha, 1f, ms, Easing.SmoothOut);
        }
        if (!leave.IsNull && scene.IsLive(leave))
        {
            ref var paint = ref scene.Paint(leave);
            float dx = paint.LocalTransform.Dx, alpha = paint.Opacity;
            anim.Animate(leave, AnimChannel.TranslateX, dx, dx - shift, ms, Easing.SmoothOut);
            anim.Animate(leave, AnimChannel.Opacity, alpha, 0f, ZuneFrameRules.PanelPaneFadeMs, Easing.SmoothOut);
        }
    }

    readonly Dictionary<PanelMode, Action<NodeHandle>> _paneRealized = new();
    PanelMode? _shown;     // the body last drawn on the open panel
    PanelMode? _leaving;   // the pane a pivot change replaced, sliding out
    int _paneGen, _paneSeeded;

    NodeHandle PaneOf(PanelMode mode) => _panes.TryGetValue(mode, out var node) ? node : NodeHandle.Null;

    Action<NodeHandle> PaneRealized(PanelMode mode)
    {
        if (!_paneRealized.TryGetValue(mode, out var realized)) _paneRealized[mode] = realized = h => _panes[mode] = h;
        return realized;
    }

    /// <summary>The pane of <paramref name="mode"/>: its body, full width, keyed per mode (each pane is a different tree); a leaving pane takes no input.</summary>
    Element Pane(PanelProps props, PanelView view, PanelMode mode, bool leaving) => new BoxEl
    {
        Key = ZuneFrameRules.PanelPaneKeyPrefix + mode, Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, HitTestVisible = !leaving,
        OnRealized = PaneRealized(mode),
        Children = [ZunePanel.Body(props, view, mode)],
    };

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool open = view.Open;
        var placement = view.Placement;
        bool floating = placement != PanelPlacement.Dock;
        float width = placement == PanelPlacement.Float ? ZuneFrameRules.PanelFloatW : view.Width;
        var body = view.Body;
        var vp = UseContextSignal(Viewport.Size);
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
        bool friends = facts.Value.Capabilities.FriendsPanel;
        bool sheet = placement == PanelPlacement.Sheet;

        _close = props.Close;
        _escapes = open && floating;
        _onKey ??= e =>
        {
            if (e.Handled || e.KeyCode != Keys.Escape || e.Mods != KeyModifiers.None || !_escapes) return;
            e.Handled = true;
            _close?.Invoke();
        };

        // A pane that unmounts its body when closed (the second docked pane) keeps it for the slide-out, then lets it go.
        var (lingering, setLingering) = UseState(open);
        UseLayoutEffect(() => { if (open && !lingering) setLingering(true); }, DepKey.From(open ? 1 : 0));
        UseTimeout(() => { if (!open) setLingering(false); }, ZuneFrameRules.PanelSlideMs, DepKey.From(open ? 1 : 0));
        bool bodyLive = !view.UnmountWhenClosed || open || lingering;

        float offCanvas = sheet ? MathF.Max(1f, vp.Peek().Height) : width;
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.HostNode.IsNull) return;
            var channel = sheet ? AnimChannel.TranslateY : AnimChannel.TranslateX;
            float target = open ? 0f : offCanvas;
            if (!_motionSeeded || _seededPlacement != placement)
            {
                _motionSeeded = true;
                _seededPlacement = placement;
                anim.Animate(Context.HostNode, sheet ? AnimChannel.TranslateX : AnimChannel.TranslateY, 0f, 0f, 1f, Easing.Linear);
                anim.Animate(Context.HostNode, channel, target, target, 1f, Easing.Linear);
                return;
            }
            float from = Context.Scene is { } scene
                ? (sheet ? scene.Paint(Context.HostNode).LocalTransform.Dy : scene.Paint(Context.HostNode).LocalTransform.Dx)
                : (open ? offCanvas : 0f);
            anim.Animate(Context.HostNode, channel, from, target, Design.Reduced ? 1f : ZuneFrameRules.PanelSlideMs, ZuneFrameRules.PanelSlideEasing);
        }, DepKey.From(open ? 1 : 0, (int)MathF.Round(offCanvas) * 4 + (int)placement));

        // THE PIVOTS (ZunePivotCarousel): the selected word leads, the rest follow round the cycle; after the commit's layout the words slide from where
        // they were drawn and, on a change of body, the new pane slides in from the side of travel while the old one slides out the other way. The
        // old pane is dropped once its slide has run.
        _pivots.Show = props.ShowMode;
        _pivots.Order = ZunePanel.Rotated(ZunePanel.Modes(props, view, body, friends), body);
        int orderKey = 0;
        foreach (var mode in _pivots.Order) orderKey = orderKey * ZuneFrameRules.PanelModeRadix + (int)mode + 1;
        var dropped = UseSignal(0);
        _ = dropped.Value;
        if (open && bodyLive && _shown is { } shown && shown != body) { _leaving = shown; _paneGen++; }
        else if (!open) _leaving = null;
        _shown = open ? body : null;
        UseTimeout(() =>
        {
            if (_leaving is null) return;
            _leaving = null;
            dropped.Value = dropped.Peek() + 1;
        }, ZuneMotion.PivotSlideMs, DepKey.From(_paneGen));
        var input = UseContext(InputHooks.Current);
        UseLayoutEffect(() =>
        {
            if (Context.Scene is not { } scene || Context.Anim is not { } anim) return;
            int travel = _pivots.Slide(scene, anim, open, Design.Reduced);
            // A change made from the keyboard: the focus follows the selection to the front (the head's one tab stop).
            if (_pivots.TakeRefocus() && _pivots.Order.Length > 0 && _pivots.WordOf(_pivots.Order[0]) is { IsNull: false } lead && scene.IsLive(lead))
                (input?.MoveFocusVisual ?? input?.RestoreFocus)?.Invoke(lead);
            if (_paneSeeded == _paneGen) return;
            _paneSeeded = _paneGen;
            if (_leaving is { } gone) SlidePanes(scene, anim, PaneOf(body), PaneOf(gone), travel, Design.Reduced);
        }, DepKey.From(orderKey, open ? 1 : 0, _paneGen, 0));

        _onBounds ??= r => props.BodyHeightChanged(r.H);
        // Design B: docked and open, the frame's full-height surface lies under the panel (it stays transparent); a card (floating, the sheet, a docked
        // panel sliding out) paints its own: the page wash below its top edge, the scrim, the glow, the edge.
        bool inline = open && placement == PanelPlacement.Dock;
        var pageWash = UseContext(ZuneFrameContext.Material) is { } material
            ? ZunePageWash.Of(material.Value, UseContext(ZuneFrameContext.PageChrome)?.Value?.Backdrop ?? PageBackdrop.None, facts.Value.Tiers.Window)
            : null;
        var panelTop = UseContext(ZuneFrameContext.PanelTop);
        float top = inline ? 0f : panelTop?.Invoke() ?? 0f;
        var glow = ZunePanelWash.Glow(props.CoverUrl?.Invoke(), out var peak);
        var surface = inline ? s_noLayers : ZunePanelWash.Card(pageWash, top, glow);
        // The inks are judged where the head lies: docked, the full-height surface's top (the window's top: the strongest wash, the glow's peak).
        var ink = ZunePanelInk.For(ZunePanelWash.HeadColour(pageWash, top, peak));

        // The panes overlay each other while a pivot change slides them (a z-stack), and the slot clips them to the panel.
        Element bodySlot = new BoxEl
        {
            Key = ZuneFrameRules.PanelBodyKey,
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Padding = ZuneFrameRules.PanelBodyPadding,
            Children = !bodyLive ? [new BoxEl { Grow = 1f, HitTestVisible = false }]
                : _leaving is { } leaving ? [Pane(props, view, leaving, leaving: true), Pane(props, view, body, leaving: false)]
                : [Pane(props, view, body, leaving: false)],
        };
        // ONE parent for the docked card (B10): it mounts unconditionally in the column (its own gate hides it), over the head, or under it where now
        // playing pins it in the hero's place. A mode switch only reorders the card and the head (the body's top is their sum either way): both keep
        // their nodes and glide to their new places on the pivot slide.
        LayoutTransition? slide = Design.Reduced ? null : ZuneMotion.PivotSlide;
        Element card = ZunePanel.DockedCap(props, view.DockedHere, width) with { Key = ZuneFrameRules.PanelCardKey, Layout = slide };
        Element head = ZunePanel.Head(props, view, body, _pivots, ink, sheet ? vp.Value.Width : width, sheet) with { Layout = slide };
        var column = new List<Element>(3);
        if (body == PanelMode.NowPlaying) { column.Add(head); column.Add(card); }
        else { column.Add(card); column.Add(head); }
        column.Add(bodySlot);

        return Ctx.Provide(ZunePanelInk.Current, ink, new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Corners = R.Shape.All(ShapeRole.Overlay),
            HitTestVisible = open,                                             // a closed panel is laid out off-canvas and unhittable
            BlocksBackgroundScroll = open && floating,
            Shadow = floating ? Elevation.Flyout : (ShadowSpec?)null,          // no shadow docked
            OnKeyDown = _onKey,
            Children =
            [
                new BoxEl { Grow = 1f, ClipToBounds = true, ZStack = true, OnBoundsChanged = _onBounds, Children = surface },
                new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = column.ToArray() },
            ],
        });
    }
}

/// <summary>The now-playing arm's body under the pinned hero: "up next" (the hero carries the track itself).</summary>
internal sealed class ZuneNowPlayingArm : Component
{
    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        var track = props.NowTrack();
        if (!track.IsValid)
            return ZunePanelBodies.OnGutter(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                title: Loc.Get(Strings.Player.NothingPlaying), subtitle: ""));
        props.SubscribeEntities();
        return new BoxEl
        {
            Direction = 1, Gap = ZuneFrameRules.PanelArmGap, Padding = ZuneFrameRules.PanelArmPadding,
            Children = ZunePanel.UpNext(props.HasNextUp(), props.NextUp),
        };
    }
}

/// <summary>The video arm: the playing track's title and line under the docked card, then "up next" (Z-Video's panel, from Zune's parts).</summary>
internal sealed class ZunePanelVideoArm : Component
{
    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var now = props.NowPlaying;
        var track = now.NowTrack();
        props.SubscribeVideoBody();
        var content = new List<Element>(5);
        if (track.IsValid)
        {
            content.Add(R.Type.NowPlayingTitle(track.Title) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis });
            string artists = now.ResolveString(track.ArtistLineId), album = track.Album.IsValid ? track.Album.Title : "";
            string meta = artists.Length == 0 ? album : album.Length == 0 ? artists : artists + " · " + album;
            if (meta.Length > 0) content.Add(R.Type.TrackMeta(meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }
        content.AddRange(ZunePanel.UpNext(now.HasNextUp(), now.NextUp));
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = ZuneFrameRules.PanelArmGap, Padding = ZuneFrameRules.PanelArmPadding,
            Children = content.ToArray(),
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }
}

/// <summary>The sheet's dim (black at 40 %): it fades with the sheet's open state and a press on it closes the panel. Nothing (transparent,
/// never hit) at every other placement.</summary>
internal sealed class ZunePanelScrim : Component
{
    bool _mounted;

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool show = view.Open && view.Placement == PanelPlacement.Sheet;
        float ms = Design.Reduced ? 0f : ZuneFrameRules.SheetScrimFadeMs;
        float target = show ? 1f : 0f;
        UseTransition(AnimChannel.Opacity, _mounted ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(show));
        _mounted = true;
        return new BoxEl
        {
            Grow = 1f, Opacity = target, HitTestVisible = show, OnClick = show ? props.Close : null, TabStop = false,
            BlocksBackgroundScroll = show,
            Children = [new BoxEl { Grow = 1f, Fill = ZuneFrameRules.SheetScrim, HitTestVisible = false }],
        };
    }
}
