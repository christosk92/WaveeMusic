// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Kit.cs — the Settings page's grammar: the row card, the section header, the controls at the tier's target ──
//
// THE ROW CARD (`DialCard`). Every dial of the registry draws as a SettingsCard (W-Settings: Body 14 label over a 12 caption, the control on the right, the
// card's fill, 1-DIP border and control radius). The engine's SettingsCard has no slot after its header text, and a row whose effect you see or hear carries
// its explanation badge right after the label (design 6.5, the concept board W-Arrival), so the page composes its card here FROM the card's own style record
// (`SettingsCard.DefaultStyle`, the expander's item and header styles): the same fills, borders, radius, padding, type and states, a label line that holds the
// badge, and an optional footer (the "Also in" line, W-Arrival). Where the tier stacks the controls (under 560 content width) a control sits UNDER its label,
// full width, except a toggle, which keeps the label | state | switch row of the board; the record decides it (`WinUiSettingsLayout.StacksControls`), never
// the card's own measured width, so the first frame is already the final form.
//
// THE CONTROLS. The Compact rule is a floor, not a size: every hit target is at least 40 x 40. A control is asked for through the helpers below and comes at
// the size the tier's record asks for (`WinUiSettingsLayout.Target`: 40 at Compact, 0 = the control's own size elsewhere): a Large button, a link with a 40
// floor, a switch in a 48 x 40 cell, a segmented selector and a slider at 40, the stock ComboBox at its Large field filling the stacked row, a NumberBox whose
// field is the target tall. Each reads the record from `s_layout`, which the page template and every `SettingsComponent` set from the tier facts first.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    // ══ 1. THE TIER RECORD AS THE CONTROLS SEE IT ═════════════════════════════════════════════════════════════════

    /// <summary>The page's tier record as the static builders see it (see the file header).</summary>
    static WinUiSettingsLayout s_layout = WinUiSettingsTiers.For(WidthTier.Standard);

    /// <summary>A stateful card of the page: it subscribes to the tier facts (so a crossing re-renders it) and refreshes <c>s_layout</c> before it builds a control.</summary>
    abstract class SettingsComponent : CommonTierComponent
    {
        TierFacts _tier;

        /// <summary>The tier facts this render was built from (set by <see cref="UseLayout"/>).</summary>
        protected TierFacts Tier => _tier;

        /// <summary>Call first in <c>Render</c>: the page's record for the tier on screen now.</summary>
        protected WinUiSettingsLayout UseLayout() => s_layout = WinUiSettingsTiers.Of(_tier = ReadTier());
    }

    // ══ 2. THE ROW CARD ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>How a card lays its control out.</summary>
    enum CardForm : byte
    {
        /// <summary>The label on the left, the control on the right (or under the label where the tier stacks the controls).</summary>
        Right,
        /// <summary>The control under the label, full width (a wide control: a chart, a picker strip).</summary>
        Vertical,
        /// <summary>The control alone, from the card's left edge (a list of links, a block of text).</summary>
        Left,
    }

    /// <summary>What one row card draws.</summary>
    /// <param name="Label">The label (Body 14).</param>
    /// <param name="Description">The caption under it (12, secondary), or null.</param>
    /// <param name="Control">The control, or null for a card of words.</param>
    /// <param name="Style">The card's style: <see cref="SettingsCard.DefaultStyle"/>, an expander's item or header style.</param>
    record struct Card(string Label, string? Description, Element? Control, SettingsCard.Style Style)
    {
        /// <summary>The 20-DIP glyph before the label (only Theme, App mode and Zoom carry one; the home's cards carry their category's).</summary>
        public string? Glyph;
        public CardForm Form;
        /// <summary>The whole card is a button (a chevron at its end): a door, an action without a control.</summary>
        public Action? OnClick;
        /// <summary>The clickable card's end glyph (the chevron by default; the open-in-new glyph for a link that leaves the app).</summary>
        public string? ActionIcon;
        /// <summary>The words beside the chevron of a clickable card (a Recently changed row's "2 min ago").</summary>
        public string? Trailing;
        public bool Enabled;
        /// <summary>The explanation badge after the label (the Explainer view), or null.</summary>
        public ExplainerProps? Explain;
        /// <summary>A footer inside the card, under a hairline ("Also in …"), or null.</summary>
        public Element? Footer;
        /// <summary>Called with the control's cell when it realizes (the arrival moves keyboard focus into it).</summary>
        public Action<NodeHandle>? OnControl;
        /// <summary>The label wraps (a word list); one line with an ellipsis where false.</summary>
        public bool WrapLabel;
        /// <summary>The description is one line with an ellipsis (a home card's value: the board's cards keep one height per row).</summary>
        public bool OneLineDescription;
        /// <summary>The footer sits in the text column under the description (an expander's header), not in a strip under a hairline.</summary>
        public bool InlineFooter;
        /// <summary>A live description (a bound line: a download's progress) in place of <see cref="Description"/>: one text run relays out, nothing re-renders.</summary>
        public Prop<string>? LiveDescription;
        /// <summary>The header row wraps: a wide control (a pair of buttons) sits beside the words while they keep <see cref="WinUiSettingsRules.WrapLabelBasis"/>,
        /// and drops under them where they would not. ONE tree at every width (a form switch would re-parent the control and remount it).</summary>
        public bool WrapControl;
    }

    /// <summary>A plain card of words and a control (a composite's own line: the census rows, the update status), the arguments of the engine's SettingsCard.</summary>
    static Element PlainCard(string label, string? sub, Element? control = null, CardForm form = CardForm.Right, Action? onClick = null, bool isEnabled = true,
        string? actionIcon = null)
        => DialCard(NewCard(label, sub, control) with { Form = form, OnClick = onClick, Enabled = isEnabled, ActionIcon = actionIcon });

    /// <summary>A plain expander item (a composite's own item: a fact, a door, a button).</summary>
    static Element PlainItem(string label, string? sub, Element? control = null, CardForm form = CardForm.Right, bool isEnabled = true, Action? onClick = null)
        => DialCard(ItemCard(label, sub, control, isEnabled, onClick) with { Form = form });

    /// <summary>A card with the default card style, enabled.</summary>
    static Card NewCard(string label, string? description, Element? control = null)
        => new(label, description, control, SettingsCard.DefaultStyle) { Enabled = true, WrapLabel = true };

    /// <summary>The row card (see the file header).</summary>
    static Element DialCard(in Card c)
    {
        var s = c.Style;
        var layout = s_layout;
        bool clickable = c.OnClick is not null && c.Enabled;
        bool stack = c.Form == CardForm.Vertical
            || (c.Form == CardForm.Right && c.Control is { } ctl && layout.StacksControls && !IsToggle(ctl) && !c.WrapControl);

        // The label line: the label, then the badge where the dial has one.
        var labelText = new TextEl(c.Label)
        {
            Size = s.HeaderFontSize, Color = c.Enabled ? s.Foreground : s.ForegroundDisabled, DisabledColor = s.ForegroundDisabled,
            Wrap = c.WrapLabel ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = c.WrapLabel ? 0 : 1, Trim = c.WrapLabel ? TextTrim.None : TextTrim.CharacterEllipsis,
            Shrink = 1f, MinWidth = 0f,
        };
        Element label = c.Explain is { } explain
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiSettingsRules.ExplainGap, MinWidth = 0f,
                Children = [labelText, Slots.View(ViewKeys.Explainer, explain)],
            }
            : labelText;

        var text = new List<Element>(3) { label };
        if (c.LiveDescription is { } live)
            text.Add(new TextEl(live)
            {
                Size = s.DescriptionFontSize, Color = c.Enabled ? s.DescriptionForeground : s.ForegroundDisabled, DisabledColor = s.ForegroundDisabled,
                Wrap = TextWrap.Wrap, MinWidth = 0f,
            });
        else if (c.Description is { Length: > 0 } d)
            text.Add(new TextEl(d)
            {
                Size = s.DescriptionFontSize, Color = c.Enabled ? s.DescriptionForeground : s.ForegroundDisabled, DisabledColor = s.ForegroundDisabled,
                Wrap = c.OneLineDescription ? TextWrap.NoWrap : TextWrap.Wrap, MaxLines = c.OneLineDescription ? 1 : 0,
                Trim = c.OneLineDescription ? TextTrim.CharacterEllipsis : TextTrim.None, MinWidth = 0f,
            });

        if (c.InlineFooter && c.Footer is { } inlineFooter) text.Add(inlineFooter);

        var head = new List<Element>(4);
        if (c.Glyph is { } glyph)
            head.Add(new BoxEl
            {
                Width = s.HeaderIconSize, Height = s.HeaderIconSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Margin = new Edges4(0f, 0f, s.HeaderIconMarginRight, 0f),
                Children = [new TextEl(glyph) { Size = s.HeaderIconSize, FontFamily = Tok.Definition.Fonts.Icon, Color = c.Enabled ? s.Foreground : s.ForegroundDisabled }],
            });
        head.Add(new BoxEl
        {
            Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f, MinWidth = 0f,
            // A wrapping header's gap (between its lines) also stands between the words and the control: the margin gives it back.
            Margin = new Edges4(0f, 0f, c.Control is null && !clickable ? 0f : s.HeaderMarginRight - (c.WrapControl ? WinUiSettingsRules.WrapLineGap : 0f), 0f),
            Children = text.ToArray(),
        });
        if (c.WrapControl)
        {
            // The glyph and the words wrap as one cell, so the control is what drops to the next line.
            var lead = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Basis = WinUiSettingsRules.WrapLabelBasis, MinWidth = 0f, Children = head.ToArray() };
            head.Clear();
            head.Add(lead);
        }

        Element? control = c.Control is { } content
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = stack ? FlexJustify.Start : FlexJustify.End, Shrink = 0f,
                Grow = stack ? 1f : 0f, MinWidth = 0f, AlignSelf = stack ? FlexAlign.Stretch : FlexAlign.Auto,
                OnRealized = c.OnControl,
                Children = [content],
            }
            : null;
        if (!stack && control is not null) head.Add(control.Value);
        if (c.Trailing is { } trailing)
            head.Add(new TextEl(trailing) { Size = s.DescriptionFontSize, Color = s.DescriptionForeground, Shrink = 0f, MaxLines = 1, Margin = new Edges4(WinUiSettingsRules.RecentWhenGap, 0f, 0f, 0f) });
        if (clickable)
            head.Add(new BoxEl
            {
                Width = SettingsCard.ActionIconMaxSize, Height = SettingsCard.ActionIconMaxSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Margin = new Edges4(s.ActionIconMarginLeft, 0f, 0f, 0f),
                Children = [new TextEl(c.ActionIcon ?? Icons.ChevronRightMed) { Size = SettingsCard.ActionIconMaxSize, FontFamily = Tok.Definition.Fonts.Icon, Color = s.Foreground }],
            });

        Element body;
        if (c.Form == CardForm.Left)
            body = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, OnRealized = c.OnControl, Children = [c.Control ?? new BoxEl()] };
        else if (stack && control is not null)
            body = new BoxEl
            {
                Direction = 1, Gap = SettingsCard.VerticalHeaderContentSpacing, MinWidth = 0f,
                Children = [new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = head.ToArray() }, control.Value],
            };
        else
            body = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = c.WrapControl, Gap = c.WrapControl ? WinUiSettingsRules.WrapLineGap : 0f,
                Children = head.ToArray(),
            };

        var inner = new BoxEl
        {
            Direction = 1, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = s.MinHeight, Padding = s.Padding,
            Children = [body],
        };
        return new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Corners = CornerRadius4.All(s.CornerRadius), ClipToBounds = c.Footer is not null && !c.InlineFooter,
            Fill = c.Enabled ? s.Background : s.BackgroundDisabled,
            HoverFill = clickable ? s.BackgroundPointerOver : c.Enabled ? s.Background : s.BackgroundDisabled,
            PressedFill = clickable ? s.BackgroundPressed : c.Enabled ? s.Background : s.BackgroundDisabled,
            BorderWidth = s.BorderWidth, BorderColor = c.Enabled ? s.Border : s.BorderDisabled,
            HoverBorderBrush = clickable ? s.BorderPointerOver : null,
            BrushTransitionMs = s.BrushTransitionMs,
            Role = clickable ? AutomationRole.Button : AutomationRole.None, Focusable = clickable, FocusVisualMargin = WinUiSettingsRules.CardFocusMargin,
            IsEnabled = c.Enabled, OnClick = clickable ? c.OnClick : null, Cursor = clickable ? CursorId.Hand : CursorId.Arrow,
            Children = c.Footer is { } footer && !c.InlineFooter ? [inner, new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Padding = WinUiSettingsRules.AlsoInPadding, Children = [footer] }] : [inner],
        };
    }

    /// <summary>The expander header's card: the row card in the expander's header style (no fill or border of its own: the expander paints the group).</summary>
    static TemplateParts HeaderParts(Element header)
    {
        var parts = new TemplateParts();
        parts.Set<Element>(SettingsExpander.PartHeaderCard, _ => header);
        return parts;
    }

    /// <summary>A SettingsExpander whose header is the row card <paramref name="header"/> (its label line can carry the badge).</summary>
    static Element Expander(in Card header, IReadOnlyList<Element> items, Signal<bool>? expanded, bool initiallyExpanded = false,
        Element? itemsHeader = null, Element? itemsFooter = null)
    {
        Card h = header with { Style = SettingsExpander.DefaultStyle.HeaderCardStyle, OnClick = null, InlineFooter = true };
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = h.Label, Description = h.Description, Content = h.Control, Items = items, IsExpanded = expanded, InitiallyExpanded = initiallyExpanded,
            ItemsHeader = itemsHeader, ItemsFooter = itemsFooter, Parts = HeaderParts(DialCard(h)),
        });
    }

    /// <summary>An expander item's card: the item style (58 lead, 52 tall, square corners), the clickable item's where it is a door.</summary>
    static Card ItemCard(string label, string? description, Element? control = null, bool enabled = true, Action? onClick = null)
    {
        var style = onClick is null ? SettingsExpander.DefaultStyle.ItemCardStyle : SettingsExpander.DefaultStyle.ClickableItemCardStyle;
        return new Card(label, description, control, style) { Enabled = enabled, OnClick = onClick, WrapLabel = true };
    }

    // ══ 3. SECTION HEADERS, TAGS, PANELS ══════════════════════════════════════════════════════════════════════════

    /// <summary>The key every section header carries.</summary>
    const string SectionKeyPrefix = "settings:section:";

    /// <summary>A section header: Body Strong title over a 12-px secondary caption (W-Settings: 14 / 600 over 12), 2 apart; the record's space above (none
    /// above the body's first) and under is the header's own padding, so a crossing that changes it reveals it (`Design.Reveal.Resize`).</summary>
    static Element SectionHeader(string id, string title, string? subtitle, bool first)
    {
        var layout = s_layout;
        bool hasSub = subtitle is { Length: > 0 };
        return new BoxEl
        {
            Key = SectionKeyPrefix + id, Direction = 1, Gap = Spacing.XXS, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = new Edges4(0f, first ? 0f : layout.SectionTop, 0f, layout.SectionBottom),
            Animate = Design.Reveal.Resize,
            Children = hasSub
                ?
                [
                    BodyStrong(title) with { MinWidth = 0f },
                    Caption(subtitle!) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis },
                ]
                : [BodyStrong(title) with { MinWidth = 0f }],
        };
    }

    /// <summary>N4: a collapsed group's ANSWER in its header — 14 DIP secondary, one line, char-ellipsis.</summary>
    static Element ValueTag(string value) => new TextEl(value)
    {
        Size = WinUiSettingsRules.ValueTagSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>Wide content (a card strip) for an expander's <c>ItemsHeader</c>: padding (16, 12, 16, 12), no fill or border (the expander paints the group).</summary>
    static Element ExpanderPanel(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
        Children = [content],
    };

    /// <summary>A footer under an expander's items: a hairline above, then the content (the engine puts dividers between items but none before a footer).</summary>
    static Element ExpanderFooter(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }, content],
    };

    /// <summary>A tertiary caption aligned with the items' text column (58 DIP from the left edge).</summary>
    static Element FooterNote(string text) => new BoxEl
    {
        Direction = 0, AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(WinUiSettingsRules.TextColumnInset, Spacing.M, Spacing.L, Spacing.M),
        Children = [Caption(text) with { Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap }],
    };

    /// <summary>N11: every destructive action confirms through the ONE dialog shape, Cancel the default button.</summary>
    static void ConfirmThen(string title, string body, string primaryText, Action onConfirm)
        => Controls.Confirm(SettingsContext.Overlay, title, body, primaryText, onConfirm);

    /// <summary>A dial's description now (its reason while it is greyed with one, else its caption).</summary>
    static string? DescriptionOf(SettingsDial dial) => dial.DescriptionKey(SettingsFacts.Live) is { } key ? Loc.Get(key) : null;

    /// <summary>The registry's dial <paramref name="id"/>.</summary>
    static SettingsDial Dial(string id) => SettingsSpec.Dial(id);

    // ══ 4. THE CONTROLS AT THE TIER'S TARGET ══════════════════════════════════════════════════════════════════════

    /// <summary>The marker key of a toggle's cell: a toggle keeps the label | state | switch row where the tier stacks every other control under its label.</summary>
    const string ToggleKey = "settings:toggle";

    static bool IsToggle(Element? control) => control.Is(out BoxEl toggle) && toggle.Key == ToggleKey;

    /// <summary>The compact switch (36 tall) of a row with its On / Off word, in a 48 x 40 cell where the tier asks for a hit target.</summary>
    static Element Toggle(Signal<bool> isOn, Action<bool>? onChange = null, bool isEnabled = true)
    {
        var layout = s_layout;
        var style = ToggleSwitch.DefaultStyle with { MinWidth = layout.ToggleMinWidth, MinHeight = layout.ToggleMinHeight };
        return new BoxEl
        {
            Key = ToggleKey, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [ToggleSwitch.Create(isOn, onChange, onContent: Loc.Get(Strings.Theme.Winui.Screens.On), offContent: Loc.Get(Strings.Theme.Winui.Screens.Off), isEnabled: isEnabled, style: style)],
        };
    }

    /// <summary>A switch over a dial: a FRESH signal seeded from the dial's value, the dial's writer, then the page bump when the writer asks for it.</summary>
    static Element DialToggle(SettingsDial dial, bool isEnabled = true)
        => Toggle(new Signal<bool>(dial.Current is true), on =>
        {
            if (dial.Writer.Toggle is { } toggle && toggle(on)) Settings.Bump();
        }, isEnabled);

    /// <summary>A standard button; a Large (40) one where the tier asks for a hit target.</summary>
    static BoxEl StdButton(string label, Action onClick, bool isEnabled = true) => Btn(label, onClick, ButtonAppearance.Standard, isEnabled);

    /// <summary>An accent button.</summary>
    static BoxEl AccentButton(string label, Action onClick, bool isEnabled = true) => Btn(label, onClick, ButtonAppearance.Accent, isEnabled);

    /// <summary>A subtle button.</summary>
    static BoxEl SubtleButton(string label, Action onClick, bool isEnabled = true) => Btn(label, onClick, ButtonAppearance.Subtle, isEnabled);

    static BoxEl Btn(string label, Action onClick, ButtonAppearance appearance, bool isEnabled)
        => s_layout.Touch
            ? Button.Create(label, onClick, appearance, ControlSize.Large, isEnabled: isEnabled)
            : Button.Create(label, onClick, appearance, isEnabled: isEnabled);

    /// <summary>A link no smaller than the tier's hit target (40 at Compact).</summary>
    static BoxEl LinkButton(string text, Action onClick, bool isEnabled = true)
        => Lifted(HyperlinkButton.Create(text, onClick, isEnabled: isEnabled));

    /// <summary>A link that opens <paramref name="navigateUri"/>, no smaller than the tier's hit target.</summary>
    static BoxEl LinkButton(string text, string navigateUri) => Lifted(HyperlinkButton.Create(text, navigateUri));

    /// <summary>A control root lifted to the tier's hit target (a link, a clickable row's cell); the look is untouched.</summary>
    static BoxEl Lifted(BoxEl box)
        => s_layout.Touch ? box with { MinHeight = s_layout.Target, MinWidth = s_layout.Target } : box;

    /// <summary>A segmented selector (the board's pill row); its items are the tier's hit target tall where it asks for one.</summary>
    static Element Selector(IReadOnlyList<string> items, Signal<int> selected, Action<int>? onChange = null)
        => s_layout.Touch
            ? SelectorBar.Create(items, selected, onChange, style: new SelectorBarStyle { ItemHeight = s_layout.Target })
            : SelectorBar.Create(items, selected, onChange);

    /// <summary>A slider whose cross size is the tier's hit target (the engine's own 32 elsewhere).</summary>
    static Element SliderControl(FloatSignal? value, Action<float>? onChange, Slider.SliderOptions? options, float length = 200f, bool isEnabled = true)
        => s_layout.Touch
            ? Slider.Create(value, onChange, options, length, thickness: s_layout.Target, isEnabled: isEnabled)
            : Slider.Create(value, onChange, options, length, isEnabled: isEnabled);

    /// <summary>A radio item at the tier's hit target.</summary>
    static BoxEl Radio(string label, bool isSelected, Action? onChange, bool isEnabled = true)
        => s_layout.Touch
            ? RadioButton.Create(label, isSelected, onChange, RadioButton.DefaultStyle with { MinHeight = s_layout.Target }, isEnabled)
            : RadioButton.Create(label, isSelected, onChange, isEnabled: isEnabled);

    /// <summary>A choice over <paramref name="selectedIndex"/>: the stock ComboBox, at a hit target its Large (40-DIP) field filling the stacked row with the
    /// touch rows. ONE tree at every tier: the combo's size, width and touch rows are live props of the mounted combo and its column is always there (it hugs
    /// the field where the tier asks for no target), so a crossing re-sizes the SAME combo instead of remounting it.</summary>
    static Element Combo(IReadOnlyList<string> items, Signal<int> selectedIndex, float width = 220f, bool isEnabled = true, Action<int>? onChange = null,
                         IReadOnlyList<string>? itemDescriptions = null, IReadOnlyList<bool>? itemEnabled = null)
    {
        bool touch = s_layout.Touch;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Grow = touch ? 1f : 0f, AlignSelf = touch ? FlexAlign.Stretch : FlexAlign.Auto,
            Children = [ComboBox.Create(items, selectedIndex, width: touch ? float.NaN : width, isEnabled: isEnabled, onChange: onChange, touchInputMode: touch,
                itemDescriptions: itemDescriptions, itemEnabled: itemEnabled, size: touch ? ControlSize.Large : ControlSize.Medium)],
        };
    }

    /// <summary>A NumberBox whose field is the tier's hit target tall (the engine's own 32 elsewhere). ONE key at every tier: the style is a live prop of
    /// the mounted box, so a crossing re-sizes the SAME field (its text, caret and focus survive).</summary>
    static Element NumberField(Signal<double> value, Action<double> onChange, NumberBox.NumberBoxOptions options, string key)
        => NumberBox.Create(value, onChange, s_layout.Touch ? options with { Style = NumberBox.DefaultStyle with { FieldHeight = s_layout.Target } } : options with { Style = null })
            with { Key = key };

    // ══ 5. GLYPHS ═════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly HashSet<string> s_unmappedGlyphs = new(StringComparer.Ordinal);

    /// <summary>A registry glyph NAME → its <see cref="Icons"/> constant. An unmapped name logs <c>settings.glyph.unmapped</c> once and paints the gear.</summary>
    static string Glyph(string? name) => name switch
    {
        null => Icons.Settings,
        "Globe" => Icons.Globe, "Link" => Icons.Link, "Devices" => Icons.Devices, "Device" => Icons.Device,
        "Code" => Icons.Code, "LocaleLanguage" => Icons.LocaleLanguage, "OpenInNewWindow" => Icons.OpenInNewWindow,
        "Settings" => Icons.Settings, "Clock" => Icons.Clock, "Document" => Icons.Document, "Refresh" => Icons.Refresh,
        "Brush" => Icons.Brush, "Sun" => Icons.Sun, "Zoom" => Icons.Zoom, "Font" => Icons.Font, "Design" => Icons.Design, "Home" => Icons.Home,
        "List" => Icons.List, "RowSize" => Icons.RowSize, "Picture" => Icons.Picture, "ViewList" => Icons.ViewList,
        "DockLeft" => Icons.DockLeft, "SplitView" => Icons.SplitView, "Edit" => Icons.Edit, "Microphone" => Icons.Microphone,
        "RefineSparkle" => Icons.RefineSparkle, "Filter" => Icons.Filter, "MusicNote" => Icons.MusicNote,
        "Headphones" => Icons.Headphones, "RadioTower" => Icons.RadioTower, "Volume" => Icons.Volume, "Play" => Icons.Play,
        "Speakers" => Icons.Speakers, "Equalizer" => Icons.Equalizer, "Audio" => Icons.Audio, "Movie" => Icons.Movie,
        "TvMonitor" => Icons.TvMonitor, "Pin" => Icons.Pin, "ThisPc" => Icons.ThisPc, "Album" => Icons.Album,
        "Folder" => Icons.Folder, "FolderOpen" => Icons.FolderOpen, "Tag" => Icons.Tag, "Delete" => Icons.Delete,
        "Attention" => Icons.Attention, "Download" => Icons.Download, "ChromeClose" => Icons.ChromeClose,
        "ChromeMinimize" => Icons.ChromeMinimize, "RevealPassword" => Icons.RevealPassword, "Contact" => Icons.Contact,
        "Info" => Icons.Info, "StatusWarning" => Icons.StatusWarning, "Camera" => Icons.Camera, "Forward" => Icons.Forward,
        "Shield" => Icons.Shield, "Warning" => Icons.Warning, "Repair" => Icons.Repair, "Bell" => Icons.Bell, "Moon" => Icons.Moon,
        _ => UnmappedGlyph(name),
    };

    static string UnmappedGlyph(string name)
    {
        if (s_unmappedGlyphs.Add(name))
            Log.Warn("settings", "settings.glyph.unmapped: Settings glyph name has no Icons mapping; using the default glyph (name=" + name + ")");
        return Icons.Settings;
    }
}
