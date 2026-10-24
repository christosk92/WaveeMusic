// ── Themes/Zune/Structure/Views/Video/ZuneStageVideoFullscreen.cs — Zune's `Video.Fullscreen` (P5 Z4c) ────────────────────────────────────
//
// The fullscreen surface's chrome (Z-Video): the 48 bar across the top over a scrim fading to nothing, "video · full screen" at the left
// (13/18, the on-media secondary ink), the track's title in the middle (15, the primary ink), and the way out at the right as a word
// ("exit full screen"). The picture lives in the stay-mounted presenter (`Video.Pip`'s overlay fills the window); the core's surface keeps the
// mode (the OS window goes borderless-fullscreen on its monitor and restores what it found, mount = enter, unmount = exit), the focus scope,
// Escape / F at the root and the never-leave-focus-null guard. This template draws the hover band only; the opacity is on THIS band only, never
// on an ancestor of the hole. Per window tier (`ZuneStageVideoRules.Of`, ZT3): the bar is 48 (56 at Huge, Z-Video-Huge; 72 at Huge 4K),
// its padding the board's (16 / 24 / 48 / 64 / 96 on the left), its type 13 (14, 17) and its words the tier's target tall (40 at Compact).
//
// Not on this view's props: the board's "switch to audio" word (no verb reaches the surface; "turn off video" is in the placement menu), listed
// as a gap.

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

using VR = Wavee.Themes.Zune.ZuneStageVideoRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's fullscreen video chrome.</summary>
internal static class ZuneStageVideoFullscreen
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoFullscreen"/>.</summary>
    public static Element Template(VideoFullscreenProps props, in ViewContext ctx)
    {
        var t = VR.OfFacts(ctx.Tier());
        // Two genuinely different surfaces (Z-Video's overlay, Z-Video-Compact's page), each under its own key: a crossing swaps them under the
        // swap fade.
        if (t.PageForm) return Page(props, t, ctx);
        return new BoxEl
        {
            Key = "zune-video-fs:overlay", Grow = 1f, Direction = 1, HitTestPassThrough = true,
            Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
            Children =
            [
                Bar(props, t),
                // Z-Video's foot: the transport the fullscreen surface owns (the seek line, previous · play · next, the volume and "..."), the
                // on-media strip over its bottom scrim, revealed by the window's idle machine like the presenter's own.
                new BoxEl
                {
                    Grow = 1f, Direction = 1, MinHeight = 0f, HitTestPassThrough = true,
                    Children = props.Transport is { } transport ? [ctx.View(ViewKeys.OnMediaTransport, transport)] : [],
                },
            ],
        };
    }

    /// <summary>The overlay's bar: "video · full screen", the title, then "switch to audio" and the way out, over the top scrim.</summary>
    static Element Bar(VideoFullscreenProps props, ZuneStageVideoLayout t) => new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = MathF.Max(t.BarH, t.WordH), Gap = t.BarGap,
                    Padding = new Edges4(t.BarPadL, 0f, t.BarPadR, 0f),
                    Gradient = Tok.ScrimTop,
                    Opacity = t.ChromeAtRest ? 1f : 0f, HoverOpacity = 1f,
                    HoverDurationMs = Design.Motion.Fast, HoverEasing = Easing.FluentDecelerate,
                    Children =
                    [
                        t.LabelShown
                            ? new TextEl(Loc.Get(Strings.Theme.Zune.Stage.VideoFullScreen))
                            {
                                Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, Case = R.Case.For(LabelKind.Label),
                                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                            }
                            : new BoxEl { Width = 0f, Margin = new Edges4(-t.BarGap, 0f, 0f, 0f) },
                        // Centred (Standard) the title has air either side; led (Small, Huge) it follows the label.
                        t.TitleCentered ? new BoxEl { Grow = 1f, MinWidth = 0f } : new BoxEl { Width = 0f, Margin = new Edges4(-t.BarGap, 0f, 0f, 0f) },
                        // Bound REACTIVELY: the surface outlives a track change. The title keeps its own casing; the kind and the artists follow it in the secondary ink ("Nightcall · official
                        // video · Kavinsky").
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = VR.TitleGap, Shrink = 1f, MinWidth = 0f,
                            Children =
                            [
                                new TextEl(Prop.Of(props.Title))
                                {
                                    Size = t.TitleSize, LineHeight = t.TitleLine, Weight = VR.TitleWeight, Color = Tok.OnMediaPrimary,
                                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                                },
                                new TextEl(Prop.Of(() => props.Title().Length == 0 ? "" : "·  " + Loc.Get(Strings.Theme.Zune.Stage.VideoKind)))
                                {
                                    Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, Case = R.Case.For(LabelKind.Label),
                                    Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f,
                                },
                                // The artists keep their own casing (names are not re-cased).
                                new TextEl(Prop.Of(() => props.Title().Length == 0 || props.Artists().Length == 0 ? "" : "·  " + props.Artists()))
                                {
                                    Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, Case = TextCase.AsWritten,
                                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                                },
                            ],
                        },
                        new BoxEl { Grow = 1f, MinWidth = 0f },
                        props.SwitchToAudio is { } toAudio ? SecondaryWord(Loc.Get(Strings.Theme.Zune.Stage.VideoSwitchToAudio), toAudio, t) : new BoxEl { Width = 0f },
                        Word(Loc.Get(Strings.Player.VideoExitFullScreen), props.Exit, t),
                    ],
                };

    // ══ THE COMPACT PAGE (Z-Video-Compact) ══════════════════════════════════════════════════════════════════════════════

    static readonly Signal<bool> s_shown = new(true);

    /// <summary>The page form: the bar ("⌄ video", the way out, and "switch to audio"), the picture's place (the presenter's box stands there, 16:9
    /// at the window's width: <c>ZuneStageVideoPip</c>), the title over "official video · artists", the transport at rest (the page is no overlay:
    /// nothing to hide), then "play video" and the placement, aspect, quality and speed rows (the "..." menu's rows, laid out as the page's list).</summary>
    static Element Page(VideoFullscreenProps props, ZuneStageVideoLayout t, in ViewContext ctx)
    {
        float width = ctx.Hooks.UseContextSignal(Viewport.Size).Value.Width;
        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = t.BarH, Padding = new Edges4(t.BarPadL, 0f, t.BarPadR, 0f),
                Fill = R.Layer.ShellGround,
                Children =
                [
                    new BoxEl
                    {
                        Height = t.WordH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = VR.TitleGap,
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = props.Exit,
                        Children =
                        [
                            Ui.Icon(Icons.ChevronDown, VR.PageChevron, Tok.OnMediaPrimary),
                            new TextEl(Loc.Get(Strings.Player.Video))
                            {
                                Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1,
                            },
                        ],
                    },
                    new BoxEl { Grow = 1f, MinWidth = 0f },
                    props.SwitchToAudio is { } toAudio ? SecondaryWord(Loc.Get(Strings.Theme.Zune.Stage.VideoSwitchToAudio), toAudio, t) with { Margin = default } : new BoxEl { Width = 0f },
                ],
            },
            // The picture's place: the presenter's box is laid over it (transparent here, so the hole shows).
            new BoxEl { Key = "zune-video-fs:picture", Height = width * VR.PageAspect, Shrink = 0f, HitTestVisible = false },
        };
        var body = new List<Element>(8)
        {
            new TextEl(Prop.Of(props.Title))
            {
                Size = R.Type.Heading("").Size, LineHeight = R.Type.Heading("").LineHeight, Weight = R.Type.Heading("").Weight, Color = Tok.TextPrimary,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            // "official video · Kavinsky": the kind is a word the theme writes (cased as a label), the artists keep their own casing.
            new BoxEl
            {
                Direction = 0, MinWidth = 0f, Margin = new Edges4(0f, VR.PageSubGap, 0f, 0f),
                Children =
                [
                    new TextEl(Loc.Get(Strings.Theme.Zune.Stage.VideoKind))
                    {
                        Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f,
                    },
                    new TextEl(Prop.Of(() => props.Artists().Length == 0 ? "" : " · " + props.Artists()))
                    {
                        Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.TextSecondary, Case = TextCase.AsWritten,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                    },
                ],
            },
        };
        var list = new List<Element>(4);
        if (props.Transport is { } transport)
        {
            // At rest: the page's transport is no overlay over the picture, so the window's idle machine does not hide it.
            body.Add(new BoxEl
            {
                Key = "zune-video-fs:transport", Height = VR.PageTransportH, Shrink = 0f, Direction = 1, Margin = new Edges4(-ZuneOnMediaLayout.Pad, VR.PageBlockGap, -ZuneOnMediaLayout.Pad, 0f),   // the strip's own inset lands its words on the page's
                Children = [ctx.View(ViewKeys.OnMediaTransport, transport with { Visible = s_shown })],
            });
            list.Add(Embed.Comp(transport, static () => new PlacementList()) with { Key = "zune-video-fs:list" });
        }
        kids.Add(new BoxEl
        {
            Grow = 1f, MinHeight = 0f, Direction = 1, Fill = R.Layer.ShellGround,
            Children =
            [
                Ui.ScrollView(new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Padding = new Edges4(VR.PagePad, VR.PageTitleTop, VR.PagePad, VR.PagePad),
                    Children = [.. body, .. list],
                }) with { Grow = 1f, MinHeight = 0f },
            ],
        });
        return new BoxEl
        {
            Key = "zune-video-fs:page", Grow = 1f, Direction = 1, Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
            Children = kids.ToArray(),
        };
    }

    /// <summary>"play video" over the "..." menu's rows as the page's list (Z-Video-Compact): a radio row per placement (the dot, the accent on the
    /// current one), a hairline, then a row per cascade (aspect ratio, quality, playback speed) that opens its rows as a menu, and the trailing
    /// commands ("turn off video"). Built from live reads at mount (choosing another placement leaves this page; a cascade builds its rows when it
    /// opens).</summary>
    sealed class PlacementList : Component
    {
        public override Element Render()
        {
            var transport = UseProps<OnMediaTransportProps>();
            var overlay = UseContext(Overlay.Service);
            var items = transport.MoreMenu();
            var rows = new List<Element>(items.Count + 1)
            {
                R.Type.Heading(Loc.Get(Strings.Theme.Zune.Stage.VideoPlayHeading)) with
                {
                    Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading), Margin = new Edges4(0f, VR.PageBlockGap, 0f, 0f),
                },
            };
            for (int i = 0; i < items.Count; i++) rows.Add(Row(items[i], i, overlay));
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = rows.ToArray() };
        }

        static Element Row(MenuFlyoutItem item, int index, IOverlayService overlay)
        {
            if (item.IsSeparator)
                return new BoxEl { Key = "sep:" + index, Height = 1f, Shrink = 0f, Margin = new Edges4(0f, VR.PageBlockGap / 2f, 0f, VR.PageBlockGap / 2f), Fill = Tok.StrokeDividerDefault };
            bool radio = item.Kind == MenuItemKind.Radio;
            bool on = radio && item.IsChecked;
            var anchor = new NodeHandle[1];
            Action? click = item.Kind == MenuItemKind.SubMenu && item.SubItems is { Count: > 0 } sub
                ? () =>
                {
                    OverlayHandle? h = null;
                    h = overlay.Open(() => anchor[0], () => MenuFlyout.Create(sub, () => h?.Close(), size: OverlayFit.MenuSize),
                        FlyoutPlacement.BottomEdgeAlignedRight,
                        new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
                }
                : item.Invoke;
            var kids = new List<Element>(3);
            if (radio)
                kids.Add(new BoxEl
                {
                    Width = VR.PageDot, Height = VR.PageDot, Shrink = 0f, Corners = CornerRadius4.All(VR.PageDot / 2f), Margin = new Edges4(0f, 0f, VR.PageDotGap, 0f),
                    Fill = on ? Prop.Of(static () => R.Ink.AccentDecor) : (Prop<ColorF>)ColorF.Transparent,
                    BorderWidth = on ? 0f : 1f, BorderColor = Tok.TextSecondary,
                });
            kids.Add(R.Type.DenseTitle(item.Label) with
            {
                Color = on ? Prop.Of(static () => R.Ink.AccentDecor) : (Prop<ColorF>)Tok.TextPrimary, Case = item.Literal ? TextCase.AsWritten : R.Case.For(LabelKind.Command),
                Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
            if (item.AcceleratorText is { Length: > 0 } acc)
                kids.Add(new TextEl(acc) { Size = R.Type.MicroMeta("").Size, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 });
            if (item.Kind == MenuItemKind.SubMenu) kids.Add(Ui.Icon(Icons.ChevronRight, VR.PageChevron, Tok.TextTertiary));
            return new BoxEl
            {
                Key = "row:" + index, Height = VR.PageRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
                Role = radio ? AutomationRole.RadioButton : AutomationRole.Button, Focusable = item.Enabled, IsEnabled = item.Enabled,
                Opacity = item.Enabled ? 1f : ZuneBandRules.DisabledOpacity, AllowFocusOnInteraction = false, Cursor = item.Enabled ? CursorId.Hand : null,
                OnRealized = h => anchor[0] = h, OnClick = click,
                Children = kids.ToArray(),
            };
        }
    }

    /// <summary>"Switch to audio": a word in the on-media secondary ink, beside the way out.</summary>
    static BoxEl SecondaryWord(string text, Action onClick, ZuneStageVideoLayout t) => new BoxEl
    {
        Height = t.WordH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Margin = new Edges4(0f, 0f, VR.WordGap, 0f),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(text) { Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaSecondary, HoverColor = Tok.OnMediaPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };

    /// <summary>The way out: a word, the on-media primary ink, no plate (the hover lifts nothing: it is already the brightest ink).</summary>
    static Element Word(string text, Action onClick, ZuneStageVideoLayout t) => new BoxEl
    {
        Height = t.WordH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(text) { Size = t.LabelSize, LineHeight = t.LabelLine, Color = Tok.OnMediaPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };
}
