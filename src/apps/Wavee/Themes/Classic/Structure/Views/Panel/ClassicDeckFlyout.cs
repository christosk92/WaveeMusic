// ── Themes/Classic/Structure/Views/Panel/ClassicDeckFlyout.cs — Classic's `Player.DeckStyleFlyout` (themes plan 6.1, T6b) ─────
//
// From `Shell/Rail.Styles.UI.cs`: the player-style picker (ch 21 W9, W10, §6.3). Three labelled rows of four 74.5-DIP thumbnail
// cards straight off `Rail.PlayerCatalog`, over the option rows of the selected preset.
//
//   • It STAYS OPEN across picks: every pick writes a preference through the verbs, bumps the ONE epoch, and this body re-renders
//     while the deck changes behind it. Only Escape / light-dismiss closes it.
//   • A fresh mirror `Signal<int>` per Segmented row per render, ON PURPOSE: the option SET changes with the preset, so
//     a hook-per-option would be a conditional hook. The store, not the mirror, is the truth.
//   • The twelve mini-arts are ICONS, not decks (`Rules\DeckThumbnails.cs`): nothing animates, nothing binds a signal.
//
// The art menu (the discovery path: Cover / ‹Player› as RADIO items, then "Player style…" opening the SAME popup) is the hero's
// (`ClassicNowPlayingHero.ArtMenu`): it is the context menu of the hero's wrapper.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's deck style flyout.</summary>
internal static class ClassicDeckFlyout
{
    /// <summary>The registered template of <see cref="ViewKeys.DeckStyleFlyout"/>.</summary>
    public static Element Template(DeckStyleFlyoutProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicStyleFlyoutCore());

    /// <summary>The thumbnail grid derived from the flyout's width: four cards + three gaps inside the padding. A const-width flyout:
    /// at rail 200 it overhangs the page by 140 and does not reflow (W7b).</summary>
    internal static float ThumbSize => (ClassicDeckFlyoutLayout.Width - 2f * Spacing.M - 3f * ClassicDeckFlyoutLayout.CardGap) / 4f;

    internal static Element FlyoutLabel(string key) => new TextEl(Loc.Get(key))
    {
        Size = ClassicDeckFlyoutLayout.LabelSize, LineHeight = ClassicDeckFlyoutLayout.LabelLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    static readonly Rail.PlayerCatalog.Preset[] s_groupBuf = new Rail.PlayerCatalog.Preset[Rail.PlayerCatalog.PerGroup];

    internal static Element GroupRow(IDeckStyleVerbs verbs, string labelKey, Rail.PlayerGroup group, int selectedId)
    {
        var buf = s_groupBuf;                                              // UI thread only; cold path (per open / per pick)
        int n = Rail.PlayerCatalog.Group(group, buf);
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = ThumbCard(verbs, buf[i], buf[i].Id == selectedId);
        return new BoxEl
        {
            Direction = 1, Gap = ClassicDeckFlyoutLayout.CardGap,
            Children = [FlyoutLabel(labelKey), new BoxEl { Direction = 0, Gap = ClassicDeckFlyoutLayout.CardGap, Children = cards }],
        };
    }

    /// <summary>One preset card. The recipe supplies the press geometry and the brush cross-fade; the plate and the
    /// SELECTION ring are this card's own, and all three stroke legs are re-stated so hover never fades the accent ring.</summary>
    static Element ThumbCard(IDeckStyleVerbs verbs, in Rail.PlayerCatalog.Preset preset, bool selected)
    {
        int id = preset.Id;
        var stroke = selected ? Tok.AccentDefault : Tok.StrokeCardDefault;
        return new BoxEl
        {
            Width = ThumbSize, Shrink = 0f, Direction = 1, Gap = ClassicDeckFlyoutLayout.CardPad, Padding = Edges4.All(ClassicDeckFlyoutLayout.CardPad),
            AlignItems = FlexAlign.Center, Corners = CornerRadius4.All(ClassicDeckFlyoutLayout.CardRadius),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, FocusVisualMargin = Design.FocusInsetBordered,
            OnClick = () => verbs.SetStyle(id),
            Children =
            [
                ClassicDeckThumbnails.Thumbnail(id, ThumbSize - 2f * ClassicDeckFlyoutLayout.CardPad),
                R.Type.MicroMeta(Loc.Get(preset.ShortLabelKey)) with
                {
                    Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
                    MaxWidth = ThumbSize - 2f * ClassicDeckFlyoutLayout.CardPad, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        }.Interactive(Interaction.Card) with
        {
            Fill = Tok.FillControlAltSecondary, HoverFill = Tok.FillControlAltTertiary, PressedFill = Tok.FillControlAltQuaternary,
            BorderWidth = selected ? ClassicDeckFlyoutLayout.SelectedRing : 1f, BorderColor = stroke, HoverBorderColor = stroke, PressedBorderColor = stroke,
        };
    }

    /// <summary>Label (min 60) + a Segmented or a swatch row; <c>Wrap</c> lets a 4-choice row drop under its label.</summary>
    internal static Element OptionRow(DeckStyleFlyoutProps props, in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option) => new BoxEl
    {
        Direction = 0, Wrap = true, Gap = ClassicDeckFlyoutLayout.OptionGap, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
        Children =
        [
            new TextEl(Loc.Get(option.LabelKey)) { Size = ClassicDeckFlyoutLayout.LabelSize, LineHeight = ClassicDeckFlyoutLayout.LabelLine, Color = Tok.TextSecondary, MinWidth = ClassicDeckFlyoutLayout.OptionLabelW, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            option.Kind == Rail.OptionKind.Swatch ? Swatches(props, preset, option) : SegmentedRow(props.Verbs, preset, option),
        ],
    };

    static Element SegmentedRow(IDeckStyleVerbs verbs, Rail.PlayerCatalog.Preset preset, Rail.PlayerCatalog.OptionDef option)
    {
        var items = new SegmentedItem[option.Choices.Length];
        for (int i = 0; i < items.Length; i++) items[i] = new SegmentedItem(Loc.Get(option.Choices[i].LabelKey));
        // A per-render mirror: the click writes it (the pill moves at once), then onChange writes the store.
        return Segmented.Create(items, verbs.ChoiceMirror(in preset, in option),
            i => verbs.SetChoice(in preset, in option, i),
            new Segmented.SegmentedOptions { Style = ClassicDeckFlyoutLayout.CompactSeg });
    }

    /// <summary>30-DIP rings around 22-DIP dots; the selected ring is the 2-DIP accent. Swatch 0 is "derive from the cover"
    /// — a BOUND fill painting the SAME hero wash the cover placeholder and the vinyl take.</summary>
    static Element Swatches(DeckStyleFlyoutProps props, Rail.PlayerCatalog.Preset preset, Rail.PlayerCatalog.OptionDef option)
    {
        int current = Rail.PlayerPrefs.Choice(preset, option);
        var verbs = props.Verbs;
        var dots = new Element[option.Choices.Length];
        for (int i = 0; i < dots.Length; i++)
        {
            int index = i;
            var choice = option.Choices[i];
            bool selected = index == current;
            Prop<ColorF> fill = choice.Swatch == Rail.PlayerCatalog.FromCover
                ? Prop.Of(() => Rail.HeroWashColor(props.NowArtUrl()))
                : ColorScience.ToColor(choice.Swatch);
            dots[i] = ToolTip.Wrap(new BoxEl
            {
                Width = ClassicDeckFlyoutLayout.SwatchRing, Height = ClassicDeckFlyoutLayout.SwatchRing, Shrink = 0f, Corners = Radii.Circle(ClassicDeckFlyoutLayout.SwatchRing),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BorderWidth = selected ? ClassicDeckFlyoutLayout.SelectedRing : 0f, BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, FocusVisualMargin = Design.FocusInsetBordered,
                OnClick = () => verbs.SetChoice(in preset, in option, index),
                Children =
                [
                    new BoxEl
                    {
                        Width = ClassicDeckFlyoutLayout.SwatchDot, Height = ClassicDeckFlyoutLayout.SwatchDot, Shrink = 0f, Corners = Radii.Circle(ClassicDeckFlyoutLayout.SwatchDot), Fill = fill,
                        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HitTestVisible = false,
                    },
                ],
            }, Loc.Get(choice.LabelKey));
        }
        return new BoxEl { Direction = 0, Gap = Spacing.S, Children = dots };
    }
}

/// <summary>The flyout body (the gear's popup content). A component of the theme: it re-renders off the prefs epoch while open.</summary>
internal sealed class ClassicStyleFlyoutCore : Component
{
    public override Element Render()
    {
        var props = UseProps<DeckStyleFlyoutProps>();
        var verbs = props.Verbs;
        var preset = Rail.PlayerPrefs.CurrentPreset();                      // epoch-subscribed
        var kids = new List<Element>(8)
        {
            new TextEl(Loc.Get(Strings.Player.PlayerStyle)) { Size = ClassicDeckFlyoutLayout.TitleSize, LineHeight = ClassicDeckFlyoutLayout.TitleLine, Weight = 600, Color = Tok.TextPrimary },
            ClassicDeckFlyout.GroupRow(verbs, Strings.Player.StyleGroupMedia, Rail.PlayerGroup.Media, preset.Id),
            ClassicDeckFlyout.GroupRow(verbs, Strings.Player.StyleGroupDevices, Rail.PlayerGroup.Devices, preset.Id),
            ClassicDeckFlyout.GroupRow(verbs, Strings.Player.StyleGroupSoftware, Rail.PlayerGroup.Software, preset.Id),
            ClassicDeckFlyout.FlyoutLabel(Strings.Player.StyleOptions),
        };
        for (int i = 0; i < preset.Options.Length; i++) kids.Add(ClassicDeckFlyout.OptionRow(props, in preset, in preset.Options[i]));
        return new BoxEl
        {
            Width = ClassicDeckFlyoutLayout.Width, Direction = 1, Gap = Spacing.M, Padding = Edges4.All(Spacing.M),
            Children = kids.ToArray(),
        };
    }
}
