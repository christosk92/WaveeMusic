// ── Shell/Rail.Styles.UI.cs ────────────────────────────────────────────────────────────────────────────────────────
// player-style flyout, thumbnails, art menu, swatches
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 600 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE PLAYER-STYLE PICKER (ch 21 W9, W10, §6.3). Three labelled rows of four 74.5-DIP thumbnail cards straight off
// `Rail.PlayerCatalog`, over the option rows of the selected preset.
//
//   • It STAYS OPEN across picks: every pick writes a preference, bumps the ONE epoch, and this body re-renders while the
//     deck changes behind it. Only Escape / light-dismiss closes it.
//   • A fresh mirror `Signal<int>` per Segmented row per render, ON PURPOSE: the option SET changes with the preset, so
//     a hook-per-option would be a conditional hook. The store, not the mirror, is the truth.
//   • The twelve mini-arts are ICONS, not decks: literal colours transliterated from the mockup's `.m-*` CSS, nothing
//     animates, nothing binds a signal (WMP's analyser is the one theme read — its deck's default palette is the accent).
//   • The art menu is the discovery path: Cover / ‹Player› as RADIO items, then "Player style…" opening the SAME popup.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Rail
{
    /// <summary>The flyout's content width, and the thumbnail grid derived from it: four cards + three 6-DIP gaps inside
    /// the 12-DIP padding. A const: at rail 200 it overhangs the page by 140 and does not reflow (W7b).</summary>
    public const float StyleFlyoutW = 340f, ThumbCardGap = 6f, ThumbSize = (StyleFlyoutW - 2f * Spacing.M - 3f * ThumbCardGap) / 4f;

    const float ThumbCardPad = 4f, OptionLabelW = 60f;

    /// <summary>The flyout body (the gear's popup content).</summary>
    static Element StyleFlyout() => Embed.Comp(static () => new StyleFlyoutCore());

    /// <summary>The compact Segmented of the option rows: 28 tall, 12 pt, items ≥ 56 — four choices share ~240 DIP.</summary>
    static Segmented.Style CompactSeg => Segmented.DefaultStyle with
    {
        Height = 28f, FontSize = 12f, ItemMinWidth = 56f, CornerRadius = 5f, ItemCornerRadius = 4f,
    };

    sealed class StyleFlyoutCore : Component
    {
        public override Element Render()
        {
            var preset = PlayerPrefs.CurrentPreset();                      // epoch-subscribed
            var kids = new System.Collections.Generic.List<Element>(8)
            {
                new TextEl(Loc.Get(Strings.Player.PlayerStyle)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary },
                GroupRow(Strings.Player.StyleGroupMedia, PlayerGroup.Media, preset.Id),
                GroupRow(Strings.Player.StyleGroupDevices, PlayerGroup.Devices, preset.Id),
                GroupRow(Strings.Player.StyleGroupSoftware, PlayerGroup.Software, preset.Id),
                FlyoutLabel(Strings.Player.StyleOptions),
            };
            for (int i = 0; i < preset.Options.Length; i++) kids.Add(OptionRow(preset, preset.Options[i]));
            return new BoxEl
            {
                Width = StyleFlyoutW, Direction = 1, Gap = Spacing.M, Padding = Edges4.All(Spacing.M),
                Children = kids.ToArray(),
            };
        }
    }

    static Element FlyoutLabel(string key) => new TextEl(Loc.Get(key))
    {
        Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    static readonly PlayerCatalog.Preset[] s_groupBuf = new PlayerCatalog.Preset[PlayerCatalog.PerGroup];

    static Element GroupRow(string labelKey, PlayerGroup group, int selectedId)
    {
        var buf = s_groupBuf;                                              // UI thread only; cold path (per open / per pick)
        int n = PlayerCatalog.Group(group, buf);
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = ThumbCard(buf[i], buf[i].Id == selectedId);
        return new BoxEl
        {
            Direction = 1, Gap = ThumbCardGap,
            Children = [FlyoutLabel(labelKey), new BoxEl { Direction = 0, Gap = ThumbCardGap, Children = cards }],
        };
    }

    /// <summary>One preset card. The recipe supplies the press geometry and the brush cross-fade; the plate and the
    /// SELECTION ring are this card's own, and all three stroke legs are re-stated so hover never fades the accent ring.</summary>
    static Element ThumbCard(in PlayerCatalog.Preset preset, bool selected)
    {
        int id = preset.Id;
        var stroke = selected ? Tok.AccentDefault : Tok.StrokeCardDefault;
        return new BoxEl
        {
            Width = ThumbSize, Shrink = 0f, Direction = 1, Gap = ThumbCardPad, Padding = Edges4.All(ThumbCardPad),
            AlignItems = FlexAlign.Center, Corners = CornerRadius4.All(6f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = () => PlayerPrefs.SetStyle(id, NpvDiagnostics.SourceFlyout),
            Children =
            [
                Thumbnail(id, ThumbSize - 2f * ThumbCardPad),
                new TextEl(Loc.Get(preset.ShortLabelKey))
                {
                    Size = 11f, LineHeight = 14f, Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
                    MaxWidth = ThumbSize - 2f * ThumbCardPad, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        }.Interactive(Interaction.Card) with
        {
            Fill = Tok.FillControlAltSecondary, HoverFill = Tok.FillControlAltTertiary, PressedFill = Tok.FillControlAltQuaternary,
            BorderWidth = selected ? 2f : 1f, BorderColor = stroke, HoverBorderColor = stroke, PressedBorderColor = stroke,
        };
    }

    /// <summary>Label (min 60) + a Segmented or a swatch row; <c>Wrap</c> lets a 4-choice row drop under its label.</summary>
    static Element OptionRow(in PlayerCatalog.Preset preset, in PlayerCatalog.OptionDef option) => new BoxEl
    {
        Direction = 0, Wrap = true, Gap = 10f, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
        Children =
        [
            new TextEl(Loc.Get(option.LabelKey)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MinWidth = OptionLabelW, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            option.Kind == OptionKind.Swatch ? Swatches(preset, option) : SegmentedRow(preset, option),
        ],
    };

    static Element SegmentedRow(PlayerCatalog.Preset preset, PlayerCatalog.OptionDef option)
    {
        var items = new SegmentedItem[option.Choices.Length];
        for (int i = 0; i < items.Length; i++) items[i] = new SegmentedItem(Loc.Get(option.Choices[i].LabelKey));
        // A per-render mirror: the click writes it (the pill moves at once), then onChange writes the store.
        return Segmented.Create(items, new Signal<int>(PlayerPrefs.Choice(preset, option)),
            i => PlayerPrefs.SetChoice(preset, option, i, NpvDiagnostics.SourceFlyout),
            new Segmented.SegmentedOptions { Style = CompactSeg });
    }

    /// <summary>30-DIP rings around 22-DIP dots; the selected ring is the 2-DIP accent. Swatch 0 is "derive from the cover"
    /// — a BOUND fill painting the SAME hero wash the cover placeholder and the vinyl take.</summary>
    static Element Swatches(PlayerCatalog.Preset preset, PlayerCatalog.OptionDef option)
    {
        int current = PlayerPrefs.Choice(preset, option);
        var dots = new Element[option.Choices.Length];
        for (int i = 0; i < dots.Length; i++)
        {
            int index = i;
            var choice = option.Choices[i];
            bool selected = index == current;
            Prop<ColorF> fill = choice.Swatch == PlayerCatalog.FromCover
                ? Prop.Of(static () => HeroWashColor(NowArtUrl()))
                : SwatchColor(choice.Swatch);
            dots[i] = ToolTip.Wrap(new BoxEl
            {
                Width = 30f, Height = 30f, Shrink = 0f, Corners = Radii.Circle(30f),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BorderWidth = selected ? 2f : 0f, BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, FocusVisualMargin = Design.FocusInsetBordered,
                OnClick = () => PlayerPrefs.SetChoice(preset, option, index, NpvDiagnostics.SourceFlyout),
                Children =
                [
                    new BoxEl
                    {
                        Width = 22f, Height = 22f, Shrink = 0f, Corners = Radii.Circle(22f), Fill = fill,
                        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HitTestVisible = false,
                    },
                ],
            }, Loc.Get(choice.LabelKey));
        }
        return new BoxEl { Direction = 0, Gap = Spacing.S, Children = dots };
    }

    /// <summary>0xAARRGGBB (the catalog's persisted encoding) → a colour. Never called with
    /// <see cref="PlayerCatalog.FromCover"/>, which is not a colour.</summary>
    public static ColorF SwatchColor(uint argb) => Design.Palette.ToColor(argb);

    // ── the artwork context menu (W10) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Cover / ‹Player› as ONE mutually exclusive radio choice, a separator, then "Player style…" opening the SAME
    /// popup the gear owns (one controlled signal, one flyout instance — never a second copy).
    /// <para>The radio PAIR is the header's switch in menu form, so it follows the same developer-only gate
    /// (<paramref name="switchVisible"/>): outside developer mode the menu is "Player style…" alone, never a choice that
    /// cannot change the face.</para></summary>
    static ContextMenuModel? ArtMenu(int presentation, in PlayerCatalog.Preset preset, bool switchVisible)
    {
        var style = new MenuFlyoutItem(Loc.Get(Strings.Player.PlayerStyleEllipsis), Icons.Settings,
            Invoke: static () => PlayerPrefs.StyleFlyoutOpen.Value = true);
        if (!switchVisible) return new ContextMenuModel([style]);
        return new ContextMenuModel(
        [
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.PresentationCover), presentation == PlayerPrefs.Cover,
                static () => PlayerPrefs.SetPresentation(PlayerPrefs.Cover, NpvDiagnostics.SourceArtMenu), Icons.Picture),
            MenuFlyoutItem.RadioItem(Loc.Get(preset.ShortLabelKey), presentation == PlayerPrefs.Player,
                static () => PlayerPrefs.SetPresentation(PlayerPrefs.Player, NpvDiagnostics.SourceArtMenu), Icons.Album),
            MenuFlyoutItem.Separator,
            style,
        ]);
    }

    // ── the twelve mini-arts (NpvThumbnails.cs, verbatim geometry) ───────────────────────────────────────────────────

    /// <summary>The mini-art for a preset id, drawn <paramref name="s"/> square. An unknown id paints the default preset's
    /// art, so a retired id can never paint a hole. Every layer is <c>HitTestVisible = false</c>: the card owns the click.</summary>
    public static Element Thumbnail(int presetId, float s) => presetId switch
    {
        PlayerCatalog.Cassette => Plate(s, Hex(0x2B2F3A),
        [
            At(.10f * s, .24f * s, .80f * s, .52f * s) with { Fill = Hex(0xE9E2D0), Corners = CornerRadius4.All(3f) },
            Hub(At(.22f * s, .44f * s, .20f * s, .20f * s), .20f * s),
            Hub(AtRight(.22f * s, .44f * s, .20f * s, .20f * s), .20f * s),
        ]),
        PlayerCatalog.Reel => Plate(s, Hex(0x22252B),
        [
            Flange(At(.08f * s, .16f * s, .40f * s, .40f * s), .40f * s),
            Flange(AtRight(.08f * s, .16f * s, .40f * s, .40f * s), .40f * s),
        ]),
        PlayerCatalog.Cd => Plate(s, Tok.FillCardSecondary,
        [
            At(.18f * s, .18f * s, .64f * s, .64f * s) with
            {
                Corners = Radii.Circle(.64f * s), ZStack = true,
                Gradient = Ui.LinearGradient(45f,
                    new GradientStop(0f, Hex(0xFF99CC)), new GradientStop(.25f, Hex(0x99CCFF)), new GradientStop(.5f, Hex(0xCCFFCC)),
                    new GradientStop(.75f, Hex(0xFFFFCC)), new GradientStop(1f, Hex(0xFF99CC))),
                Children = [Centered(.64f * s * .32f, Tok.FillCardSecondary)],
            },
        ]),
        PlayerCatalog.Turntable => Plate(s, Ui.GradientDown(new GradientStop(0f, Hex(0x6B4A2E)), new GradientStop(1f, Hex(0x4A301B))),
        [
            Disc(AtRight(.08f * s, .22f * s, .60f * s, .60f * s), .60f * s, Hex(0x111111), Hex(0xBCD6FF)) with { BorderWidth = 3f, BorderColor = Hex(0x3A3D44) },
        ]),
        PlayerCatalog.Ipod => Plate(s, Hex(0x3A3F4A),
        [
            At(.24f * s, .06f * s, .52f * s, .88f * s) with
            {
                Corners = CornerRadius4.All(6f),
                Gradient = Ui.GradientDown(new GradientStop(0f, Hex(0xF4F4F6)), new GradientStop(1f, Hex(0xC9CCD3))),
            },
            At(.32f * s, .12f * s, .36f * s, .30f * s) with { Fill = Hex(0x1D2A3A), Corners = CornerRadius4.All(2f) },
            At(.33f * s, .50f * s, .34f * s, .34f * s) with { Corners = Radii.Circle(.34f * s), Fill = Hex(0xFFFFFF), BorderWidth = 1f, BorderColor = Hex(0xAAAAAA) },
        ]),
        PlayerCatalog.Winamp => Plate(s, Hex(0x232A37),
        [
            At(.08f * s, .14f * s, .84f * s, .30f * s) with { Fill = Hex(0x3B4459) },
            At(.08f * s, .14f * s, .84f * s, 3f) with { Fill = Hex(0x6E7A97) },
            At(.14f * s, .20f * s, .34f * s, .16f * s) with { Fill = Hex(0x000000), BorderWidth = 1f, BorderColor = Hex(0x00FF00) },
            Skyline(.08f * s, .14f * s, .84f * s, .36f * s, Hex(0x00FF00), WinampBars),
        ]),
        PlayerCatalog.Vu => Plate(s, Hex(0x15161A),
        [
            At(.08f * s, .22f * s, .38f * s, .38f * s) with { Fill = Hex(0xEFE6CF), Corners = new CornerRadius4(6f, 6f, .19f * s, .19f * s) },
            AtRight(.08f * s, .22f * s, .38f * s, .38f * s) with { Fill = Hex(0xEFE6CF), Corners = new CornerRadius4(6f, 6f, .19f * s, .19f * s) },
            AtBottom(.08f * s, .14f * s, .84f * s, .14f * s) with { Fill = Hex(0x1A0D00), BorderWidth = 1f, BorderColor = Hex(0xFFB000) },
        ]),
        PlayerCatalog.Zune => Plate(s, Hex(0x000000),
        [
            Disc(At(.10f * s, .24f * s, .52f * s, .52f * s), .52f * s, Hex(0x111111), Hex(0xFFFFFF)),
            AtRight(.08f * s, .10f * s, .28f * s, 6f) with { Fill = Hex(0xF0568C) },
        ]),
        PlayerCatalog.Wmp => Plate(s, Hex(0x04060C),
        [
            Skyline(.08f * s, .14f * s, .84f * s, .60f * s, Prop.Of(static () => Tok.AccentDefault), WmpBars),
        ]),
        PlayerCatalog.Canvas => Plate(s,
            Ui.RadialGradient(new Point2(.40f, .40f), new Point2(.85f, .85f),
                new GradientStop(0f, Hex(0xFFFFFF)), new GradientStop(.55f, Hex(0xB9D0FB)), new GradientStop(1f, Hex(0x7F9BE0))),
        [
            At(.14f * s, .14f * s, .72f * s, .72f * s) with { Fill = Hex(0xE9F1FF), Corners = CornerRadius4.All(3f), Rotation = 3f },
        ]),
        PlayerCatalog.Picture => Plate(s, Tok.FillCardSecondary,
        [
            AtRight(.14f * s, .18f * s, .64f * s, .64f * s) with
            {
                Corners = Radii.Circle(.64f * s),
                Gradient = Ui.RadialGradient(new Point2(.50f, .40f), new Point2(.75f, .75f),
                    new GradientStop(0f, Hex(0xFFFFFF)), new GradientStop(.70f, Hex(0xBCD6FF)), new GradientStop(1f, Hex(0x9FBEF0))),
            },
        ]),
        // .m-record — a pale tilted sleeve at the back, a black disc with a pale label over it.
        _ => Plate(s, Tok.FillCardSecondary,
        [
            At(.08f * s, .18f * s, .50f * s, .50f * s) with { Fill = Hex(0xBCD6FF), Corners = CornerRadius4.All(2f), Rotation = -3f },
            Disc(AtRight(.10f * s, .22f * s, .56f * s, .56f * s), .56f * s, Hex(0x111111), Hex(0xBCD6FF)),
        ]),
    };

    static readonly float[] WinampBars = [.40f, .60f, .20f, .55f, .95f, .45f, .70f, 1f];
    static readonly float[] WmpBars = [.55f, .25f, .70f, .45f, .95f, .35f, .60f, .30f, .75f, .40f];

    static BoxEl Plate(float s, Prop<ColorF> fill, Element[] kids) => new()
    {
        Width = s, Height = s, Shrink = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Corners = CornerRadius4.All(4f), Fill = fill, Children = kids,
    };

    static BoxEl Plate(float s, GradientSpec gradient, Element[] kids) => new()
    {
        Width = s, Height = s, Shrink = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Corners = CornerRadius4.All(4f), Gradient = gradient, Children = kids,
    };

    /// <summary>CSS <c>left:x; top:y</c> — a ZStack layer anchored top-left; the leading Margin is the offset.</summary>
    static BoxEl At(float x, float y, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(x, y, 0f, 0f),
    };

    /// <summary>CSS <c>right:r; top:y</c>.</summary>
    static BoxEl AtRight(float r, float y, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, y, r, 0f),
    };

    /// <summary>CSS <c>left:x; bottom:b</c>.</summary>
    static BoxEl AtBottom(float x, float b, float w, float h) => new()
    {
        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Margin = new Edges4(x, 0f, 0f, b),
    };

    /// <summary>A circular body with its label dead centre at 32 % of the diameter (the CSS inset:34%).</summary>
    static BoxEl Disc(BoxEl box, float d, ColorF body, ColorF label) => box with
    {
        Corners = Radii.Circle(d), Fill = body, ZStack = true, Children = [Centered(d * .32f, label)],
    };

    static BoxEl Centered(float d, Prop<ColorF> fill) => new()
    {
        Width = d, Height = d, Shrink = 0f, HitTestVisible = false, Corners = Radii.Circle(d), Fill = fill,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };

    /// <summary>A dark hub inside a white ring (the CSS inset box-shadow ⇒ an inside border).</summary>
    static BoxEl Hub(BoxEl box, float d) => box with { Corners = Radii.Circle(d), Fill = Hex(0x222222), BorderWidth = 2f, BorderColor = Hex(0xFFFFFF) };

    /// <summary>An aluminium flange with the brown pack showing as the inner ring.</summary>
    static BoxEl Flange(BoxEl box, float d) => box with { Corners = Radii.Circle(d), Fill = Hex(0xC8CCD3), BorderWidth = 4f, BorderColor = Hex(0x3A2A1A) };

    /// <summary>The clip-path skyline (Winamp / WMP): literal bottom-aligned bars tracing the same polygon.</summary>
    static BoxEl Skyline(float x, float bottom, float w, float h, Prop<ColorF> ink, float[] heights)
    {
        int n = heights.Length;
        float gap = w / (n * 3f);
        float bw = MathF.Max(1f, (w - gap * (n - 1)) / n);
        var bars = new Element[n];
        for (int i = 0; i < n; i++)
            bars[i] = new BoxEl { Width = bw, Height = MathF.Max(2f, h * heights[i]), Shrink = 0f, HitTestVisible = false, Fill = ink };
        return AtBottom(x, bottom, w, h) with { Direction = 0, Gap = gap, AlignItems = FlexAlign.End, Children = bars };
    }

    static ColorF Hex(uint rgb) => ColorF.FromRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
