// ── Themes/Zune/Structure/Views/Player/ZunePlayerBar.Parts.cs — the play bar's parts (P5 Z4a) ────────────────────────────────────────
//
// The disc, the plateless glyphs, the tool words, the heart, the reserved tool slots (a face lights or dims by opacity, never moving its
// neighbours), the volume (the mute glyph, the inline 2-px volume line, the popup line), the device word and its picker, the "…" menu, the
// video word with its placement chevron, the podcast steps and the speed word with its panel, the accent "playing on" line and the credits
// line. Zune draws no plates: a latched tool takes the accent ink (Z-PlayBar: shuffle and the device word in the accent).

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

using L = Wavee.Themes.Zune.ZunePlayerLayout;

namespace Wavee.Themes.Zune;

internal static partial class ZunePlayerBar
{
    /// <summary>The props of the speed word.</summary>
    internal sealed record SpeedProps(PlayerBarProps Bar, float Size, float Height);

    /// <summary>The props of the "playing on" line.</summary>
    internal sealed record RemoteProps(PlayerBarProps Bar, float Size, float Line);

    /// <summary>The props of the credits line (<paramref name="Linked"/>: each name is a link; the mini bar's door has none of its own).</summary>
    internal sealed record CreditsProps(PlayerBarProps Bar, float Size, float Line, bool Linked);

    /// <summary>The props of a word or glyph tool that owns an overlay.</summary>
    internal sealed record ToolProps(PlayerBarProps Bar, float Box, float Glyph, float WordSize, bool Popup);

    /// <summary>The props of the "…" menu.</summary>
    internal sealed record MoreProps(PlayerBarProps Bar, OverflowCommand[] Commands, float Box, float Glyph);

    // ── the buttons ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A plateless glyph: <paramref name="ink"/> at rest, the primary ink on hover, the disabled ink when it cannot be pressed.</summary>
    internal static BoxEl Glyph(string glyph, Action onClick, bool enabled, ColorF ink, float box, float size, Action<NodeHandle>? realized = null,
        string? font = null) => new()
    {
        Width = box, Height = box, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        PressScale = R.Motion.ScaleSubtle.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
        OnClick = enabled ? onClick : null, Cursor = enabled ? CursorId.Hand : null, OnRealized = realized,
        Children =
        [
            new TextEl(glyph)
            {
                Size = size, FontFamily = font ?? Tok.Definition.Fonts.Icon,
                Color = enabled ? ink : Tok.TextDisabled, HoverColor = enabled ? Tok.TextPrimary : Tok.TextDisabled,
            },
        ],
    };

    /// <summary>A tool word (lyrics, queue, devices, video): the secondary ink, the accent while latched, the command case; as tall as a target.</summary>
    internal static BoxEl Word(string text, Action onClick, bool enabled, bool latched, float height, float size, Action<NodeHandle>? realized = null)
    {
        ColorF ink = !enabled ? Tok.TextDisabled : latched ? Tok.AccentTextPrimary : Tok.TextSecondary;
        return new BoxEl
        {
            Height = height, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(L.WordPadX, 0f, L.WordPadX, 0f),
            PressScale = R.Motion.ScaleSubtle.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
            OnClick = enabled ? onClick : null, Cursor = enabled ? CursorId.Hand : null, OnRealized = realized,
            Children =
            [
                new TextEl(text)
                {
                    Size = size, Color = ink, HoverColor = !enabled ? ink : latched ? ink : Tok.TextPrimary,
                    Case = R.Case.For(LabelKind.Command), Wrap = TextWrap.NoWrap, MaxLines = 1,
                },
            ],
        };
    }

    /// <summary>A reserved tool: present at the level whatever the playback state; its face lights (opaque, hit-testable) or dims (transparent,
    /// inert) by opacity alone, so lighting a tool moves nothing.</summary>
    static BoxEl Slot(string key, bool lit, Element face) => new()
    {
        Key = key, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = lit ? 1f : 0f, HitTestVisible = lit, Animate = L.FaceMotion,
        Children = [face],
    };

    /// <summary>The round accent disc: play or pause (the user's intent through a load), the glyph in the accent's contrast ink.</summary>
    static BoxEl Disc(PlayerBarProps props, PlayerBarView v, ZuneBarLook look)
    {
        var verb = v.Primary;
        bool enabled = verb != PrimaryVerb.None;
        var verbs = props.Verbs;
        string glyph = !v.Errored && v.PauseGlyph ? L.PauseSolid : L.PlaySolid;
        float solid = MathF.Round(look.DiscGlyph * L.DiscGlyphScale);
        float nudge = glyph == L.PlaySolid ? MathF.Round(solid / L.PlayNudgeDivisor) : 0f;
        Action onClick = verb == PrimaryVerb.Retry ? () => verbs.Retry() : () => verbs.TogglePlay("playerbar.button");
        return new BoxEl
        {
            Width = look.Disc, Height = look.Disc, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(look.Disc), Animate = L.MoveMotion,
            Fill = enabled ? Tok.AccentDefault : Tok.FillSubtleSecondary,
            HoverFill = enabled ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = enabled ? Tok.AccentTertiary : Tok.FillSubtleSecondary,
            BrushTransitionMs = Design.Motion.Faster,
            HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = onClick,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = solid, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextDisabled,
                    Margin = new Edges4(nudge, 0f, 0f, 0f),
                },
            ],
        };
    }

    /// <summary>The heart's reserved slot in the identity column: the accent heart while saved, the primary outline otherwise.</summary>
    static Element HeartSlot(PlayerBarProps props, PlayerBarView v, ZuneBarLook look, Ref<NodeHandle> likeNode)
    {
        var verbs = props.Verbs;
        var icon = v.FaceIsEpisode ? ActionIcons.Resolve(ActionIcons.Save, v.Liked) : ActionIcons.Resolve(ActionIcons.Heart, v.Liked);
        var heart = Glyph(icon.Glyph ?? Icons.Add, () => verbs.ToggleLike(), v.LikeEnabled, v.Liked ? Tok.AccentTextPrimary : Tok.TextPrimary,
            look.Button, look.Glyph, h => likeNode.Value = h) with { BlocksDragArm = true };
        Element face = ToolTip.Wrap(heart, v.FaceIsEpisode
            ? Loc.Get(v.Liked ? Strings.Podcast.Reader.RemoveSaved : Strings.Podcast.Reader.Save)
            : Loc.Get(v.Liked ? Strings.Menu.Saved : Strings.Menu.SaveToLiked));
        return Slot("like", v.LikeLit, face);
    }

    /// <summary>A ±10 s step word (−10 / +10), armed while the rail can be scrubbed.</summary>
    static Element StepWord(PlayerBarProps props, int deltaMs, ZuneBarLook look, bool enabled)
    {
        var verbs = props.Verbs;
        return ToolTip.Wrap(new BoxEl
        {
            Height = look.Button, MinWidth = look.Button, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(L.StepPadX, 0f, L.StepPadX, 0f), PressScale = R.Motion.ScaleSubtle.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
            OnClick = enabled ? () => verbs.SeekBy(deltaMs) : null, Cursor = enabled ? CursorId.Hand : null,
            Children = [new TextEl(deltaMs < 0 ? "−10" : "+10") { Size = look.WordSize, Color = enabled ? Tok.TextPrimary : Tok.TextDisabled, Wrap = TextWrap.NoWrap }],
        }, Loc.Get(deltaMs < 0 ? "player.seekBack10" : "player.seekForward10"));
    }

    // ── the tools ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One tool of the right cluster, in the core's row order.</summary>
    static Element Tool(PlayerBarProps props, PlayerBarView v, ZuneBarLook look, RightSlot slot, Action<string>? begin, IOverlayService overlay,
        Ref<NodeHandle> videoAnchor, Ref<OverlayHandle?> videoMenu)
    {
        var verbs = props.Verbs;
        bool lit = (v.Lit & slot) != 0;
        float box = look.Button, glyph = look.Glyph;
        switch (slot)
        {
            case RightSlot.Shuffle:
                return Slot("shuffle", lit, ToolTip.Wrap(
                    Glyph(Icons.Shuffle, () => verbs.ToggleShuffle(), v.CanTransport, v.Shuffle ? Tok.AccentTextPrimary : Tok.TextSecondary, box, glyph),
                    Loc.Get(Strings.Player.Shuffle)));
            case RightSlot.Repeat:
                return Slot("repeat", lit, ToolTip.Wrap(
                    Glyph(v.RepeatGlyph, () => verbs.CycleRepeat(), v.CanTransport, v.RepeatOn ? Tok.AccentTextPrimary : Tok.TextSecondary, box, glyph),
                    Loc.Get(Strings.Player.Repeat)));
            case RightSlot.Volume:
                return Slot("volume", lit, Embed.Comp(new ToolProps(props, box, glyph, look.WordSize, v.VolumePopup), static () => new ZuneVolumeGlyph()));
            case RightSlot.VolumeSlider:
                return Slot("volume-slider", lit, new BoxEl
                {
                    Margin = L.VolumeMargin, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [Slider.Create(props.Volume, props.SetVolume, new Slider.SliderOptions { ThumbToolTipValueConverter = props.VolumeText },
                        length: look.VolumeW, thickness: L.VolumeThickness, style: LineSlider())],
                });
            case RightSlot.Lyrics:
                return Slot("lyrics", lit, Word(Loc.Get(v.Episode ? Strings.Podcast.Reader.Transcript : Strings.Player.Lyrics), props.OpenLyrics, lit, v.LyricsOpen,
                    box, look.WordSize));
            case RightSlot.Video:
            {
                void OpenMenu()
                {
                    if (videoMenu.Value is { IsOpen: true } open) { open.Close(); return; }
                    ToolTip.CloseOpen();
                    var items = props.VideoPlacementMenu();
                    videoMenu.Value = overlay.Open(
                        () => videoAnchor.Value,
                        () => Slots.View(ViewKeys.VideoPlacementMenu, new VideoPlacementMenuProps { Items = items, Close = () => videoMenu.Value?.Close() }),
                        FlyoutPlacement.TopEdgeAlignedLeft,
                        new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                    videoMenu.Value.ClosedAction = () => videoMenu.Value = null;
                }
                bool on = v.VideoLive && lit;
                return Slot("video", lit, new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, OnRealized = h => videoAnchor.Value = h, OnContextRequested = _ => OpenMenu(),
                    Children =
                    [
                        ToolTip.Wrap(Word(Loc.Get(Strings.Player.Video), () => verbs.ToggleVideo(), lit, on, box, look.WordSize),
                            Loc.Get(v.VideoLive ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo)),
                        ToolTip.Wrap(Glyph(Icons.ChevronDownSmall, OpenMenu, lit, on ? Tok.AccentTextPrimary : Tok.TextSecondary, box, L.ChevronGlyph),
                            Loc.Get(Strings.Player.VideoOptions)),
                    ],
                });
            }
            case RightSlot.Queue:
                return Slot("queue", lit, Word(Loc.Get(Strings.Player.Queue), props.OpenQueue, true, v.QueueOpen, box, look.WordSize));
            case RightSlot.Devices:
                return Slot("devices", lit, Embed.Comp(new ToolProps(props, box, glyph, look.WordSize, Popup: false), static () => new ZuneDevicesWord()));
            case RightSlot.Expand:
                return Slot("expand", lit, ToolTip.Wrap(
                    Glyph(Icons.FullScreen, () => verbs.OpenStage(begin, "bar-expand"), true, Tok.TextSecondary, box, glyph),
                    Loc.Get(Strings.Player.Fullscreen)));
            default:
            {
                // The face is keyed over its command set (the rows are built at open time, so a check mark is always fresh).
                var commands = v.Overflow;
                int key = commands.Length;
                for (int i = 0; i < commands.Length; i++) key = key * 11 + (int)commands[i] + 1;
                return Slot("more", lit, Embed.Comp(new MoreProps(props, commands, box, glyph), static () => new ZuneMoreMenu()) with { Key = "more#" + key });
            }
        }
    }

    /// <summary>The 2-px volume line: the ink at 0.18 under the primary ink, a 12 round thumb (Z-PlayBar's inline line and its popup).</summary>
    internal static Slider.Style LineSlider() => Slider.DefaultStyle with
    {
        TrackHeight = L.VolumeTrack, TrackCornerRadius = 0f,
        RailFill = Tok.TextPrimary with { A = L.VolumeRailA }, RailFillDisabled = Tok.TextPrimary with { A = L.VolumeRailDisabledA },
        ValueFill = Tok.TextPrimary, ValueFillPointerOver = Tok.TextPrimary, ValueFillPressed = Tok.TextSecondary, ValueFillDisabled = Tok.TextDisabled,
        ThumbRingDiameter = L.VolumeThumb, InnerThumbDiameter = L.VolumeThumbInner, ThumbCornerRadius = L.VolumeThumb / 2f,
        ThumbRing = Tok.TextPrimary, ThumbFill = Tok.TextPrimary, ThumbFillPointerOver = Tok.TextPrimary, ThumbFillPressed = Tok.TextSecondary,
        ThumbFillDisabled = Tok.TextDisabled, ThumbBorder = null, ThumbBorderWidth = 0f,
    };
}

/// <summary>The mute glyph. Inline (a level with the volume line): a click mutes. Popup (the levels without it): a click opens the 2-px line
/// upward on a square raised plate. It re-renders only when the mute glyph flips.</summary>
internal sealed class ZuneVolumeGlyph : Component
{
    public override Element Render()
    {
        var p = UseProps<ZunePlayerBar.ToolProps>();
        var props = p.Bar;
        bool muted = props.Muted.Value;
        var overlay = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        bool popup = p.Popup;

        void Click()
        {
            if (!popup) { props.ToggleMute(); return; }
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => new BoxEl
                {
                    Width = ZunePlayerLayout.VolumePopupW, Height = ZunePlayerLayout.VolumePopupH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        Slider.Create(props.Volume, props.SetVolume, new Slider.SliderOptions { Vertical = true, IsThumbToolTipEnabled = false },
                            length: ZunePlayerLayout.VolumePopupRail, thickness: ZunePlayerLayout.VolumeThickness, style: ZunePlayerBar.LineSlider()),
                    ],
                },
                FlyoutPlacement.TopCenter,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return ToolTip.Wrap(ZunePlayerBar.Glyph(muted ? Icons.Mute : Icons.Volume, Click, true, Tok.TextPrimary, p.Box, p.Glyph, h => anchor.Value = h),
            Loc.Get(popup ? Strings.Player.Volume : muted ? Strings.Player.Unmute : Strings.Player.Mute));
    }
}

/// <summary>The device word: in the accent while playback is on another device (the same verdict the "playing on" line reads). It opens the
/// picker (`Player.DevicePicker` in the core's overlay) and opens itself when something asks for the picker after its mount (a toast's
/// "choose device"); the bar mounts exactly one, so a request opens exactly one picker.</summary>
internal sealed class ZuneDevicesWord : Component
{
    public override Element Render()
    {
        var p = UseProps<ZunePlayerBar.ToolProps>();
        var props = p.Bar;
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        bool latched = props.DeviceIsRemote();
        int request = props.DevicePickerRequest.Value;
        var lastRequest = UseRef(request);

        void Toggle() => props.ToggleDevicePicker(overlay, anchor, handle);

        UseEffect(() =>
        {
            if (request == lastRequest.Value) return;
            lastRequest.Value = request;
            if (handle.Value is not { IsOpen: true }) Toggle();
        }, request);

        return ZunePlayerBar.Word(Loc.Get(Strings.Theme.Zune.Player.Devices), Toggle, true, latched, p.Box, p.WordSize, h => anchor.Value = h);
    }
}

/// <summary>"…": the level's overflow rows, built at open time from live reads, upward from the glyph.</summary>
internal sealed class ZuneMoreMenu : Component
{
    public override Element Render()
    {
        var p = UseProps<ZunePlayerBar.MoreProps>();
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        var begin = UseContext(SharedTransition.Begin);
        var commands = p.Commands;
        var props = p.Bar;

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            if (commands.Length == 0) return;
            var items = new List<MenuFlyoutItem>(commands.Length);
            for (int i = 0; i < commands.Length; i++) items.Add(props.OverflowRow(commands[i], begin));
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return ToolTip.Wrap(ZunePlayerBar.Glyph(Icons.More, Toggle, true, Tok.TextSecondary, p.Box, p.Glyph, h => anchor.Value = h), Loc.Get(Strings.Common.More));
    }
}

/// <summary>The speed word ("1.5×"): a press opens the speed panel upward from it.</summary>
internal sealed class ZuneEpisodeSpeed : Component
{
    public override Element Render()
    {
        var p = UseProps<ZunePlayerBar.SpeedProps>();
        var props = p.Bar;
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        UseEffect(() => () => handle.Value?.Close(), DepKey.Empty);
        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = overlay.Open(() => anchor.Value, () => Embed.Comp(props, static () => new ZuneSpeedPanel()),
                FlyoutPlacement.TopEdgeAlignedLeft, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
            handle.Value.ClosedAction = () => handle.Value = null;
        }
        return ToolTip.Wrap(new BoxEl
        {
            Width = ZunePlayerLayout.SpeedW, Height = p.Height, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnRealized = node => anchor.Value = node, OnClick = Toggle,
            Children = [new TextEl(props.RateText(props.EpisodeSpeed.Value)) { Size = p.Size, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Wrap = TextWrap.NoWrap }],
        }, Loc.Get("podcast.reader.speed"));
    }
}

/// <summary>"playback speed" (Z-PlayBar): the heading, the accent line over the 0.5-3 band, the preset words under it.</summary>
internal sealed class ZuneSpeedPanel : Component
{
    Slider.SliderOptions? _options;

    public override Element Render()
    {
        var props = UseProps<PlayerBarProps>();
        var verbs = props.Verbs;
        _options ??= new Slider.SliderOptions
        {
            Min = ZunePlayerLayout.SpeedMin, Max = ZunePlayerLayout.SpeedMax, Step = ZunePlayerLayout.SpeedStep, SmallChange = ZunePlayerLayout.SpeedStep,
            LargeChange = ZunePlayerLayout.SpeedLargeStep, ThumbToolTipValueConverter = props.RateText,
        };
        var value = UseFloatSignal(props.EpisodeSpeed.Peek());
        UseSignalEffect(() => value.SetIfChanged(props.EpisodeSpeed.Value));
        float current = props.EpisodeSpeed.Value;
        var presets = new List<Element>(ZunePlayerLayout.SpeedPresets.Length);
        foreach (float rate in ZunePlayerLayout.SpeedPresets)
        {
            float r = rate;
            presets.Add(ZunePlayerBar.Word(props.RateText(r), () => verbs.SetSpeed(r), true, MathF.Abs(current - r) < ZunePlayerLayout.SpeedStep / 2f,
                ZunePlayerLayout.VolumeThickness, ZunePanelBodies.MetaSize));
        }
        var style = ZunePlayerBar.LineSlider() with { ValueFill = Tok.AccentDefault, ValueFillPointerOver = Tok.AccentDefault };
        return new BoxEl
        {
            Width = ZunePlayerLayout.SpeedPanelW, Direction = 1, Gap = ZunePlayerLayout.SpeedPanelGap, Padding = Edges4.All(ZunePlayerLayout.SpeedPanelPad),
            Children =
            [
                new TextEl(Loc.Get(Strings.Player.SpeedMenu))
                {
                    Size = ZunePlayerLayout.SpeedTitleSize, LineHeight = ZunePlayerLayout.SpeedTitleLine, Weight = ZunePlayerLayout.SpeedTitleWeight,
                    Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading),
                },
                Slider.Create(value, rate => verbs.SetSpeed(rate), _options, length: ZunePlayerLayout.SpeedSliderW, thickness: ZunePlayerLayout.VolumeThickness, style: style),
                new BoxEl { Direction = 0, Justify = FlexJustify.SpaceBetween, Children = presets.ToArray() },
            ],
        };
    }
}

/// <summary>"playing on living room": the label (cased) and the device's name (its own casing) in the accent, above the title; a second
/// door to the same picker.</summary>
internal sealed class ZuneRemoteLine : Component
{
    public override Element Render()
    {
        var p = UseProps<ZunePlayerBar.RemoteProps>();
        var props = p.Bar;
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        string? name = props.RemoteDeviceName();
        if (name is null) return new BoxEl { Height = 0f, HitTestVisible = false };
        ColorF accent = Tok.AccentTextPrimary;
        return new BoxEl
        {
            Height = p.Line, Direction = 0, AlignItems = FlexAlign.Center, Gap = ZunePlayerLayout.FromGap, MinWidth = 0f, ClipToBounds = true,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => props.ToggleDevicePicker(overlay, anchor, handle), OnRealized = h => anchor.Value = h,
            Children =
            [
                new TextEl(Loc.Get(Strings.Theme.Zune.Player.PlayingOn)) { Size = p.Size, LineHeight = p.Line, Color = accent, Case = R.Case.For(LabelKind.Label), Shrink = 0f, Wrap = TextWrap.NoWrap },
                new TextEl(name) { Size = p.Size, LineHeight = p.Line, Color = accent, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            ],
        };
    }
}

/// <summary>The artists under the title (or the episode's show): each name a link where it has a route (the mini bar: plain text, its door
/// is the identity). It re-renders only when its stamp moves; a held face keeps its credits, so the line never empties on a skip.</summary>
internal sealed class ZuneCredits : Component
{
    readonly ZunePlayerBar.CreditsProps? _fixed;

    /// <summary>Mounted through <c>Embed.Comp(props, …)</c>: the props arrive per render.</summary>
    public ZuneCredits() { }

    /// <summary>Mounted as a marquee's content: the props are fixed at mount.</summary>
    public ZuneCredits(ZunePlayerBar.CreditsProps props) => _fixed = props;

    public override Element Render()
    {
        var p = _fixed ?? UseProps<ZunePlayerBar.CreditsProps>();
        var props = p.Bar;
        _ = UseComputed(props.CreditsStamp).Value;
        var credits = props.Credits();
        if (credits.Show is { } show)
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [Name(show, p)] };
        var artists = credits.Artists;
        if (artists.Length == 0) return new BoxEl { Direction = 0, Height = p.Line };
        var kids = new List<Element>(artists.Length * 2);
        for (int i = 0; i < artists.Length; i++)
        {
            var link = artists[i];
            if (link.Text.Length == 0) continue;
            if (kids.Count > 0) kids.Add(new TextEl(", ") { Size = p.Size, LineHeight = p.Line, Color = Tok.TextSecondary, Shrink = 0f });
            kids.Add(Name(link, p));
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = kids.ToArray() };
    }

    static Element Name(PlayerCreditLink link, ZunePlayerBar.CreditsProps p)
    {
        bool nav = p.Linked && link.Navigate is not null;
        var text = new TextEl(link.Text)
        {
            Size = p.Size, LineHeight = p.Line, Color = Tok.TextSecondary, HoverColor = nav ? Tok.TextPrimary : Tok.TextSecondary,
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Key = link.Key + ":" + link.Text, Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
            Cursor = nav ? CursorId.Hand : null, OnClick = nav ? link.Navigate : null,
            Role = nav ? AutomationRole.Hyperlink : AutomationRole.Text,
            Children = [text],
        };
    }
}
