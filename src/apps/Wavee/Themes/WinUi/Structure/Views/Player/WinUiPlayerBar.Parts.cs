// ── Themes/WinUi/Structure/Views/Player/WinUiPlayerBar.Parts.cs — the play bar's parts (P6 W4a) ──────────────────────────────────────
//
// Shell-Player's controls in WinUI's own dress: the subtle buttons (transparent at rest, the subtle fill on hover and press, r4; a pane on show
// or a latched route keeps the subtle fill), the 36 accent play circle, the heart, the ±10 s steps (the arrow glyph with "10" inside), the
// reserved controls (a face lights or dims by opacity, never moving its neighbours), the mute button and its flyout slider, the device button
// (an icon, or "This PC" with its name at Huge; the accent ink on a remote output) and its picker, the episode speed menu, the video split, the
// ⋯ menu (the core's rows in the board's order, separators between groups; the Mini bar adds previous and devices), the "playing on" line and
// the credits line.

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

using PL = Wavee.Themes.WinUi.WinUiPlayerLayout;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiPlayerBar
{
    /// <summary>The props of the device button (<paramref name="Labelled"/>: the Huge button with the device's name).</summary>
    internal sealed record DevicesProps(PlayerBarProps Bar, bool Labelled);

    /// <summary>The props of the "playing on" line (<paramref name="Accent"/>: the Mini bar's accent line in place of the artists).</summary>
    internal sealed record RemoteProps(PlayerBarProps Bar, bool Accent);

    /// <summary>The props of the credits line (<paramref name="Linked"/>: each name is a link; the Mini bar's door has none of its own).</summary>
    internal sealed record CreditsProps(PlayerBarProps Bar, bool Linked);

    /// <summary>The props of the mute button (<paramref name="Popup"/>: a level without the inline slider opens it in a flyout).</summary>
    internal sealed record VolumeProps(PlayerBarProps Bar, bool Popup);

    /// <summary>The props of the ⋯ menu (<paramref name="Mini"/>: the Mini bar adds previous and devices, and answers the picker requests).</summary>
    internal sealed record MoreProps(PlayerBarProps Bar, OverflowCommand[] Commands, bool Mini, bool AddPrevious, bool PrevEnabled);

    // ── the buttons ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A subtle button: the glyph in the primary ink (the accent ink while <paramref name="latched"/>), transparent at rest, the subtle
    /// fill on hover and press; <paramref name="plated"/> keeps the subtle fill (a pane on show).</summary>
    internal static BoxEl Button(string glyph, Action onClick, bool enabled, bool latched, float width, float height, float size,
        Action<NodeHandle>? realized = null, string? font = null, bool plated = false)
    {
        ColorF ink = !enabled ? Tok.TextDisabled : latched && !plated ? Tok.AccentTextPrimary : Tok.TextPrimary;
        return new BoxEl
        {
            Width = width, Height = height, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(PL.ButtonCorner),
            Fill = plated && enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = enabled ? Tok.FillSubtleTertiary : ColorF.Transparent,
            BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
            OnClick = enabled ? onClick : null, Cursor = enabled ? CursorId.Hand : null, OnRealized = realized,
            Children = [new TextEl(glyph) { Size = size, FontFamily = font ?? Tok.Definition.Fonts.Icon, Color = ink }],
        };
    }

    /// <summary>A control a level brings or drops: keyed, it fades in and out in place on a crossing.</summary>
    internal static BoxEl Item(string key, Element face) => new()
    {
        Key = key, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Animate = PL.ItemMotion,
        Children = [face],
    };

    /// <summary>A reserved control: present at the level whatever the playback state; its face lights (opaque, hit-testable) or dims
    /// (transparent, inert) by opacity alone, so lighting it moves nothing. It fades in and out on a crossing like any other.</summary>
    internal static BoxEl Lit(string key, bool lit, Element face) => new()
    {
        Key = key, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Animate = PL.ItemMotion,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Opacity = lit ? 1f : 0f, HitTestVisible = lit, Animate = PL.FaceMotion,
                Children = [face],
            },
        ],
    };

    /// <summary>The 36 accent play circle: play or pause (the user's intent through a load), the glyph in the accent's contrast ink.</summary>
    static BoxEl PlayCircle(PlayerBarProps props, PlayerBarView v, float size = PL.Play)
    {
        var verb = v.Primary;
        bool enabled = verb != PrimaryVerb.None;
        var verbs = props.Verbs;
        string glyph = !v.Errored && v.PauseGlyph ? PL.PauseSolid : PL.PlaySolid;
        float nudge = glyph == PL.PlaySolid ? MathF.Round(PL.PlayGlyph / PL.PlayNudgeDivisor) : 0f;
        Action onClick = verb == PrimaryVerb.Retry ? () => verbs.Retry() : () => verbs.TogglePlay("playerbar.button");
        return new BoxEl
        {
            Width = size, Height = size, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(size), Animate = PL.MoveMotion,
            Fill = enabled ? Tok.AccentDefault : Tok.AccentDisabled,
            HoverFill = enabled ? Tok.AccentSecondary : Tok.AccentDisabled,
            PressedFill = enabled ? Tok.AccentTertiary : Tok.AccentDisabled,
            BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = onClick,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = PL.PlayGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextOnAccentDisabled,
                    Margin = new Edges4(nudge, 0f, 0f, 0f),
                },
            ],
        };
    }

    /// <summary>The heart's reserved slot: the filled heart while saved, the outline otherwise (both in the primary ink, Shell-Player).</summary>
    static Element HeartSlot(PlayerBarProps props, PlayerBarView v, Ref<NodeHandle> likeNode)
    {
        var verbs = props.Verbs;
        var icon = v.FaceIsEpisode ? ActionIcons.Resolve(ActionIcons.Save, v.Liked) : ActionIcons.Resolve(ActionIcons.Heart, v.Liked);
        var heart = Button(icon.Glyph ?? Icons.Add, () => verbs.ToggleLike(), v.LikeEnabled, false, PL.ToolBox, PL.ToolBox, PL.Glyph,
            h => likeNode.Value = h) with { BlocksDragArm = true };
        Element face = ToolTip.Wrap(heart, v.FaceIsEpisode
            ? Loc.Get(v.Liked ? Strings.Podcast.Reader.RemoveSaved : Strings.Podcast.Reader.Save)
            : Loc.Get(v.Liked ? Strings.Menu.Saved : Strings.Menu.SaveToLiked));
        return Lit(PL.LikeKey, v.LikeLit, face);
    }

    /// <summary>A ±10 s step: the curved arrow with "10" in it (Shell-Player), armed while the rail can be scrubbed.</summary>
    static Element StepButton(PlayerBarProps props, int deltaMs, bool enabled, float height)
    {
        var verbs = props.Verbs;
        ColorF ink = enabled ? Tok.TextPrimary : Tok.TextDisabled;
        var button = Button(deltaMs < 0 ? PL.StepBackGlyph : PL.StepForwardGlyph, () => verbs.SeekBy(deltaMs), enabled, false, PL.TransportW, height, PL.StepGlyph);
        button = button with
        {
            ZStack = true,
            Children =
            [
                new BoxEl
                {
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(deltaMs < 0 ? PL.StepBackGlyph : PL.StepForwardGlyph) { Size = PL.StepGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = ink }],
                },
                new BoxEl
                {
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = PL.StepDigitsPadding, HitTestVisible = false,
                    Children = [new TextEl(PL.StepDigits) { Size = PL.StepDigitsSize, Weight = PL.SpeedWeight, Color = ink, Wrap = TextWrap.NoWrap }],
                },
            ],
        };
        return ToolTip.Wrap(button, Loc.Get(deltaMs < 0 ? Strings.Player.SeekBack10 : Strings.Player.SeekForward10));
    }

    // ── the tools ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One plain tool: lyrics (the transcript for an episode), queue, mute, full screen.</summary>
    static Element Tool(PlayerBarProps props, PlayerBarView v, RightSlot slot, Action<string>? begin)
    {
        var verbs = props.Verbs;
        bool lit = (v.Lit & slot) != 0;
        switch (slot)
        {
            case RightSlot.Lyrics:
                return Lit(PL.LyricsKey, lit, ToolTip.Wrap(
                    Button(PL.LyricsGlyph, props.OpenLyrics, lit, v.LyricsOpen, PL.ToolBox, PL.ToolBox, PL.Glyph, plated: v.LyricsOpen),
                    Loc.Get(v.Episode ? Strings.Podcast.Reader.Transcript : Strings.Player.Lyrics)));
            case RightSlot.Queue:
                return Lit(PL.QueueKey, lit, ToolTip.Wrap(
                    Button(PL.QueueGlyph, props.OpenQueue, true, v.QueueOpen, PL.ToolBox, PL.ToolBox, PL.Glyph, plated: v.QueueOpen),
                    Loc.Get(Strings.Player.Queue)));
            case RightSlot.Volume:
                return Lit(PL.VolumeKey, lit, Embed.Comp(new VolumeProps(props, v.VolumePopup), static () => new WinUiVolumeButton())
                    with { Key = v.VolumePopup ? "volume-popup" : "volume-inline" });
            default:
                return Lit(PL.ExpandKey, lit, ToolTip.Wrap(
                    Button(PL.ExpandGlyph, () => verbs.OpenStage(begin, "bar-expand"), true, false, PL.ToolBox, PL.ToolBox, PL.Glyph),
                    Loc.Get(Strings.Player.Fullscreen)));
        }
    }

    /// <summary>The video split (Medium up, while the track has a video): the switch (the film glyph; latched while the video is on) and the
    /// chevron that opens "Where to play the video", in one bordered 32 control.</summary>
    static Element VideoSplit(PlayerBarProps props, PlayerBarView v, bool lit, IOverlayService overlay, Ref<NodeHandle> anchor, Ref<OverlayHandle?> menu)
    {
        var verbs = props.Verbs;
        void OpenMenu()
        {
            if (menu.Value is { IsOpen: true } open) { open.Close(); return; }
            ToolTip.CloseOpen();
            var items = props.VideoPlacementMenu();
            menu.Value = overlay.Open(
                () => anchor.Value,
                () => Slots.View(ViewKeys.VideoPlacementMenu, new VideoPlacementMenuProps { Items = items, Close = () => menu.Value?.Close() }),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            menu.Value.ClosedAction = () => menu.Value = null;
        }
        bool on = v.VideoLive && lit;
        return new BoxEl
        {
            Height = PL.SplitH, Direction = 0, AlignItems = FlexAlign.Center, Corners = CornerRadius4.All(PL.ButtonCorner), ClipToBounds = true,
            Fill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
            OnRealized = h => anchor.Value = h, OnContextRequested = _ => OpenMenu(),
            Children =
            [
                ToolTip.Wrap(Button(Icons.Movie, () => verbs.ToggleVideo(), lit, on, PL.SplitMainW, PL.SplitInnerH, PL.Glyph),
                    Loc.Get(v.VideoLive ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo)),
                new BoxEl { Width = 1f, Height = PL.SplitInnerH - 2f * PL.SplitDividerInset, Shrink = 0f, Fill = Tok.StrokeDividerDefault },
                ToolTip.Wrap(Button(Icons.ChevronDownSmall, OpenMenu, lit, false, PL.SplitChevronW, PL.SplitInnerH, PL.SplitChevronGlyph),
                    Loc.Get(Strings.Theme.Winui.Player.WherePlayVideo)),
            ],
        };
    }

    /// <summary>"⋯": the level's overflow rows in the board's order. Keyed over its command set (the rows are built at open time, so a check
    /// mark is always fresh).</summary>
    static Element MoreMenu(PlayerBarProps props, PlayerBarView v, bool mini)
    {
        var commands = v.Overflow;
        bool addPrevious = mini && v.OwnsTransport && !v.Episode && v.ShowPrevNext;
        int key = commands.Length * 2 + (addPrevious ? 1 : 0);
        for (int i = 0; i < commands.Length; i++) key = key * 11 + (int)commands[i] + 1;
        return Embed.Comp(new MoreProps(props, commands, mini, addPrevious, v.PrevEnabled), static () => new WinUiMoreMenu()) with { Key = "more#" + key };
    }
}

/// <summary>The mute button. Inline (a level with the slider): a click mutes. Popup (Compact and Medium): a click opens the slider in a flyout
/// above it. It re-renders only when the mute glyph flips.</summary>
internal sealed class WinUiVolumeButton : Component
{
    public override Element Render()
    {
        var p = UseProps<WinUiPlayerBar.VolumeProps>();
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
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = PL.ToolGap, Padding = PL.VolumeFlyoutPadding,
                    Children =
                    [
                        WinUiPlayerBar.Button(props.Muted.Peek() ? Icons.Mute : Icons.Volume, props.ToggleMute, true, false, PL.ToolBox, PL.ToolBox, PL.Glyph),
                        Slider.Create(props.Volume, props.SetVolume, new Slider.SliderOptions { ThumbToolTipValueConverter = props.VolumeText },
                            length: PL.VolumeFlyoutW, thickness: PL.VolumeThickness),
                    ],
                },
                FlyoutPlacement.TopCenter,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return ToolTip.Wrap(WinUiPlayerBar.Button(muted ? Icons.Mute : Icons.Volume, Click, true, false, PL.ToolBox, PL.ToolBox, PL.Glyph, h => anchor.Value = h),
            Loc.Get(popup ? Strings.Player.Volume : muted ? Strings.Player.Unmute : Strings.Player.Mute));
    }
}

/// <summary>The device button: the glyph (Compact to Full) or the glyph and the device's name (Huge: "This PC"), in the accent ink with the
/// subtle fill while playback is on another device. It opens the picker (`Player.DevicePicker` in the core's overlay) and opens itself when
/// something asks for the picker after its mount (a toast's "Choose device"); the bar mounts exactly one, so a request opens exactly one.</summary>
internal sealed class WinUiDevicesButton : Component
{
    /// <summary>The labelled button's widest (its name trims past it).</summary>
    public const float LabelledW = 160f;

    public override Element Render()
    {
        var p = UseProps<WinUiPlayerBar.DevicesProps>();
        var props = p.Bar;
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        bool remote = props.DeviceIsRemote();
        int request = props.DevicePickerRequest.Value;
        var lastRequest = UseRef(request);

        void Toggle() => props.ToggleDevicePicker(overlay, anchor, handle);

        UseEffect(() =>
        {
            if (request == lastRequest.Value) return;
            lastRequest.Value = request;
            if (handle.Value is not { IsOpen: true }) Toggle();
        }, request);

        ColorF ink = remote ? Tok.AccentTextPrimary : Tok.TextPrimary;
        if (!p.Labelled)
            return ToolTip.Wrap(WinUiPlayerBar.Button(PL.DevicesGlyph, Toggle, true, remote, PL.ToolBox, PL.ToolBox, PL.Glyph, h => anchor.Value = h, plated: remote)
                with { Children = [new TextEl(PL.DevicesGlyph) { Size = PL.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = ink }] },
                Loc.Get(Strings.Theme.Winui.Player.Devices));
        string name = remote ? props.RemoteDeviceName() ?? Loc.Get(Strings.Theme.Winui.Player.ThisPc) : Loc.Get(Strings.Theme.Winui.Player.ThisPc);
        return ToolTip.Wrap(new BoxEl
        {
            Height = PL.DeviceH, MaxWidth = LabelledW, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = PL.DeviceGap,
            Padding = new Edges4(PL.DevicePadX, 0f, PL.DevicePadX, 0f), Corners = CornerRadius4.All(PL.ButtonCorner),
            Fill = remote ? Tok.FillSubtleSecondary : ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = Toggle, OnRealized = h => anchor.Value = h,
            Children =
            [
                new TextEl(PL.DevicesGlyph) { Size = PL.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = ink, Shrink = 0f },
                new TextEl(name) { Size = PL.DeviceText, Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            ],
        }, Loc.Get(Strings.Theme.Winui.Player.Devices));
    }
}

/// <summary>"⋯": the rows the bar no longer shows, built at open time from live reads, upward from the glyph, in the board's order with a
/// separator between groups. The Mini bar adds Previous (its level keeps the core's pair: next is on the bar) and Devices (its name as the
/// row's hint), and answers the picker requests (the device button is not mounted there).</summary>
internal sealed class WinUiMoreMenu : Component
{
    public override Element Render()
    {
        var p = UseProps<WinUiPlayerBar.MoreProps>();
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var picker = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        var begin = UseContext(SharedTransition.Begin);
        var props = p.Bar;
        int request = p.Mini ? props.DevicePickerRequest.Value : 0;
        var lastRequest = UseRef(request);

        void Devices() => props.ToggleDevicePicker(overlay, anchor, picker);

        UseEffect(() =>
        {
            if (!p.Mini || request == lastRequest.Value) return;
            lastRequest.Value = request;
            if (picker.Value is not { IsOpen: true }) Devices();
        }, request);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var rows = new List<(int Rank, MenuFlyoutItem Item)>(p.Commands.Length + 2);
            foreach (var command in p.Commands)
            {
                var item = props.OverflowRow(command, begin);
                if (command == OverflowCommand.FullScreen) item = item with { AcceleratorText = PL.FullScreenAccelerator };
                string? glyph = command switch
                {
                    OverflowCommand.Lyrics => PL.LyricsGlyph, OverflowCommand.Queue => PL.QueueGlyph, OverflowCommand.FullScreen => PL.ExpandGlyph, _ => null,
                };
                if (glyph is not null) item = item with { Icon = glyph };
                rows.Add((PL.MenuRank(command), item));
            }
            if (p.AddPrevious)
                rows.Add((PL.MenuRank(OverflowCommand.Previous),
                    new MenuFlyoutItem(Loc.Get(Strings.Player.Previous), Icons.Previous, p.PrevEnabled, () => props.Verbs.Previous())));
            if (p.Mini)
            {
                string device = props.RemoteDeviceName() ?? Loc.Get(Strings.Theme.Winui.Player.ThisPc);
                rows.Add((PL.DevicesRank, new MenuFlyoutItem(Loc.Get(Strings.Theme.Winui.Player.Devices), PL.DevicesGlyph, true, Devices) { AcceleratorText = device }));
            }
            if (rows.Count == 0) return;
            rows.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));
            var items = new List<MenuFlyoutItem>(rows.Count + 3);
            int group = PL.MenuGroup(rows[0].Rank);
            foreach (var (rank, item) in rows)
            {
                if (PL.MenuGroup(rank) != group) { items.Add(MenuFlyoutItem.Separator); group = PL.MenuGroup(rank); }
                items.Add(item);
            }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.TopEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return ToolTip.Wrap(WinUiPlayerBar.Button(Icons.More, Toggle, true, false, PL.ToolBox, PL.ToolBox, PL.Glyph, h => anchor.Value = h),
            Loc.Get(Strings.Common.More));
    }
}

/// <summary>The episode speed button ("1.0×", Caption Strong): a press opens the rates as a radio menu upward from it.</summary>
internal sealed class WinUiEpisodeSpeed : Component
{
    public override Element Render()
    {
        var props = UseProps<PlayerBarProps>();
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        float rate = props.EpisodeSpeed.Value;
        UseEffect(() => () => handle.Value?.Close(), DepKey.Empty);
        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            float current = props.EpisodeSpeed.Peek();
            var items = new List<MenuFlyoutItem>(PL.SpeedPresets.Length);
            foreach (float preset in PL.SpeedPresets)
            {
                float r = preset;
                items.Add(MenuFlyoutItem.RadioItem(props.RateText(r), MathF.Abs(current - r) < 0.01f, () => props.Verbs.SetSpeed(r)));
            }
            handle.Value = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(items, () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.TopEdgeAlignedLeft, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }
        return ToolTip.Wrap(new BoxEl
        {
            Width = PL.SpeedW, Height = PL.SpeedH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(PL.ButtonCorner), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnRealized = node => anchor.Value = node, OnClick = Toggle,
            Children = [new TextEl(props.RateText(rate)) { Size = PL.SpeedText, Weight = PL.SpeedWeight, Color = Tok.TextPrimary, Wrap = TextWrap.NoWrap }],
        }, Loc.Get(Strings.Player.SpeedMenu));
    }
}

/// <summary>"Playing on Living room speaker": the device glyph and the words (the secondary ink under the artists from Medium; the accent ink in
/// place of the artists in the Mini bar); a second door to the same picker.</summary>
internal sealed class WinUiRemoteLine : Component
{
    public override Element Render()
    {
        var p = UseProps<WinUiPlayerBar.RemoteProps>();
        var props = p.Bar;
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        string? name = props.RemoteDeviceName();
        if (name is null) return new BoxEl { Height = 0f, HitTestVisible = false };
        ColorF ink = p.Accent ? Tok.AccentTextPrimary : Tok.TextSecondary;
        return new BoxEl
        {
            Height = PL.SubLine, Direction = 0, AlignItems = FlexAlign.Center, Gap = PL.RemoteGap, MinWidth = 0f, ClipToBounds = true,
            Cursor = p.Accent ? null : CursorId.Hand, Role = p.Accent ? AutomationRole.Text : AutomationRole.Button,
            Focusable = !p.Accent, AllowFocusOnInteraction = false,
            OnClick = p.Accent ? null : () => props.ToggleDevicePicker(overlay, anchor, handle), OnRealized = h => anchor.Value = h,
            Children =
            [
                new TextEl(PL.DevicesGlyph) { Size = PL.RemoteGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = ink, Shrink = 0f },
                new TextEl(Strings.Player.PlayingOn(name))
                {
                    Size = PL.SubSize, LineHeight = PL.SubLine, Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                    MinWidth = 0f, Shrink = 1f,
                },
            ],
        };
    }
}

/// <summary>The artists under the title (or the episode's show): each name a link where it has a route (the Mini bar: plain text, its door is
/// the identity). It re-renders only when its stamp moves; a held face keeps its credits, so the line never empties on a skip.</summary>
internal sealed class WinUiCredits : Component
{
    public override Element Render()
    {
        var p = UseProps<WinUiPlayerBar.CreditsProps>();
        var props = p.Bar;
        _ = UseComputed(props.CreditsStamp).Value;
        var credits = props.Credits();
        if (credits.Show is { } show)
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [Name(show, p)] };
        var artists = credits.Artists;
        if (artists.Length == 0) return new BoxEl { Direction = 0, Height = PL.SubLine };
        var kids = new List<Element>(artists.Length * 2);
        for (int i = 0; i < artists.Length; i++)
        {
            var link = artists[i];
            if (link.Text.Length == 0) continue;
            if (kids.Count > 0) kids.Add(new TextEl(", ") { Size = PL.SubSize, LineHeight = PL.SubLine, Color = Tok.TextSecondary, Shrink = 0f });
            kids.Add(Name(link, p));
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = kids.ToArray() };
    }

    static Element Name(PlayerCreditLink link, WinUiPlayerBar.CreditsProps p)
    {
        bool nav = p.Linked && link.Navigate is not null;
        var text = new TextEl(link.Text)
        {
            Size = PL.SubSize, LineHeight = PL.SubLine, Color = Tok.TextSecondary, HoverColor = nav ? Tok.TextPrimary : Tok.TextSecondary,
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
