// ── Themes/Zune/Structure/Views/Panel/ZuneLyricsBits.cs — Zune's lyrics header, "next up" and lyrics peek (P5 Z4a) ─────────────────────
//
// Board Z-Part-Panel:
//
//   Panel.LyricsHeader   NO title and NO close (Z2a's hand-off: the panel's pivot head carries "lyrics" and ✕). What is left is the board's
//                        meta row under the pivots, words 16 apart in the small tertiary voice: "translate" (the second line; the accent while
//                        on, only when the document carries one), the AI sparkle (only where the feature can run; it breathes while the track
//                        is being timed), "full screen" (the stage on lyrics), "options" (settings rework phase 5, design 5.4: a card with
//                        "lyrics blur" and its value over the slider, the SAME dial as Settings › lyrics written through
//                        `LyricsPaneOptions.SetBlur`, then the AI lyrics status line and the accent word "settings"), and the developer
//                        inspector's glyph. 28 tall, 40 at Compact.
//   Panel.NextUp         the rows under "up next": the shared media surface in its row shape at the board's 36 art and 52 floor.
//   Player.LyricsPeek    the sung line over a faded peek at the next on a 2-px square accent spine, both in the panel's lyric voice (22/28
//                        light); a playing video's one-row note. Each line is keyed by its index, so a new line flips in from below.
//
// The facts, verbs and clocks stay core (`RailWiring`, `AiLyrics.HeaderFacts`, `Rail.NextUpRows`, `Lyrics.PeekReel`).

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
using Wavee.Theming.Roles;

using P = Wavee.Themes.Zune.ZunePanelBodies;

namespace Wavee.Themes.Zune;

/// <summary>Zune's lyrics header: the meta row under the pivots.</summary>
internal static class ZuneLyricsHeader
{
    /// <summary>The node key of the meta row (the mounted tests find it).</summary>
    public const string Key = ZunePlayerLayout.LyricsMetaKey;

    /// <summary>The registered template of <see cref="ViewKeys.PanelLyricsHeader"/>.</summary>
    public static Element Template(PanelLyricsHeaderProps props, in ViewContext ctx)
    {
        var begin = ctx.Hooks.UseContext(SharedTransition.Begin);
        var tiers = ctx.Tier();
        float h = P.MetaRow(tiers.Policy is null ? WidthTier.Standard : tiers.Window);
        var facts = props.Read();
        var kids = new List<Element>(4);
        if (facts.HasSecondLine)
            kids.Add(ToolTip.Wrap(ZunePlayerBar.Word(Loc.Get(Strings.Theme.Zune.Player.Translate), props.ToggleSecondLine, true, facts.SecondLineActive, h, P.MetaSize),
                facts.SecondLineTip) with { Key = "lyrics-second-line" });
        kids.Add(Embed.Comp(new SparkleProps(props, h), static () => new ZuneLyricsSparkle()) with { Key = "ai-lyrics-header" });
        kids.Add(ToolTip.Wrap(ZunePlayerBar.Word(Loc.Get(Strings.Player.Fullscreen), () => props.Expand(begin), true, false, h, P.MetaSize), facts.ExpandTip)
            with { Key = "lyrics-expand" });
        if (props.Options is { } options)
            kids.Add(Embed.Comp(new ZuneLyricsOptions.Props(options, h), static () => new ZuneLyricsOptions()) with { Key = "lyrics-options" });
        kids.Add(Embed.Comp(new SparkleProps(props, h), static () => new ZuneLyricsInspector()) with { Key = "lyrics-inspector" });
        return new BoxEl
        {
            Key = Key, Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = P.WordRowPadding, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The props of the sparkle and the inspector glyph: the header's and the row's height.</summary>
    internal sealed record SparkleProps(PanelLyricsHeaderProps Header, float Height);
}

/// <summary>The "options" word: opens the lyrics options card under it (light dismiss); a second press closes it.</summary>
internal sealed class ZuneLyricsOptions : Component
{
    /// <summary>The options and the meta row's height (the Compact touch floor included).</summary>
    internal sealed record Props(LyricsPaneOptions Options, float Height);

    readonly Ref<NodeHandle> _anchor = new(default);
    readonly Action _toggle;
    OverlayHandle? _handle;
    IOverlayService? _overlay;
    Props? _props;

    public ZuneLyricsOptions() => _toggle = Toggle;

    public override Element Render()
    {
        var p = _props = UseProps<Props>();
        _overlay = UseContext(Overlay.Service);
        return ToolTip.Wrap(ZunePlayerBar.Word(Loc.Get(Strings.Theme.Zune.Stage.Options), _toggle, true, false, p.Height, P.MetaSize,
            realized: h => _anchor.Value = h), Loc.Get(Strings.Player.LyricsOptions));
    }

    void Toggle()
    {
        if (_handle is { IsOpen: true } open) { open.Close(); return; }
        if (_overlay is not { } overlay || _props is not { } props) return;
        var opened = overlay.Open(() => _anchor.Value, () => Embed.Comp(props, static () => new ZuneLyricsOptionsCard()),
            FlyoutPlacement.BottomEdgeAlignedLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
        _handle = opened;
        opened.ClosedAction = () => { if (ReferenceEquals(_handle, opened)) _handle = null; };
    }
}

/// <summary>The lyrics options card: "lyrics blur" and its value over the slider, then the AI lyrics status line and the accent word "settings".</summary>
internal sealed class ZuneLyricsOptionsCard : Component
{
    static readonly Slider.SliderOptions s_blurOptions = new()
    {
        Min = 0f, Max = P.LyricsBlurMax, Step = P.LyricsBlurStep, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    static readonly string?[] s_numbers = new string?[(int)P.LyricsBlurMax + 1];

    /// <summary>"off" at 0, else the number (each built once: a drag allocates nothing).</summary>
    static string BlurWords(float v)
    {
        int n = Math.Clamp((int)MathF.Round(v), 0, (int)P.LyricsBlurMax);
        return n == 0 ? Loc.Get(Strings.Stage.Options.BlurOff) : s_numbers[n] ??= n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public override Element Render()
    {
        var p = UseProps<ZuneLyricsOptions.Props>();
        var o = p.Options;
        var blur = UseFloatSignal(o.Blur());
        UseSignalEffect(() => blur.SetIfChanged(o.Blur()));
        string ai = o.AiLine();
        float inner = P.LyricsOptionsW - 2f * P.LyricsOptionsPad;
        var kids = new List<Element>(2)
        {
            new BoxEl
            {
                Key = "lyrics-options:blur", Direction = 1, Gap = P.LyricsOptionsInnerGap,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Settings.Appearance.LyricsBlur)) { Size = P.MetaSize, LineHeight = P.MetaLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Heading) },
                            new TextEl("") { Text = Prop.Of(() => BlurWords(blur.Value)), Size = P.MetaSize, LineHeight = P.MetaLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label) },
                        ],
                    },
                    Slider.Create(blur, v => o.SetBlur((int)MathF.Round(v / P.LyricsBlurStep) * (int)P.LyricsBlurStep), s_blurOptions,
                        length: inner, thickness: MathF.Max(P.LyricsSliderThickness, p.Height)),
                ],
            },
        };
        if (ai.Length > 0)
            kids.Add(new BoxEl
            {
                Key = "lyrics-options:ai", Direction = 0, AlignItems = FlexAlign.Center, Gap = P.MetaGap,
                Children =
                [
                    new TextEl(ai) { Size = P.MetaSize, LineHeight = P.MetaLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Shrink = 1f, MinWidth = 0f, MaxLines = 2, Wrap = TextWrap.Wrap },
                    ZunePlayerBar.Word(Loc.Get(Strings.Auth.Settings), o.OpenAiSettings, true, true, p.Height, P.MetaSize) with { Key = "lyrics-options:ai:settings", Role = AutomationRole.Hyperlink },
                ],
            });
        return new BoxEl
        {
            Key = "lyrics-options:card", Width = P.LyricsOptionsW, Direction = 1, Gap = P.LyricsOptionsGap,
            Padding = Edges4.All(P.LyricsOptionsPad), Children = kids.ToArray(),
        };
    }
}

/// <summary>The AI sparkle: hidden while the feature cannot run; the accent while the song is timed by AI; it breathes (a looping opacity
/// track on its own host node) while the track is being timed, and settles back when that ends.</summary>
internal sealed class ZuneLyricsSparkle : Component
{
    readonly Action _click;
    readonly Action _drivePulse;
    PanelLyricsHeaderProps? _props;
    LyricsSparkleFacts _facts;
    bool _pulse, _pulsed;

    public ZuneLyricsSparkle()
    {
        _click = () => _props?.SparkleClick(_facts);
        _drivePulse = DrivePulse;
    }

    public override Element Render()
    {
        var p = UseProps<ZuneLyricsHeader.SparkleProps>();
        var props = _props = p.Header;
        var facts = _facts = props.Sparkle();
        _pulse = facts.Visible && facts.Working && !Design.Reduced;
        UseLayoutEffect(_drivePulse, DepKey.From(_pulse));
        if (!facts.Visible) return new BoxEl { Visible = false };
        return new BoxEl
        {
            Shrink = 0f,
            Children = [ToolTip.Wrap(ZunePlayerBar.Glyph(Icons.RefineSparkle, _click, true, facts.Active ? Tok.AccentTextPrimary : Tok.TextTertiary, p.Height, P.MetaGlyph), facts.Tip)],
        };
    }

    void DrivePulse()
    {
        var ctx = Context;
        if (ctx.Anim is not { } anim || ctx.HostNode.IsNull) return;
        if (_pulse)
        {
            anim.Keyframes(ctx.HostNode, AnimChannel.Opacity, P.SparklePulse, P.SparklePulseMs, loop: true);
            _pulsed = true;
        }
        else if (_pulsed)
        {
            anim.Spring(ctx.HostNode, AnimChannel.Opacity, 1f, P.SparkleSettle);
            _pulsed = false;
        }
    }
}

/// <summary>The developer-mode inspector glyph: its own component, so only it subscribes the developer setting.</summary>
internal sealed class ZuneLyricsInspector : Component
{
    public override Element Render()
    {
        var p = UseProps<ZuneLyricsHeader.SparkleProps>();
        var overlay = UseContext(Overlay.Service);
        if (!p.Header.InspectorShown()) return new BoxEl { Visible = false };
        return ToolTip.Wrap(ZunePlayerBar.Glyph(Icons.Code, () => p.Header.OpenInspector(overlay), true, Tok.TextTertiary, p.Height, P.MetaGlyph),
            Loc.Get(Strings.Player.InspectLyrics));
    }
}

/// <summary>Zune's "next up" rows.</summary>
internal static class ZuneNextUp
{
    /// <summary>The registered template of <see cref="ViewKeys.PanelNextUp"/>.</summary>
    public static Element Template(NextUpProps props, in ViewContext ctx)
    {
        var rows = props.Read();
        var els = new Element[rows.Length];
        for (int k = 0; k < rows.Length; k++)
        {
            var row = rows[k];
            var shape = Shape.Row(TrackRowRules.ArtEdge(P.RowArt, row.Art)) with { MinHeight = P.RowH, PadX = P.RowPadX };
            els[k] = Controls.Surface(row.Data, shape) with { Key = row.Key };
        }
        return new BoxEl { Direction = 1, Children = els };
    }
}

/// <summary>Zune's lyrics peek.</summary>
internal static class ZuneLyricsPeek
{
    static readonly EnterExit FlipIn = new(Dy: P.PeekRowH, Opacity: 0f, Active: true);
    static readonly EnterExit FlipOut = new(Dy: -P.PeekRowH, Opacity: 0f, Active: true);

    /// <summary>The registered template of <see cref="ViewKeys.LyricsPeek"/>. Over the edge-to-edge canvas (<see cref="Deck.OverCanvas"/>) the
    /// lines are drawn in the media scope's ink.</summary>
    public static Element Template(LyricsPeekProps props, in ViewContext ctx)
    {
        var ink = new CanvasInk(ctx.Hooks.UseContext(Deck.OverCanvas));
        return props.Face switch
        {
            LyricsPeekFace.Reel => Reel(props, ink),
            LyricsPeekFace.Note => Note(props.OpenLyrics, ink),
            _ => new BoxEl(),
        };
    }

    /// <summary>The spine hangs in the panel's gutter (<see cref="ZunePanelBodies.PeekSpineHang"/>): the words stand on the bodies' text column.</summary>
    static Edges4 SpineHang => new(P.PeekSpineHang, 0f, 0f, 0f);

    static Element Reel(LyricsPeekProps props, CanvasInk ink) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Stretch, Gap = P.PeekGap, Height = P.PeekReelH, Margin = SpineHang,
        Cursor = CursorId.Hand, OnClick = props.OpenLyrics, Role = AutomationRole.Button, Focusable = true,
        Children =
        [
            new BoxEl { Key = P.PeekSpineKey, Width = P.PeekSpineW, Shrink = 0f, Fill = ink.AccentFill },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = P.PeekReelH, ClipToBounds = true,
                Children = [Line(props.Active, props.ActiveText, faded: false, ink), Line(props.Peek, props.PeekText, faded: true, ink)],
            },
        ],
    };

    static Element Note(Action openLyrics, CanvasInk ink) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = P.PeekGap, Height = P.PeekRowH, Margin = SpineHang,
        Cursor = CursorId.Hand, OnClick = openLyrics, Role = AutomationRole.Button, Focusable = true,
        Children =
        [
            new BoxEl { Key = P.PeekSpineKey, Width = P.PeekSpineW, Height = P.PeekRowH, Shrink = 0f, Fill = ink.Tertiary },
            new TextEl(Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo))
            {
                Size = P.PeekNoteSize, Color = ink.Tertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    static Element Line(int index, string? text, bool faded, CanvasInk ink)
    {
        Element inner = text is not null
            ? new BoxEl
            {
                Key = "l:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Height = P.PeekRowH, MinWidth = 0f, Grow = 1f, Basis = 0f, Direction = 1, Justify = FlexJustify.Center,
                Enter = FlipIn, Exit = FlipOut, Transition = MotionTok.ControlFast,
                Children = [R.Type.NpvLyric(text) with { Color = faded ? ink.Primary : ink.Accent, MaxLines = 1, MinWidth = 0f, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis }],
            }
            : new BoxEl { Height = P.PeekRowH };
        return new BoxEl
        {
            Height = P.PeekRowH, ClipToBounds = true, MinWidth = 0f, Opacity = faded ? P.PeekOpacity : 1f,
            Children = [inner],
        };
    }
}
