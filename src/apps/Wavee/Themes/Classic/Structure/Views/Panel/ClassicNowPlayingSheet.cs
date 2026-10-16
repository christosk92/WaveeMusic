// ── Themes/Classic/Structure/Views/Panel/ClassicNowPlayingSheet.cs — the Compact now-playing sheet's column under the art ─────────────
//
// C-NowPlaying-Compact: under the art (the hero tile, at the side `ClassicPanelTiers.SheetArtSide` leaves it) the sheet carries what the
// mini bar it covers would have, and more:
//
//   identity    the title (20/28, one line) over the artists (14/20, links), the heart (40) beside them
//   seek        the bar's own seek rail in its 32-tall lane, the elapsed and remaining times under it
//   transport   shuffle (40) · previous (48) · the play disc (56) · next (48) · repeat (40), spread across the sheet
//   doors       Lyrics and Queue (48), each opening that body in the same sheet
//   the rest    the lyrics peek and the docked body's sections (about the artist, the cities, merch, "Next up")
//
// Every block above the rest has a fixed height (`ClassicPanelTiers.SheetBelowArt`), so the art's side arithmetic and the column agree. The
// transport is its own component: a playback edge re-renders it, not the column. Its facts and verbs are the core's (`PanelProps.Transport`).

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
        if (props.ShowMode is { } show) kids.Add(ClassicPanel.SheetDoors(show));
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
