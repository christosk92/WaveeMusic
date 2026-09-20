using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The customizer's small shared surfaces: the anchored "…" menu button every row uses, the Settings-vocabulary property
// row (§C4.6 reuses the Settings language verbatim so the panel needs no new visual system), and the three CONTROLLED
// option rows the property panel is built from.
//
// THE CONTROLLED-INPUT PATTERN used by every row here: the row owns its control signal, reads the DOCUMENT during render
// (which is what subscribes it to LayoutVersion) and mirrors that value into its signal from a LAYOUT EFFECT — never from
// render (a render-time signal write is a backwards write). A user edit writes the signal and dispatches; the reducer's
// answer flows back through the same mirror, so a REJECTED edit visibly snaps back instead of leaving a lying control.
//
// Each row is mounted under a Key that carries the section id (props freeze at mount), so selecting another section
// REMOUNTS the rows against the new subject.
//
// PHASE 2 / DECISION B — RE-HOSTED, NOT FORKED. Every row here used to take `SidebarCustomizerPage`; it now takes
// `ISidebarEditHost`, the small interface that page already satisfied (Prefs / Acts / Registry / OverlaySvc /
// RejectEpoch / Selected / Dispatch / Select). Nothing about the rows' BODIES changed — the point is that the same
// controls can now also be mounted by the live pane's per-section options POPOVER (P3: options live on the object,
// not in a docked panel whose subject silently changes), driven by the shared `SidebarEditSession`. Forking this file
// per host would have duplicated the controlled-input + rejection contract, which is exactly the "same artifact defined
// twice" failure the sidebar architecture exists to prevent.

/// <summary>An icon button that opens a <c>MenuFlyout</c> built at OPEN time (never at render time — resolving labels in
/// a render subscribes the row to the culture epoch; the landed <c>SidebarLayoutMenu</c> note).</summary>
sealed class CzMenuButton : Component
{
    readonly string _glyph;
    readonly Func<IReadOnlyList<MenuFlyoutItem>> _items;
    readonly float _box;

    public CzMenuButton(string glyph, Func<IReadOnlyList<MenuFlyoutItem>> items, float box = 24f)
    {
        _glyph = glyph; _items = items; _box = box;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = _items();
            if (items.Count == 0) return;
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                                 Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return new BoxEl
        {
            Width = _box, Height = _box, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [Icon(_glyph, 14f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
    }
}

/// <summary>The property panel's row vocabulary — hand-rolled, NOT <c>SettingsCard</c>.
/// <para>WHY (round-2 defect 3): <c>SettingsCard</c>'s header lane has no line cap. In a 320-DIP inspector column
/// "Show in collapsed rail" plus its 11f sublabel wrapped to three lines and jammed against the ToggleSwitch, because the
/// card gives its content lane whatever it asks for and the header lane whatever is left. These rows instead enforce ONE
/// two-column contract: the label column is <c>Grow=1 · MinWidth=0 · MaxLines 2 · ellipsis</c>, the control column is
/// <c>Shrink=0</c> and right-aligned, and every row has the same 10-DIP vertical padding and 44-DIP floor.</para></summary>
static class CzRow
{
    /// <summary>Vertical padding shared by every row, so a group card reads as evenly-pitched rows.</summary>
    const float RowPadY = 10f;

    /// <summary>Every row's height floor. A one-line row lands exactly here; a two-line row grows from it.</summary>
    const float RowMinHeight = 44f;

    /// <summary>An 11f/600 UPPERCASE tertiary group label — the flat-group heading. The four groups used to be
    /// <c>SettingsExpander</c> accordions whose headers rendered as literal <c>[sidebar.customizer.group.*]</c>; the group
    /// is now ALWAYS OPEN, so the label is plain text with nothing to toggle and no disclosure state to remember.
    /// <para>The catalog authors these in sentence case and the UI keeps that casing verbatim — the old edge
    /// upper-casing was exactly the transform the eyebrow role gave up (it is wrong to author, and wrong to APPLY, in
    /// the languages whose casing rules Invariant does not model).</para></summary>
    /// <remarks>Returns <see cref="TextEl"/>, not <see cref="Element"/>, so a caller can <c>with</c>-tweak its flex
    /// props (the group head row needs <c>Grow=1</c> to let the trailing caption sit flush right).</remarks>
    public static TextEl GroupLabel(string text) => WaveeType.Eyebrow(text) with
    {
        Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>One always-open property group: the uppercase label (plus an optional trailing caption — round-2 defect 4
    /// puts the item count THERE rather than in a bare "0 items" body row) above ONE card of rows.
    /// <para>The card carries a hairline (<c>StrokeCardDefault</c>) and a slightly stronger fill than before, because on
    /// the dark wash a fill-only group did not read as a card at all (round-2 defect 3).</para></summary>
    public static Element Group(string labelKey, IReadOnlyList<Element> items, string? caption = null) => new BoxEl
    {
        Direction = 1, Shrink = 0f, Gap = Spacing.XS,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 0f),
                Children =
                [
                    GroupLabel(Loc.Get(labelKey)) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
                    caption is { Length: > 0 }
                        ? new TextEl(caption)
                        {
                            Size = 11f, Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
                        }
                        : new BoxEl { Width = 0f },
                ],
            },
            new BoxEl
            {
                Direction = 1, Shrink = 0f, Corners = Radii.ControlAll,
                Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                ClipToBounds = true,
                Children = [.. items],
            },
        ],
    };

    /// <summary>A LABEL + CONTROL row: label (and optional sublabel) left, control right, on the two-column contract in
    /// the class summary. <paramref name="icon"/> and <paramref name="onClick"/> keep the old <c>SettingsCard</c>-era
    /// signature so every existing call site compiles unchanged.</summary>
    public static Element Prop(string label, string? sub, Element? control, string? icon = null,
                               bool enabled = true, Action? onClick = null)
    {
        var kids = new List<Element>(3);
        if (icon is { Length: > 0 }) kids.Add(Icon(icon, 16f, Tok.TextSecondary));
        kids.Add(LabelColumn(label, sub));
        if (control is not null)
            kids.Add(new BoxEl
            {
                Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Children = [control],
            });
        if (onClick is not null) kids.Add(Icon(Icons.ChevronRight, 12f, Tok.TextTertiary));

        return Frame(kids, enabled, onClick);
    }

    /// <summary>A row whose control needs the FULL width (a segmented bar, a combo box, a uri list): label above, control
    /// below, same padding and floor as <see cref="Prop"/> so the two mix inside one group card without a rhythm break.</summary>
    public static Element Wide(string label, string? sub, Element? control, bool enabled = true)
    {
        Element[] stack = control is null
            ? [LabelColumn(label, sub)]
            : [LabelColumn(label, sub), control];
        return Frame(
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 6f,
                    Children = stack,
                },
            ],
            enabled, null, vertical: true);
    }

    /// <summary>The label column: 13f title over an optional 11f tertiary sublabel, both capped at two lines with an
    /// ellipsis. This cap IS the defect-3 fix — it is the only thing standing between a long localization and a row that
    /// pushes its control off the panel.</summary>
    static Element LabelColumn(string label, string? sub)
    {
        var lines = new List<Element>(2)
        {
            new TextEl(label)
            {
                Size = 13f, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap,
                Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (sub is { Length: > 0 })
            lines.Add(new TextEl(sub)
            {
                Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                Trim = TextTrim.CharacterEllipsis,
            });
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Gap = 1f,
            Justify = FlexJustify.Center,
            Children = [.. lines],
        };
    }

    static Element Frame(IReadOnlyList<Element> kids, bool enabled, Action? onClick, bool vertical = false)
    {
        var box = new BoxEl
        {
            Direction = (byte)(vertical ? 1 : 0),
            Shrink = 0f,
            MinHeight = RowMinHeight,
            Gap = Spacing.M,
            AlignItems = vertical ? FlexAlign.Stretch : FlexAlign.Center,
            Padding = new Edges4(Spacing.M, RowPadY, Spacing.M, RowPadY),
            Opacity = enabled ? 1f : 0.4f,
            IsEnabled = enabled,
            Children = [.. kids],
        };
        if (onClick is null || !enabled) return box;
        return (box with
        {
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
        }).Interactive(Interaction.Subtle);
    }

    /// <summary>The row's subject, AND the subscription that makes it live. It reads BOTH epochs on purpose:
    /// <list type="bullet">
    /// <item><c>LayoutVersion</c> — an accepted edit, an undo/redo, an external write.</item>
    /// <item><c>RejectEpoch</c> — a REJECTED edit (round-2 defect 1a). A rejection does not bump LayoutVersion, so before
    /// this the row never re-rendered, its mirror effect never re-ran, and the control kept showing the value the user
    /// picked while the document still held the old one. Pair it with <see cref="Epoch"/> in the mirror's dep key.</item>
    /// </list></summary>
    public static SidebarSectionSpec? Subject(ISidebarEditHost page, string sectionId)
    {
        var prefs = page.Prefs;
        _ = page.RejectEpoch.Value;
        if (prefs is null) return null;
        _ = prefs.LayoutVersion.Value;
        return prefs.Layout.Find(sectionId);
    }

    /// <summary>The document+rejection epoch a controlled row must fold into its mirror dep key, so the mirror re-runs on
    /// every answer the reducer gives — including "no".</summary>
    public static int Epoch(ISidebarEditHost page)
        => (page.Prefs?.LayoutVersion.Peek() ?? 0) * 397 + page.RejectEpoch.Peek();

    public static Element Header(string text) => new TextEl(text)
    {
        Size = 11f, Weight = 600, Color = Tok.TextTertiary, MaxLines = 1,
        Margin = new Edges4(2f, Spacing.M, 0f, 4f),
    };

    /// <summary>A row whose control is a full-width slider under a header line that carries the live VALUE on the right
    /// (R3.2 item 4's max-items row). Hand-rolled rather than a <c>SettingsCard</c> because the card's Vertical alignment
    /// puts the whole content below the header — there is no "caption right of the header" slot.</summary>
    public static Element Ranged(string label, string valueCaption, Element control) => new BoxEl
    {
        Direction = 1, Shrink = 0f, Gap = Spacing.XS,
        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(label)
                    {
                        Size = 13f, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(valueCaption)
                    {
                        Size = 12f, Weight = 600, Color = Tok.TextSecondary, Shrink = 0f, MaxLines = 1,
                    },
                ],
            },
            control,
        ],
    };

    // ── the ONE enum treatment (round-2 defect 2) ─────────────────────────────────────────────────────────────────────
    //
    // The panel had THREE indicator styles fighting: a Segmented plate, Segmented's own accent underline pill, and
    // SelectorBar's tab underline. The screenshot showed Density wearing a segmented box AND a blue underline at once.
    // Rules from here on:
    //   • short choices  → Segmented, with its selection PILL SUPPRESSED (the filled selected segment is the indicator)
    //   • long choices   → a real ComboBox dropdown
    //   • SelectorBar    → BANNED in the property panel. It is a page-level tab strip; in a 320-DIP column its four
    //                      "When empty" tabs clipped mid-word ("Sh…") because a tab strip cannot elide or wrap.

    /// <summary>Segmented, minus the accent underline. <c>SegmentedCore</c> paints BOTH a selected-segment plate and a
    /// 24×3 accent pill (Segmented.cs — the <c>pillSlot</c> under every selected item); two indicators for one value is
    /// the defect. The plate wins (it reads at a glance and matches the prototype), so the pill is styled to nothing
    /// through the control's public <c>PartSelectionPill</c> seam — no engine edit, and the 3-DIP slot stays put so
    /// suppressing it costs no relayout.</summary>
    static readonly TemplateParts SegmentedNoPill = new()
    {
        [Segmented.PartSelectionPill] = pill => pill with { Fill = ColorF.Transparent, Width = 0f },
    };

    /// <summary>The inspector's compact Segmented metrics (the stock 34-DIP/14f control is a page-level size).</summary>
    static Segmented.Style SegmentedCompact => Segmented.DefaultStyle with
    {
        Height = 30f,
        FontSize = 12f,
        ItemMinWidth = 40f,
        CornerRadius = Radii.Control,
        ItemCornerRadius = Radii.Control - 1f,
    };

    /// <summary>Longest resolved choice label that still fits a Segmented item in a 320-DIP column.</summary>
    const int SegmentedLabelBudget = 12;

    /// <summary>Most choices a Segmented may carry here.</summary>
    const int SegmentedChoiceBudget = 4;

    /// <summary>Render an enum choice set with the ONE treatment its labels earn. Deciding on the RESOLVED labels (never
    /// on the field) means a long localization demotes itself to the dropdown instead of clipping.</summary>
    public static Element Choice(string[] labels, Signal<int> index, Action<int> onChange, bool enabled = true)
    {
        bool segmented = labels.Length is > 0 and <= SegmentedChoiceBudget;
        for (int i = 0; i < labels.Length && segmented; i++)
            if (labels[i].Length > SegmentedLabelBudget) segmented = false;

        if (!segmented)
            return ComboBox.Create(labels, index, width: ComboWidth, isEnabled: enabled, onChange: onChange);

        var items = new SegmentedItem[labels.Length];
        for (int i = 0; i < labels.Length; i++) items[i] = new SegmentedItem(labels[i], IsEnabled: enabled);
        return Segmented.Create(items, index, onChange, new Segmented.SegmentedOptions
        {
            IsEnabled = enabled,
            Style = SegmentedCompact,
            Parts = SegmentedNoPill,
        });
    }

    /// <summary>One width for every dropdown / number / text control in the panel, so the right-hand column lines up
    /// instead of stair-stepping (round-2 defect 9). 320 column − 2 border − 16 inspector inset − 24 row padding = 278.</summary>
    public const float ComboWidth = 264f;

    /// <summary>The DESTRUCTIVE action's button (R3.2 item 4's "Remove section"). The engine has no
    /// <c>ButtonAppearance.Danger</c> arm, so this folds the app's ONE existing red pairing —
    /// <c>Tok.SystemFillCritical</c> ink on <c>Tok.SystemFillCriticalBackground</c>, the destructive swipe plate in
    /// <c>Components/RowSwipe.cs</c> — into the stock Subtle geometry through <c>Button</c>'s public palette seam. Red is
    /// a WARNING, never the safety: the confirmation and the undo step are what actually protect the document.</summary>
    public static BoxEl Danger(string label, Action onClick, string? glyph = null,
                              ControlSize size = ControlSize.Small)
        => Button.Create(label, onClick, ButtonAppearance.Subtle, size, glyph: glyph, palette: DangerPalette);

    static Button.ButtonPalette DangerPalette
    {
        get
        {
            var wash = Tok.SystemFillCriticalBackground;
            var ink = Tok.SystemFillCritical;
            return new Button.ButtonPalette(
                Background: new StateBrush(ColorF.Transparent, wash, wash with { A = wash.A * 0.75f },
                                           ColorF.Transparent),
                Foreground: new StateBrush(ink, ink, ink with { A = 0.85f }, Tok.TextDisabled),
                Border: Button.BorderRamp.Flat(GradientSpec.Solid(ColorF.Transparent)),
                Sizing: BackgroundSizing.InnerBorderEdge);
        }
    }
}

/// <summary>A <c>ToggleSwitch</c> display-option row (<c>SetDisplayOption</c> with 0/1).</summary>
sealed class CzToggleRow : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly SidebarDisplayField _field;
    readonly Signal<bool> _on = new(false);

    public CzToggleRow(ISidebarEditHost page, string sectionId, SidebarDisplayField field)
    {
        _page = page; _sectionId = sectionId; _field = field;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        bool value = SidebarDisplayValues.Read(spec?.Display, _field) != 0;
        // The mirror dep carries the EPOCH, not just the value: a rejected edit leaves `value` unchanged, so a
        // value-only dep would never re-run and the switch would keep the position the user dragged it to while the
        // document still said otherwise (round-2 defect 1a).
        UseLayoutEffect(() => _on.SetIfChanged(value), DepKey.From(value ? 1 : 0, CzRow.Epoch(_page)));

        return CzRow.Prop(Loc.Get(SidebarDisplayValues.LabelLocKey(_field)), SubLabel(_field),
            ToggleSwitch.Create(_on, v => _page.Dispatch(new SetDisplayOption(_sectionId, _field, v ? 1 : 0))));
    }

    /// <summary>The 11f explanatory sublabel, for the two flags whose consequence is not obvious from the label alone
    /// (R3.2 item 4). Only the keys the catalog actually carries are wired — a missing key would render as "[key]", which
    /// is precisely the bug this wave exists to remove, so every other flag stays label-only.</summary>
    internal static string? SubLabel(SidebarDisplayField field) => field switch
    {
        SidebarDisplayField.ShowInRail => Loc.Get("sidebar.option.showInRailSub"),
        SidebarDisplayField.CollapsedByDefault => Loc.Get("sidebar.option.collapsedSub"),
        _ => null,
    };
}

/// <summary>An enum display-option row: <c>CzRow.Choice</c> picks Segmented-without-pill or a ComboBox from the resolved
/// labels (round-2 defect 2). <c>SelectorBar</c> is gone from this panel entirely.</summary>
sealed class CzSelectorRow : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly SidebarDisplayField _field;
    readonly Signal<int> _index = new(0);

    public CzSelectorRow(ISidebarEditHost page, string sectionId, SidebarDisplayField field)
    {
        _page = page; _sectionId = sectionId; _field = field;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        int value = SidebarDisplayValues.Read(spec?.Display, _field);
        UseLayoutEffect(() => _index.SetIfChanged(value), DepKey.From(value, CzRow.Epoch(_page)));

        var keys = SidebarDisplayValues.ChoiceLocKeys(_field);
        var labels = new string[keys.Length];
        for (int i = 0; i < keys.Length; i++) labels[i] = Loc.Get(keys[i]);

        return CzRow.Wide(Loc.Get(SidebarDisplayValues.LabelLocKey(_field)), null,
            CzRow.Choice(labels, _index, Commit));
    }

    void Commit(int i) => _page.Dispatch(new SetDisplayOption(_sectionId, _field, i));
}

/// <summary>The <c>MaxItems</c> row (R3.2 item 4): a <c>Slider</c> over 0…<c>MaxItemsPerSection</c> whose live value rides
/// as the caption right of the header, so 0 can read as the WORD "All" — the thing a spinner structurally cannot show and
/// the reason the old <c>NumberBox</c> row had to explain itself in a description line. <c>GridColumns</c> keeps
/// <see cref="CzNumberRow"/>: two-to-four columns is a discrete pick, not a range to sweep.</summary>
sealed class CzSliderRow : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly SidebarDisplayField _field;
    readonly int _min, _max;
    readonly FloatSignal _value = new(0f);

    public CzSliderRow(ISidebarEditHost page, string sectionId, SidebarDisplayField field, int min, int max)
    {
        _page = page; _sectionId = sectionId; _field = field; _min = min; _max = max;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        int value = SidebarDisplayValues.Read(spec?.Display, _field);
        UseLayoutEffect(() => _value.SetIfChanged(value), DepKey.From(value, CzRow.Epoch(_page)));

        // 0 is not "none" for MaxItems — it is the sentinel for UNCAPPED, which the catalog spells as "All".
        string caption = value == 0
            ? Loc.Get("sidebar.option.maxItemsAll")
            : value.ToString(System.Globalization.CultureInfo.CurrentCulture);

        return CzRow.Ranged(Loc.Get(SidebarDisplayValues.LabelLocKey(_field)), caption,
            Slider.Create(_value, Commit, new Slider.SliderOptions
            {
                Min = _min, Max = _max, Step = 1f, SmallChange = 1f, LargeChange = 10f,
                IsThumbToolTipEnabled = true,
                ThumbToolTipValueConverter = ThumbCaption,
            }, length: SliderLength));
    }

    /// <summary>The inspector column is 320 DIP; the group card's rows inset 12 DIP a side, so this is the widest a
    /// fixed-length slider can be without pushing the card. (<c>Slider.Create</c> takes a LENGTH, not a stretch.)</summary>
    const float SliderLength = 272f;

    static string ThumbCaption(float v)
        => v <= 0f ? Loc.Get("sidebar.option.maxItemsAll")
                   : ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.CurrentCulture);

    void Commit(float v)
        => _page.Dispatch(new SetDisplayOption(_sectionId, _field, (int)MathF.Round(v)));
}

/// <summary>A <c>NumberBox</c> display-option row (max items / grid columns). <c>MaxItems</c> renders 0 as "All" in its
/// description, because a spinner cannot show a word.</summary>
sealed class CzNumberRow : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly SidebarDisplayField _field;
    readonly int _min, _max;

    public CzNumberRow(ISidebarEditHost page, string sectionId, SidebarDisplayField field, int min, int max)
    {
        _page = page; _sectionId = sectionId; _field = field; _min = min; _max = max;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        int value = SidebarDisplayValues.Read(spec?.Display, _field);
        string? sub = _field == SidebarDisplayField.MaxItems && value == 0
            ? Loc.Get("sidebar.option.maxItemsAll")
            : null;

        // NumberBox's affixes freeze at their first mount. Key the inner owner by the authoritative document value so
        // both arrows are born from the real 2-4 value, then remount after every accepted change.
        Element spinner = Embed.Comp(() => new CzNumberSpinner(
            _page, _sectionId, _field, _min, _max, value)) with
        {
            Key = "number:" + _sectionId + ":" + (int)_field + ":" + value,
        };
        return CzRow.Wide(Loc.Get(SidebarDisplayValues.LabelLocKey(_field)), sub, spinner);
    }
}

/// <summary>The keyed, authoritative owner of one NumberBox. A rejected/no-op edit snaps its local signal back
/// immediately; an accepted edit changes the document and remounts this component under the new value key.</summary>
sealed class CzNumberSpinner : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly SidebarDisplayField _field;
    readonly int _min, _max, _authoritative;
    readonly Signal<double> _value;

    public CzNumberSpinner(ISidebarEditHost page, string sectionId, SidebarDisplayField field,
        int min, int max, int authoritative)
    {
        _page = page;
        _sectionId = sectionId;
        _field = field;
        _min = min;
        _max = max;
        _authoritative = authoritative;
        _value = new Signal<double>(authoritative);
    }

    public override Element Render() => NumberBox.CreateWithSpinners(_value, Commit,
        new NumberBox.NumberBoxOptions
        {
            Minimum = _min, Maximum = _max, SmallChange = 1, Width = CzRow.ComboWidth,
        });

    void Commit(double value)
    {
        int next = SidebarNumberEdit.Normalize(value, _min, _max);
        var reason = _page.Dispatch(new SetDisplayOption(_sectionId, _field, next));
        if (reason != SidebarRejectReason.None) _value.SetIfChanged(_authoritative);
    }
}
