// ── Themes/Classic/Structure/Views/Panel/ClassicNowPlayingSheet.cs — the Compact now-playing sheet's column under the art ─────────────
//
// C-NowPlaying-Compact: the sheet's own HEADER under the grab strip (`ClassicSheetHeader`, the head the sheet drags by), then under the art (the
// hero tile, at the side `ClassicPanelTiers.SheetArtSide` leaves it) what the mini bar it covers would have, and more:
//
//   header      the close chevron (40) · "Playing from album" over the source's name (12/16, centred, a link to its page) · "⋯" (40, the
//               now-playing track's menu: the same seam the bar's right-click opens), 48 tall; the developer-only style gear beside the "⋯"
//   identity    the title (20/28, one line) over the artists (14/20, links), the heart (40) beside them
//   seek        the bar's own seek rail in its 32-tall lane, the elapsed and remaining times under it
//   transport   shuffle (40) · previous (48) · the play disc (56) · next (48) · repeat (40), spread across the sheet
//   doors       Lyrics · Devices · Queue (48, three equal columns): Lyrics and Queue open that body in the same sheet, Devices the bar's own
//               device picker, named for the device playback is on ("This computer") and inked in the accent while it is another device
//   the rest    the lyrics peek and the docked body's sections (about the artist, the cities, merch, "Next up")
//
// Every block above the rest has a fixed height (`ClassicPanelTiers.SheetBelowArt`, the head `SheetHeadH`), so the art's side arithmetic and the
// column agree, and nothing in the header or the doors changes a height when the source or the device changes. The transport, the header and the
// Devices door are their own components: a playback, context or roster edge re-renders them, not the column. Their facts and verbs are the core's
// (`PanelProps.Transport`; the source, the device and the picker toggle are contract 1.22).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The now-playing sheet's column under the art (see the file header).</summary>
internal sealed class ClassicNowPlayingSheet : Component
{
    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var np = props.NowPlaying;
        var track = np.NowTrack();
        int trackSlot = track.Slot;
        var nowArtist = Rail.NowPlayingArtist(track);
        int artistSlot = nowArtist.Slot;

        // The panel is not a page and never mounts on a route: its demand is keyed on the PLAYING track (as the docked body's).
        UseEffect(() => np.DemandNowPlaying(trackSlot, artistSlot), DepKey.From(trackSlot, artistSlot));

        var kids = new List<Element>(5);
        if (track.IsValid)
        {
            np.SubscribeEntities();
            kids.Add(Identity(np, track));
        }
        else kids.Add(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.NothingPlaying), subtitle: ""));
        if (props.Transport is { } transport) kids.Add(Embed.Comp(transport, static () => new ClassicSheetTransport()));
        if (props.ShowMode is { } show) kids.Add(ClassicPanel.SheetDoors(show, props.Transport));
        if (track.IsValid)
        {
            kids.Add(new BoxEl
            {
                Direction = 1, Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
                Children = [np.LyricsPeek() with { Key = "npv-lyrics:" + track.Uri.Text }],
            });
            kids.Add(ClassicNowPlayingBody.Sections(np, track, nowArtist));
        }
        return new BoxEl { Direction = 1, Children = kids.ToArray() };
    }

    /// <summary>The title over the artists, the heart beside them: one fixed-height row.</summary>
    static Element Identity(NowPlayingHeroProps np, Track track)
    {
        string uri = track.Uri.Text;
        string title = track.Title;
        var lines = new List<Element>(2)
        {
            new TextEl(title)
            {
                Key = ClassicPanelTiers.SheetTitleKey,
                Size = ClassicPanelTiers.SheetTitleSize, LineHeight = ClassicPanelTiers.SheetTitleLine, Weight = 600, Color = Tok.TextPrimary,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (ClassicNowPlayingHero.ArtistLinks(np, track, ClassicPanelTiers.SheetArtistSize, ClassicPanelTiers.SheetArtistLine) is { } artists) lines.Add(artists);
        return new BoxEl
        {
            Key = ClassicPanelTiers.SheetMetaKey,
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Height = ClassicPanelTiers.SheetTitleLine + ClassicPanelTiers.SheetMetaGap + ClassicPanelTiers.SheetArtistLine,
            Margin = new Edges4(ClassicPanelTiers.SheetInset, ClassicPanelTiers.SheetMetaTop, ClassicPanelTiers.SheetInset - Spacing.S, 0f),
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicPanelTiers.SheetMetaGap, Children = lines.ToArray() },
                Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = ClassicPanelTiers.SheetHeartGlyph, Box = ClassicPanelTiers.SheetHeartBox })
                    with { Key = "save:" + uri },
            ],
        };
    }
}

/// <summary>The sheet's seek block and transport row. Reads the facts in its own render, so a playback edge re-renders only this.</summary>
internal sealed class ClassicSheetTransport : Component
{
    public override Element Render()
    {
        var t = UseProps<PanelTransportProps>();
        var f = t.Facts();
        var verbs = t.Verbs;
        return new BoxEl
        {
            Key = ClassicPanelTiers.SheetTransportKey, Direction = 1, Shrink = 0f,
            Padding = new Edges4(ClassicPanelTiers.SheetInset, ClassicPanelTiers.SheetSeekTop, ClassicPanelTiers.SheetInset, 0f),
            Children =
            [
                new BoxEl { Height = ClassicPanelTiers.SheetSeekRailH, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center, Children = [t.SeekBar()] },
                new BoxEl
                {
                    Height = ClassicPanelTiers.SheetTimesH, Shrink = 0f, Margin = new Edges4(0f, ClassicPanelTiers.SheetSeekGap, 0f, 0f),
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                    Children = [t.TimeText(false), t.TimeText(true)],
                },
                new BoxEl
                {
                    Height = ClassicPanelTiers.SheetTransportH, Shrink = 0f, Margin = new Edges4(0f, ClassicPanelTiers.SheetTransportTop, 0f, 0f),
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                    Children =
                    [
                        ToolTip.Wrap(Button(Icons.Shuffle, verbs.ToggleShuffle, f.CanTransport, f.Shuffle, ClassicPanelTiers.SheetSatelliteBox,
                            ClassicPanelTiers.SheetSatelliteGlyph), Loc.Get(Strings.Player.Shuffle)),
                        ToolTip.Wrap(Button(Icons.Previous, verbs.Previous, f.PrevEnabled, false, ClassicPanelTiers.SheetSkipBox, ClassicPanelTiers.SheetSkipGlyph),
                            Loc.Get(Strings.Player.Previous)),
                        Play(f.Playing, f.PrimaryEnabled, verbs),
                        ToolTip.Wrap(Button(Icons.Next, verbs.Next, f.NextEnabled, false, ClassicPanelTiers.SheetSkipBox, ClassicPanelTiers.SheetSkipGlyph),
                            Loc.Get(Strings.Player.Next)),
                        ToolTip.Wrap(Button(f.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, verbs.CycleRepeat, f.CanTransport, f.RepeatOn,
                            ClassicPanelTiers.SheetSatelliteBox, ClassicPanelTiers.SheetSatelliteGlyph), Loc.Get(Strings.Player.Repeat)),
                    ],
                },
            ],
        };
    }

    /// <summary>A glyph button: the accent glyph on the subtle plate while latched (shuffle, repeat), unpainted at rest.</summary>
    static BoxEl Button(string glyph, Action onClick, bool enabled, bool latched, float box, float glyphSize)
    {
        ColorF ink = !enabled ? Tok.TextDisabled : latched ? Tok.AccentDefault : Tok.TextPrimary;
        ColorF plate = latched && enabled ? Tok.FillSubtleSecondary : ColorF.Transparent;
        return new BoxEl
        {
            Width = box, Height = box, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Fill = plate, HoverFill = enabled ? Tok.FillSubtleTertiary : plate, PressedFill = plate, BrushTransitionMs = Design.Motion.Faster,
            HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
            OnClick = enabled ? onClick : null, Cursor = enabled ? CursorId.Hand : null,
            Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = ink }],
        };
    }

    /// <summary>The 56 play disc in the accent (the bar's primary, larger).</summary>
    static Element Play(bool playing, bool enabled, IPlayerVerbs verbs) => new BoxEl
    {
        Width = ClassicPanelTiers.SheetPlayDisc, Height = ClassicPanelTiers.SheetPlayDisc, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(ClassicPanelTiers.SheetPlayDisc),
        Fill = enabled ? Tok.AccentDefault : Tok.FillSubtleSecondary,
        HoverFill = enabled ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
        PressedFill = enabled ? Tok.AccentTertiary : Tok.FillSubtleSecondary,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
        Cursor = enabled ? CursorId.Hand : null, OnClick = () => verbs.TogglePlay("sheet.button"),
        Children =
        [
            new TextEl(playing ? Icons.Pause : Icons.Play)
            {
                Size = ClassicPanelTiers.SheetPlayGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextDisabled,
            },
        ],
    };
}

/// <summary>The now-playing sheet's own header (C-NowPlaying-Compact): the close chevron, "Playing from album" over the source's name (a link to its
/// page, which closes the sheet first: the page must not change behind a modal surface), and the "⋯" that opens the now-playing track's menu (as a
/// context sheet at Compact, the window band the sheet exists in). A fixed 48-tall row: the source's two lines, its one-line fallback and the
/// developer-only gear change no height. Reads the source in its own render, so a context change re-renders only this.</summary>
internal sealed class ClassicSheetHeader : Component
{
    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var overlay = UseContext(Overlay.Service);
        var transport = props.Transport;
        var source = transport?.Source is { } readSource ? readSource() : new PanelSourceFacts("", "", null);
        bool hasTrack = props.NowPlaying.NowTrack().IsValid;
        bool styleVisible = Rail.PlayerPrefs.StyleChoiceVisible();     // the style gear is developer-only (#199)
        var close = props.Close;

        var kids = new List<Element>(4)
        {
            ToolTip.Wrap(ClassicPanel.SheetGlyphButton(Icons.ChevronDown, close, enabled: true) with { Key = ClassicPanelTiers.SheetCloseKey },
                Loc.Get(Strings.Common.Close)),
            Source(source, close),
        };
        if (styleVisible && hasTrack) kids.Add(ClassicHeroHeaderRow.StyleGear(props.NowPlaying));
        if (transport is { } t)
        {
            // The ONE now-playing menu seam the bar's right-click and the stage open (`IPlayerVerbs.NowPlayingMenu`); the click re-enters the
            // context funnel, so at Compact it opens as the context sheet (`ContextSheets`).
            var verbs = t.Verbs;
            var more = ClassicPanel.SheetGlyphButton(Icons.More, null, enabled: hasTrack) with
            {
                Key = ClassicPanelTiers.SheetMoreKey, ClickRequestsContext = hasTrack,
            };
            kids.Add(ToolTip.Wrap(hasTrack ? more.WithContextMenu(overlay, () => verbs.NowPlayingMenu()) : more, Loc.Get(Strings.Common.More)));
        }
        else kids.Add(new BoxEl { Width = ClassicPanelTiers.SheetHeaderButton, Height = ClassicPanelTiers.SheetHeaderButton, Shrink = 0f });   // keeps the source centred

        return new BoxEl
        {
            Key = ClassicPanelTiers.SheetHeaderKey,
            Direction = 0, Height = ClassicPanelTiers.SheetHeaderH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = ClassicPanelTiers.SheetHeaderGap,
            Padding = new Edges4(ClassicPanelTiers.SheetHeaderInset, 0f, ClassicPanelTiers.SheetHeaderInset, 0f),
            Children = kids.ToArray(),
        };
    }

    /// <summary>"Playing from album" over the source's name, centred and ellipsised; "Now playing" alone where playback names no source. A link to the
    /// source's page where it has one.</summary>
    static Element Source(PanelSourceFacts source, Action close)
    {
        Element[] lines = source.HasSource
            ? [
                ClassicPanel.CentredLine(Line(source.PlayingFrom, 400, Tok.TextSecondary)),
                ClassicPanel.CentredLine(Line(source.Name, 700, Tok.TextPrimary)),
              ]
            : [ClassicPanel.CentredLine(Line(Loc.Get(Strings.Player.NowPlaying), 600, Tok.TextSecondary))];
        var box = new BoxEl
        {
            Key = ClassicPanelTiers.SheetSourceKey,
            // 40 tall at least: the link is a Compact target like the two buttons beside it (its two 16 lines are 32).
            Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = ClassicPanelTiers.SheetHeaderButton, Direction = 1, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Children = lines,
        };
        if (source.Open is not { } open) return box;
        return (box with
        {
            Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = () => { close(); open(); },
        }).Interactive(Interaction.Subtle);
    }

    static TextEl Line(string text, ushort weight, ColorF ink) => new(text)
    {
        Size = ClassicPanelTiers.SheetSourceSize, LineHeight = ClassicPanelTiers.SheetSourceLine, Weight = weight, Color = ink,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };
}

/// <summary>The now-playing sheet's Devices door (C-NowPlaying-Compact): the device playback is on ("This computer"), inked in the accent while it is
/// another device, opening the player bar's own device picker over the door (the core's overlay; the menu is <c>Player.DevicePicker</c>). Reads the
/// roster in its own render, so a device change re-renders only the door. It also opens the picker when the frame asks (a toast's "Choose device",
/// the tray's Devices: <see cref="ClassicSheetPickerAsk"/>), once per ask, whether the door was mounted before the ask or by it.</summary>
internal sealed class ClassicSheetDeviceDoor : Component
{
    Action<NodeHandle>? _realized;

    public override Element Render()
    {
        var t = UseProps<PanelTransportProps>();
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        var device = t.Device is { } read ? read() : new PanelDeviceFacts(Loc.Get(Strings.Player.ThisComputer), false);
        var toggle = t.ToggleDevicePicker;
        var ask = UseContext(ClassicPanel.SheetPickerAsk);
        int ticket = ask?.Ticket.Value ?? 0;
        // The picker anchors on the door's node: an ask waits for it to be realized (a door mounted by the ask itself is not yet).
        var (realized, setRealized) = UseState(false);
        UseEffect(() =>
        {
            if (ask is null || !realized || ticket == ask.Consumed) return;
            ask.Consumed = ticket;
            if (handle.Value is not { IsOpen: true }) toggle?.Invoke(overlay, anchor, handle);
        }, DepKey.From(ticket, realized ? 1 : 0));
        _realized ??= h => { anchor.Value = h; setRealized(true); };
        var door = ClassicPanel.DoorBox(Icons.Devices, null, device.Name, () => toggle?.Invoke(overlay, anchor, handle), device.Remote, _realized);
        return ToolTip.Wrap(door, Loc.Get(Strings.Player.ChooseDevice));
    }
}
