using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The <b>Player style</b> flyout the hero's gear (and the artwork's context menu) opens: three labelled rows of four
/// thumbnail cards — Media / Devices / Software, straight off <see cref="NpvPlayerCatalog"/> — over the option rows
/// of whichever preset is currently selected.
///
/// <para><b>It stays open.</b> Every pick writes a preference and bumps <see cref="NpvPlayerPrefs.Epoch"/>, this body
/// re-renders (the reads below are Epoch-subscribed) and the user sees the deck change behind the still-open flyout.
/// That is the whole point of a style picker; only Escape / light-dismiss closes it.</para>
///
/// <para><b>Cold path, deliberately.</b> This subtree is built once per open and again per pick — a few dozen boxes.
/// It allocates a fresh mirror <c>Signal&lt;int&gt;</c> per Segmented row on each render rather than owning a hook
/// per option (the option SET changes with the preset, so a hook-per-option would be a conditional hook — the one
/// thing the hook rules forbid). The preference store, not the mirror, is the truth.</para>
/// </summary>
sealed class PlayerStyleFlyout : Component
{
    /// <summary>The flyout's content width, and the thumbnail grid derived from it: four cards + three 6-DIP gaps
    /// inside the M padding.</summary>
    public const float Width = 340f, ThumbCardGap = 6f, ThumbSize = (Width - 2 * Spacing.M - 3 * ThumbCardGap) / 4f;

    const float CardPad = 4f;
    const float OptionLabelWidth = 60f;

    // The compact Segmented used by the option rows: the mockup's .segsm (26 tall, 12pt) rather than the stock
    // 34/14 control — four choices have to share ~240 DIP beside a 60-DIP label.
    static Segmented.Style CompactSeg => Segmented.DefaultStyle with
    {
        Height = 28f, FontSize = 12f, ItemMinWidth = 56f, CornerRadius = 5f, ItemCornerRadius = 4f,
    };

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var b = UseContext(PlaybackBridge.Slot);
        var settings = svc?.Settings;

        int styleId = NpvPlayerPrefs.Style(settings);          // Epoch-subscribed
        var preset = NpvPlayerCatalog.ById(styleId);

        // The album-colour swatch previews the SAME wash the vinyl will take (NowPlayingPanel.HeroWashColor), so the
        // dot in the flyout and the record behind it can never disagree.
        var track = b?.CurrentTrack.Value;
        string? coverUrl = track?.Image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) : null;

        var kids = new List<Element>(8)
        {
            new TextEl(Loc.Get(Strings.Player.PlayerStyle)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary },
            GroupRow(Strings.Player.StyleGroupMedia, NpvPlayerGroup.Media, styleId, settings),
            GroupRow(Strings.Player.StyleGroupDevices, NpvPlayerGroup.Devices, styleId, settings),
            GroupRow(Strings.Player.StyleGroupSoftware, NpvPlayerGroup.Software, styleId, settings),
            Label(Strings.Player.StyleOptions),
        };
        foreach (var opt in preset.Options) kids.Add(OptionRow(settings, preset, opt, coverUrl));

        return new BoxEl
        {
            Width = Width, Direction = 1, Gap = Spacing.M, Padding = Edges4.All(Spacing.M),
            Children = kids.ToArray(),
        };
    }

    // ── groups + thumbnail cards ──────────────────────────────────────────────────────────────────────────────────

    static Element Label(string key) => new TextEl(Loc.Get(key))
    {
        Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    static Element GroupRow(string labelKey, NpvPlayerGroup group, int styleId, IAppSettings? settings)
    {
        var buf = new NpvPlayerCatalog.Preset[NpvPlayerCatalog.PerGroup];
        int n = NpvPlayerCatalog.Group(group, buf);
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = ThumbCard(buf[i], buf[i].Id == styleId, settings);
        return new BoxEl
        {
            Direction = 1, Gap = ThumbCardGap,
            Children = [Label(labelKey), new BoxEl { Direction = 0, Gap = ThumbCardGap, Children = cards }],
        };
    }

    static Element ThumbCard(NpvPlayerCatalog.Preset preset, bool selected, IAppSettings? settings)
    {
        int id = preset.Id;
        var card = new BoxEl
        {
            Width = ThumbSize, Shrink = 0f, Direction = 1, Gap = CardPad, Padding = Edges4.All(CardPad),
            AlignItems = FlexAlign.Center, Corners = CornerRadius4.All(6f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Edges4.All(-2f),
            OnClick = () => NpvPlayerPrefs.SetStyle(settings, id, NpvDiagnostics.SourceFlyout),
            Children =
            [
                NpvThumbnails.For(id, ThumbSize - 2f * CardPad),
                new TextEl(Loc.Get(preset.ShortLabelKey))
                {
                    Size = 11f, LineHeight = 14f, Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
                    MaxWidth = ThumbSize - 2f * CardPad, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        }.Interactive(Interaction.Card);
        // The recipe supplies the press geometry + the 83 ms brush cross-fade; the plate and the SELECTION ring are
        // this control's own (a card preset cannot know which of twelve is the chosen one). All THREE stroke legs are
        // re-stated: the recipe pinned hover/pressed to the flat card stroke, which would have faded the accent ring
        // off the selected card the moment the pointer touched it.
        var stroke = selected ? Tok.AccentDefault : Tok.StrokeCardDefault;
        return card with
        {
            Fill = Tok.FillControlAltSecondary,
            HoverFill = Tok.FillControlAltTertiary,
            PressedFill = Tok.FillControlAltQuaternary,
            BorderWidth = selected ? 2f : 1f,
            BorderColor = stroke,
            HoverBorderColor = stroke,
            PressedBorderColor = stroke,
        };
    }

    // ── option rows ───────────────────────────────────────────────────────────────────────────────────────────────

    static Element OptionRow(IAppSettings? settings, NpvPlayerCatalog.Preset preset, NpvPlayerCatalog.OptionDef opt, string? coverUrl)
        => new BoxEl
        {
            Direction = 0, Wrap = true, Gap = 10f, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
            Children =
            [
                new TextEl(Loc.Get(opt.LabelKey))
                {
                    Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MinWidth = OptionLabelWidth,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                opt.Kind == NpvOptionKind.Swatch
                    ? Swatches(settings, preset, opt, coverUrl)
                    : SegmentedRow(settings, preset, opt),
            ],
        };

    static Element SegmentedRow(IAppSettings? settings, NpvPlayerCatalog.Preset preset, NpvPlayerCatalog.OptionDef opt)
    {
        var items = new SegmentedItem[opt.Choices.Length];
        for (int i = 0; i < items.Length; i++) items[i] = new SegmentedItem(Loc.Get(opt.Choices[i].LabelKey));
        // A per-render mirror signal: Segmented re-pushes props, so the core reads THIS instance live; the click
        // writes it (the pill moves at once) and then OnChange writes the store, whose bump rebuilds this row.
        return Segmented.Create(items, new Signal<int>(NpvPlayerPrefs.Choice(settings, preset, opt)),
            i => NpvPlayerPrefs.SetChoice(settings, preset, opt, i, NpvDiagnostics.SourceFlyout),
            new Segmented.SegmentedOptions { Style = CompactSeg });
    }

    static Element Swatches(IAppSettings? settings, NpvPlayerCatalog.Preset preset, NpvPlayerCatalog.OptionDef opt, string? coverUrl)
    {
        int current = NpvPlayerPrefs.Choice(settings, preset, opt);
        var dots = new Element[opt.Choices.Length];
        for (int i = 0; i < dots.Length; i++)
        {
            int index = i;
            var choice = opt.Choices[i];
            bool selected = index == current;

            // Swatch 0 is the sentinel "derive from the cover" (NpvPlayerCatalog.FromCover) — a BOUND fill, so a late
            // colour grading repaints the dot without re-rendering the flyout.
            Prop<ColorF> fill = NpvSwatch.Color(choice.Swatch);
            if (choice.Swatch == NpvPlayerCatalog.FromCover) fill = Prop.Of(() => NowPlayingPanel.HeroWashColor(coverUrl));

            dots[i] = ToolTip.Wrap(new BoxEl
            {
                Width = 30f, Height = 30f, Shrink = 0f, Corners = Radii.Circle(30f),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BorderWidth = selected ? 2f : 0f,
                BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent,
                Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = Edges4.All(-2f),
                OnClick = () => NpvPlayerPrefs.SetChoice(settings, preset, opt, index, NpvDiagnostics.SourceFlyout),
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
}

/// <summary>0xAARRGGBB (the catalog's persisted swatch encoding) → <see cref="ColorF"/>. Swatch 0 is not a colour at
/// all — it is <see cref="NpvPlayerCatalog.FromCover"/>, and every caller must special-case it before getting here.</summary>
static class NpvSwatch
{
    public static ColorF Color(uint argb) => new(
        ((argb >> 16) & 255) / 255f,
        ((argb >> 8) & 255) / 255f,
        (argb & 255) / 255f,
        ((argb >> 24) & 255) / 255f);
}

/// <summary>
/// The right-click menu on the Now Playing artwork: the same Cover / ‹Player› choice the header's SelectorBar offers,
/// plus a way into the style flyout. The menu is the DISCOVERY path — a user who never notices a 36-DIP header strip
/// still finds the feature where they already right-click.
/// </summary>
static class NpvArtMenu
{
    public static ContextMenuModel Model(IAppSettings? settings, int presentation, in NpvPlayerCatalog.Preset preset)
    {
        // Radio, not toggle: the two are one mutually-exclusive choice, and WinUI's E915 bullet says so.
        MenuFlyoutItem[] rows =
        [
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.PresentationCover), presentation == NpvPlayerPrefs.Cover,
                () => NpvPlayerPrefs.SetPresentation(settings, NpvPlayerPrefs.Cover, NpvDiagnostics.SourceArtMenu), Icons.Picture),
            MenuFlyoutItem.RadioItem(Loc.Get(preset.ShortLabelKey), presentation == NpvPlayerPrefs.Player,
                () => NpvPlayerPrefs.SetPresentation(settings, NpvPlayerPrefs.Player, NpvDiagnostics.SourceArtMenu), Icons.Album),
            MenuFlyoutItem.Separator,
            // Opens the SAME popup the gear owns (one controlled signal, one flyout instance) — never a second copy.
            new MenuFlyoutItem(Loc.Get(Strings.Player.PlayerStyleEllipsis), Icons.Settings,
                Invoke: () => NpvPlayerPrefs.StyleFlyoutOpen.Value = true),
        ];
        return new ContextMenuModel(rows);
    }
}
