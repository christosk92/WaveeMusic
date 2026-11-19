// ── Themes/WinUi/Structure/Views/Panel/WinUiLyricsBits.cs — WinUI's lyrics header, "next up" and lyrics peek (P6 W4a) ─────────────────────
//
// Board W-PaneLyrics (and the Lyrics + Friends pane of W-Shell-Huge):
//
//   Panel.LyricsHeader   under the pane's tabs: the track's title (Body Strong) at the left; at the right the "Translate" button (the standard
//                        button, only when the document carries a second line; the accent while it is on), the AI sparkle (only where the
//                        feature can run; it breathes while the track is being timed), the developer inspector and full screen (the stage on
//                        lyrics) as 32 subtle buttons (40 at Compact, the touch floor), then ⋯ (settings rework phase 5, design 5.4): a
//                        FlyoutPresenter card, "Lyrics blur" (Caption Strong) with its value over the slider (the SAME dial as Settings ›
//                        Lyrics, written through `LyricsPaneOptions.SetBlur`), then the AI lyrics status line with a "Settings" hyperlink.
//                        The pane's tabs carry "Lyrics" and the close.
//   Panel.NextUp         the rows under "Next up": the shared media surface in its row shape at the 40 cover.
//   Player.LyricsPeek    the now-playing pane's lyrics card (W-PaneNowPlaying): the card, "Lyrics" (Caption), the sung line in the primary ink
//                        (Body Strong) over the next in the secondary, "Show lyrics" as an accent link; a playing video's one-row note. Each
//                        line is keyed by its index, so a new line flips in from below.
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

using P = Wavee.Themes.WinUi.WinUiPanelBodies;
using PL = Wavee.Themes.WinUi.WinUiPlayerLayout;

namespace Wavee.Themes.WinUi;

/// <summary>Zune's lyrics header: the meta row under the pivots.</summary>
internal static class WinUiLyricsHeader
{
    /// <summary>The node key of the meta row (the mounted tests find it).</summary>
    public const string Key = WinUiPanelBodies.LyricsMetaKey;

    /// <summary>The registered template of <see cref="ViewKeys.PanelLyricsHeader"/>.</summary>
    public static Element Template(PanelLyricsHeaderProps props, in ViewContext ctx)
    {
        var begin = ctx.Hooks.UseContext(SharedTransition.Begin);
        var tiers = ctx.Tier();
        float h = P.MetaRow(tiers.Policy is null ? WidthTier.Standard : tiers.Window);
        var facts = props.Read();
        // W-PaneLyrics: the song's name (Body Strong) over how it is timed (Caption, secondary); "Lyrics" / "Transcript" while no song names it.
        var name = new TextEl(facts.TrackTitle is { Length: > 0 } song ? song : facts.Title)
        {
            Size = P.HeaderTitleSize, LineHeight = P.HeaderTitleLine, Weight = P.HeaderTitleWeight, Color = Tok.TextPrimary,
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var kids = new List<Element>(5)
        {
            new BoxEl
            {
                Key = "lyrics-header-title", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, ClipToBounds = true,
                Children = facts.SyncLine is { Length: > 0 } sync
                    ? [name, new TextEl(sync) { Size = P.HeaderSyncSize, LineHeight = P.HeaderSyncLine, Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                    : [name],
            },
        };
        if (facts.HasSecondLine)
            kids.Add(ToolTip.Wrap(new BoxEl
            {
                Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = P.TranslatePadding,
                Corners = CornerRadius4.All(PL.ButtonCorner), BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                Fill = facts.SecondLineActive ? Tok.AccentDefault : Tok.FillControlDefault,
                HoverFill = facts.SecondLineActive ? Tok.AccentSecondary : Tok.FillControlSecondary,
                PressedFill = facts.SecondLineActive ? Tok.AccentTertiary : Tok.FillControlTertiary,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = props.ToggleSecondLine,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Theme.Winui.Player.Translate))
                    {
                        Size = P.MetaSize, LineHeight = P.MetaLine, Weight = P.HeaderTitleWeight,
                        Color = facts.SecondLineActive ? Tok.TextOnAccentPrimary : Tok.TextPrimary, Wrap = TextWrap.NoWrap,
                    },
                ],
            }, facts.SecondLineTip) with { Key = "lyrics-second-line" });
        kids.Add(Embed.Comp(new SparkleProps(props, h), static () => new WinUiLyricsSparkle()) with { Key = "ai-lyrics-header" });
        kids.Add(Embed.Comp(new SparkleProps(props, h), static () => new WinUiLyricsInspector()) with { Key = "lyrics-inspector" });
        kids.Add(ToolTip.Wrap(WinUiPlayerBar.Button(Icons.FullScreen, () => props.Expand(begin), true, false, h, h, P.MetaGlyph),
            facts.ExpandTip) with { Key = "lyrics-expand" });
        if (props.Options is { } options)
            kids.Add(Embed.Comp(new WinUiLyricsOptions.Props(options, h), static () => new WinUiLyricsOptions()) with { Key = "lyrics-options" });
        return new BoxEl
        {
            Key = Key, Height = h + P.HeaderRowExtra, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = P.MetaGap, Padding = P.HeaderPadding,
            ClipToBounds = true, Children = kids.ToArray(),
        };
    }

    /// <summary>The props of the sparkle and the inspector glyph: the header's and the row's height.</summary>
    internal sealed record SparkleProps(PanelLyricsHeaderProps Header, float Height);
}

/// <summary>The AI sparkle: hidden while the feature cannot run; the accent while the song is timed by AI; it breathes (a looping opacity
/// track on its own host node) while the track is being timed, and settles back when that ends.</summary>
internal sealed class WinUiLyricsSparkle : Component
{
    readonly Action _click;
    readonly Action _drivePulse;
    PanelLyricsHeaderProps? _props;
    LyricsSparkleFacts _facts;
    bool _pulse, _pulsed;

    public WinUiLyricsSparkle()
    {
        _click = () => _props?.SparkleClick(_facts);
        _drivePulse = DrivePulse;
    }

    public override Element Render()
    {
        var p = UseProps<WinUiLyricsHeader.SparkleProps>();
        var props = _props = p.Header;
        var facts = _facts = props.Sparkle();
        _pulse = facts.Visible && facts.Working && !Design.Reduced;
        UseLayoutEffect(_drivePulse, DepKey.From(_pulse));
        if (!facts.Visible) return new BoxEl { Visible = false };
        return new BoxEl
        {
            Shrink = 0f,
            Children = [ToolTip.Wrap(WinUiPlayerBar.Button(Icons.RefineSparkle, _click, true, facts.Active, p.Height, p.Height, P.MetaGlyph), facts.Tip)],
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

/// <summary>The header's ⋯: opens the lyrics options card under it (a FlyoutPresenter, light dismiss); a second press closes it.</summary>
internal sealed class WinUiLyricsOptions : Component
{
    /// <summary>The options and the header's button size (the Compact touch floor included).</summary>
    internal sealed record Props(LyricsPaneOptions Options, float Height);

    readonly Ref<NodeHandle> _anchor = new(default);
    readonly Action _toggle;
    OverlayHandle? _handle;
    IOverlayService? _overlay;
    Props? _props;

    public WinUiLyricsOptions() => _toggle = Toggle;

    public override Element Render()
    {
        var p = _props = UseProps<Props>();
        _overlay = UseContext(Overlay.Service);
        return new BoxEl
        {
            Shrink = 0f, OnRealized = h => _anchor.Value = h,
            Children = [ToolTip.Wrap(WinUiPlayerBar.Button(Icons.More, _toggle, true, false, p.Height, p.Height, P.MetaGlyph), Loc.Get(Strings.Player.LyricsOptions))],
        };
    }

    void Toggle()
    {
        if (_handle is { IsOpen: true } open) { open.Close(); return; }
        if (_overlay is not { } overlay || _props is not { } props) return;
        var opened = overlay.Open(() => _anchor.Value, () => Embed.Comp(props, static () => new WinUiLyricsOptionsCard()),
            FlyoutPlacement.BottomEdgeAlignedRight,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
        _handle = opened;
        opened.ClosedAction = () => { if (ReferenceEquals(_handle, opened)) _handle = null; };
    }
}

/// <summary>The lyrics options card: "Lyrics blur" with its value over the slider, then the AI lyrics status line and its "Settings" hyperlink.</summary>
internal sealed class WinUiLyricsOptionsCard : Component
{
    static readonly Slider.SliderOptions s_blurOptions = new()
    {
        Min = 0f, Max = P.LyricsBlurMax, Step = P.LyricsBlurStep, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    static readonly string?[] s_numbers = new string?[(int)P.LyricsBlurMax + 1];

    /// <summary>"Off" at 0, else the number (each built once: a drag allocates nothing).</summary>
    static string BlurWords(float v)
    {
        int n = Math.Clamp((int)MathF.Round(v), 0, (int)P.LyricsBlurMax);
        return n == 0 ? Loc.Get(Strings.Stage.Options.BlurOff) : s_numbers[n] ??= n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public override Element Render()
    {
        var p = UseProps<WinUiLyricsOptions.Props>();
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
                            Ui.Caption(Loc.Get(Strings.Settings.Appearance.LyricsBlur)) with { Weight = P.HeaderTitleWeight, Color = Tok.TextPrimary },
                            Ui.Caption("") with { Text = Prop.Of(() => BlurWords(blur.Value)), Color = Tok.TextSecondary },
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
                Key = "lyrics-options:ai", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children =
                [
                    Ui.Caption(ai) with { Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f, MaxLines = 2, Wrap = TextWrap.Wrap },
                    HyperlinkButton.Create(Loc.Get(Strings.Auth.Settings), o.OpenAiSettings, size: ControlSize.Small).As<BoxEl>()
                        with { Key = "lyrics-options:ai:settings", MinHeight = p.Height, Shrink = 0f },
                ],
            });
        return new BoxEl
        {
            Key = "lyrics-options:card", Width = P.LyricsOptionsW, Direction = 1, Gap = P.LyricsOptionsGap,
            Padding = Edges4.All(P.LyricsOptionsPad), Children = kids.ToArray(),
        };
    }
}

/// <summary>The developer-mode inspector glyph: its own component, so only it subscribes the developer setting.</summary>
internal sealed class WinUiLyricsInspector : Component
{
    public override Element Render()
    {
        var p = UseProps<WinUiLyricsHeader.SparkleProps>();
        var overlay = UseContext(Overlay.Service);
        if (!p.Header.InspectorShown()) return new BoxEl { Visible = false };
        return ToolTip.Wrap(WinUiPlayerBar.Button(Icons.Code, () => p.Header.OpenInspector(overlay), true, false, p.Height, p.Height, P.MetaGlyph),
            Loc.Get(Strings.Player.InspectLyrics));
    }
}

/// <summary>Zune's "next up" rows.</summary>
internal static class WinUiNextUp
{
    /// <summary>The registered template of <see cref="ViewKeys.PanelNextUp"/>.</summary>
    public static Element Template(NextUpProps props, in ViewContext ctx)
    {
        var rows = props.Read();
        var els = new Element[rows.Length];
        for (int k = 0; k < rows.Length; k++)
        {
            var row = rows[k];
            var shape = Shape.Row(TrackRowRules.ArtEdge(P.RowArt, row.Art)) with { MinHeight = P.RowH };
            els[k] = Controls.Surface(row.Data, shape) with { Key = row.Key };
        }
        return new BoxEl { Direction = 1, Children = els };
    }
}

/// <summary>Zune's lyrics peek.</summary>
internal static class WinUiLyricsPeek
{
    static readonly EnterExit FlipIn = new(Dy: P.PeekRowH, Opacity: 0f, Active: true);
    static readonly EnterExit FlipOut = new(Dy: -P.PeekRowH, Opacity: 0f, Active: true);

    /// <summary>The registered template of <see cref="ViewKeys.LyricsPeek"/>. Over the edge-to-edge canvas (<see cref="Deck.OverCanvas"/>) the
    /// card is a plate of the media scope, in its ink.</summary>
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

    static Element Reel(LyricsPeekProps props, CanvasInk ink) => new BoxEl
    {
        Direction = 1, Padding = P.PeekPadding, Gap = P.PeekGap, Corners = CornerRadius4.All(P.CardRadius), Fill = ink.Plate,
        BorderWidth = 1f, BorderColor = ink.Stroke,
        Children =
        [
            new TextEl(Loc.Get(Strings.Player.Lyrics)) { Size = P.PeekCaptionSize, LineHeight = P.PeekCaptionLine, Color = ink.Secondary },
            new BoxEl
            {
                Direction = 1, Height = P.PeekReelH, ClipToBounds = true,
                Children = [Line(props.Active, props.ActiveText, faded: false, ink), Line(props.Peek, props.PeekText, faded: true, ink)],
            },
            new BoxEl
            {
                Shrink = 0f, AlignSelf = FlexAlign.Start, Cursor = CursorId.Hand, OnClick = props.OpenLyrics, Role = AutomationRole.Hyperlink, Focusable = true,
                Children = [new TextEl(Loc.Get(Strings.Theme.Winui.Player.ShowLyrics)) { Size = P.PeekCaptionSize, LineHeight = P.PeekCaptionLine, Color = ink.Accent, HoverColor = ink.Over ? ink.Primary : Tok.AccentTextSecondary }],
            },
        ],
    };

    static Element Note(Action openLyrics, CanvasInk ink) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = P.PeekGap, Height = P.PeekRowH,
        Cursor = CursorId.Hand, OnClick = openLyrics, Role = AutomationRole.Button, Focusable = true,
        Children =
        [
            new BoxEl { Width = P.PeekSpineW, Height = P.PeekRowH, Shrink = 0f, Fill = ink.Tertiary },
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
                Children = [new TextEl(text) { Size = P.PeekLineSize, LineHeight = P.PeekRowH, Weight = faded ? P.PeekNextWeight : P.HeaderTitleWeight, Color = faded ? ink.Secondary : ink.Primary, MaxLines = 1, MinWidth = 0f, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis }],
            }
            : new BoxEl { Height = P.PeekRowH };
        return new BoxEl
        {
            Height = P.PeekRowH, ClipToBounds = true, MinWidth = 0f,
            Children = [inner],
        };
    }
}
