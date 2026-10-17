// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Kit.cs — Zune's row grammar and controls for the screens (themes plan 6.3, P5 Z8b) ─────
//
// The words and controls every Zune screen (Settings, setup, release notes, the dialogs) is written with. Zune writes its words in the display
// voice at the light weights and in lowercase: a label, a heading and a command take their casing from `R.Case` (the roles lower them; a name or a
// title someone typed is never passed through `Cased`), and every number is a role or a rule (`ZuneSettingsRules`).
//
// THE ROW GRAMMAR (boards Z-Settings-Appearance, Z-Settings-Playback, Z-Settings-Sections, Z-Theme-Options): a row is a label column (15/20, the hint
// under it in the micro voice) beside its control column, 200 | rest, 24 apart; where the tier stacks its rows (Compact) the control sits under the
// label. The same two columns in every tier, only their direction and widths are live props, so a crossing moves the SAME nodes.
//
// THE CONTROLS are the engine's, drawn by Zune's control table (`ZuneControls`): a switch, a chip row (`Segmented`), a ruled combo, a line slider, a
// square check box. The stateful ones (a combo freezes its items, signal and handler at mount, a chip row and a slider keep one signal for their life)
// are small cells that keep their signal in step with the document through an effect, and re-read it on the page epoch (`Settings.PageEpoch`): the
// page never rebuilds them, a writer's `Settings.Bump()` reaches them.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's words: the roles' voices, cased by the style's case roles.</summary>
internal static class ZuneText
{
    /// <summary>The element cased as <paramref name="kind"/> says (Zune lowers every word the theme writes; a typed name is never passed through).</summary>
    public static TextEl Cased(TextEl text, LabelKind kind) => text with { Case = R.Case.For(kind) };

    /// <summary>A row's label: the 15/20 body voice, wrapping.</summary>
    public static TextEl Label(string text) => Cased(R.Type.TrackTitle(text), LabelKind.Label) with { MinWidth = 0f, Wrap = TextWrap.Wrap };

    /// <summary>The line under a label: the micro voice in the tertiary ink, wrapping.</summary>
    public static TextEl Hint(string text) => Cased(R.Type.MicroMeta(text), LabelKind.Label) with { Color = Tok.TextTertiary, MinWidth = 0f, Wrap = TextWrap.Wrap };

    /// <summary>Body copy: the 13/18 voice in the secondary ink, wrapping. Prose the theme writes (a sentence), so it keeps its casing.</summary>
    public static TextEl Prose(string text) => R.Type.DenseMeta(text) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap };

    /// <summary>A short meta word: the 13/18 voice, secondary, one line.</summary>
    public static TextEl Meta(string text) => Cased(R.Type.DenseMeta(text), LabelKind.Label) with { Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

    /// <summary>A section heading: 26/32 at the light weight, lowercase.</summary>
    public static TextEl Heading(string text) => Cased(R.Type.Heading(text), LabelKind.Heading) with { MinWidth = 0f, Wrap = TextWrap.Wrap };

    /// <summary>A group heading: 22/28 at the light weight, lowercase.</summary>
    public static TextEl Group(string text) => Cased(R.Type.SheetTitle(text), LabelKind.Heading) with { MinWidth = 0f, Wrap = TextWrap.Wrap };

    /// <summary>A page or plate title: 40/48 at the light weight, lowercase.</summary>
    public static TextEl Title(string text) => Cased(R.Type.PageHero(text), LabelKind.Heading) with { MinWidth = 0f, Wrap = TextWrap.Wrap };

    /// <summary>A navigation word at the section list's size (the 20/28 display voice).</summary>
    public static TextEl Nav(string text) => Cased(R.Type.Wordmark(text), LabelKind.Navigation) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

    /// <summary>A command's word: the 13/18 voice, lowercase.</summary>
    public static TextEl Command(string text) => Cased(R.Type.DenseMeta(text), LabelKind.Command) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
}

/// <summary>A stateful card of the screens: it subscribes to the tier facts (so a crossing re-renders it) and reads its layout record from them.</summary>
internal abstract class ZuneScreenComponent : CommonTierComponent
{
    TierFacts _tier;

    /// <summary>The tier facts this render was built from (set by <see cref="UseLayout"/>).</summary>
    protected TierFacts Tier => _tier;

    /// <summary>Call first in <c>Render</c>: the settings record of the tier on screen now.</summary>
    protected ZuneSettingsLayout UseLayout() => ZuneSettingsTiers.Of(_tier = ReadTier());
}

/// <summary>What a choice cell draws and writes.</summary>
/// <param name="Labels">The choices' words, in order.</param>
/// <param name="Current">The selected index NOW (read each render: the document is the truth).</param>
/// <param name="Pick">The writer: called with the index picked.</param>
/// <param name="Chips">The choices are short enough to sit as a row of chips (the tier may still fold them into a combo).</param>
/// <param name="Enabled">Whether the control is live (read each render).</param>
/// <param name="Descriptions">A combo's per-item descriptions, or null.</param>
/// <param name="ItemEnabled">A combo's per-item enablement, or null.</param>
/// <param name="Width">The combo's width where the row does not stack.</param>
internal sealed record ZuneChoice(IReadOnlyList<string> Labels, Func<int> Current, Action<int> Pick, bool Chips, Func<bool>? Enabled = null,
    IReadOnlyList<string>? Descriptions = null, IReadOnlyList<bool>? ItemEnabled = null, float Width = Z.ComboWidth)
{
    /// <summary>The node key a choice cell mounts under: the words it was built from (a combo freezes its items).</summary>
    public string KeyFor(string id) => "zs:choice:" + id + ":" + string.Join('|', Labels);
}

/// <summary>What a slider cell draws and writes.</summary>
internal sealed record ZuneSlide(float Min, float Max, float Step, Func<float> Current, Action<float> Commit, Func<float, string> Format,
    Func<bool>? Enabled = null);

/// <summary>The layout-dependent controls and rows of the screens.</summary>
internal sealed class ZuneKit
{
    /// <summary>The tier's record this kit builds with.</summary>
    public ZuneSettingsLayout Lay { get; }

    public ZuneKit(ZuneSettingsLayout lay) => Lay = lay;

    /// <summary>The kit of the tier facts the page is drawn in.</summary>
    public static ZuneKit Of(in TierFacts tiers) => new(ZuneSettingsTiers.Of(tiers));

    /// <summary>The page lays its controls out for touch (the Compact rule: every hit target is at least 40).</summary>
    public bool Touch => Lay.Target > 0f;

    // ── rows ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One row: the label column (label, hint) beside the control column; stacked where the tier says. ONE tree in every tier.</summary>
    public Element Row(string key, string label, string? hint, Element? control, bool enabled = true)
    {
        bool stack = Lay.StacksRows;
        var labelKids = new List<Element>(2) { ZuneText.Label(label) };
        if (hint is { Length: > 0 }) labelKids.Add(ZuneText.Hint(hint));
        var labels = new BoxEl
        {
            Key = key + ":label", Direction = 1, Gap = Z.LabelGap, MinWidth = 0f, Shrink = 0f,
            Width = stack ? float.NaN : Lay.LabelW, Children = labelKids.ToArray(),
        };
        var controls = new BoxEl
        {
            Key = key + ":control", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinWidth = 0f,
            Grow = 1f, Shrink = 1f, Basis = stack ? float.NaN : 0f, AlignSelf = FlexAlign.Stretch,
            Children = control is null ? [] : [control],
        };
        return new BoxEl
        {
            Key = key, Direction = (byte)(stack ? 1 : 0), Gap = stack ? Z.RowStackGap : Z.RowColumnGap,
            AlignItems = stack ? FlexAlign.Stretch : FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = Lay.Target,
            Opacity = enabled ? 1f : Z.DisabledOpacity,
            Children = [labels, controls],
        };
    }

    /// <summary>A row drawn from a document row: its title, its description (the reason while it is greyed with one, else its caption; <paramref name="sub"/>
    /// overrides it for a live sentence) and its enablement. The control is the view's.</summary>
    public Element DocRow(SettingsRow row, Element? control, string? sub = null, bool? enabled = null)
    {
        var facts = SettingsFacts.Live;
        string? description = sub ?? (row.DescriptionKey(facts) is { } key ? Loc.Get(key) : null);
        return Row(RowKey(row), Loc.Get(row.TitleKey), description, control, enabled ?? row.Enabled(facts).Enabled);
    }

    /// <summary>The node key of a document row's row.</summary>
    public static string RowKey(SettingsRow row) => "zs:row:" + (int)row.Tab + ":" + row.Id;

    /// <summary>A section: its heading (with the caption beside it on the baseline) over its rows.</summary>
    public Element Block(string key, string title, string? sub, IReadOnlyList<Element> rows)
    {
        var head = new List<Element>(2) { ZuneText.Heading(title) with { Shrink = 0f, Wrap = TextWrap.NoWrap } };
        if (sub is { Length: > 0 }) head.Add(ZuneText.Meta(sub) with { Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.WordEllipsis });
        var kids = new List<Element>(rows.Count + 1)
        {
            new BoxEl
            {
                Key = key + ":head", Direction = 0, Wrap = true, AlignItems = FlexAlign.End, Gap = Z.HeadGap, MinWidth = 0f,
                Children = head.ToArray(),
            },
        };
        kids.AddRange(rows);
        return new BoxEl
        {
            Key = key, Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    // ── switches, checks, chips, combos, sliders ──────────────────────────────────────────────────────────────────────────

    /// <summary>A switch over a document row: its value, its writer, then the page bump when the writer asks for it.</summary>
    public Element Toggle(SettingsRow row, bool enabled = true)
        => Switch(row.Current is true, on => { if (row.Writer.Toggle!(on)) Settings.Bump(); }, enabled);

    /// <summary>A switch with its own writer. A fresh signal per render carries the live value (the switch re-pushes its props).</summary>
    public Element Switch(bool on, Action<bool>? onChange, bool enabled = true) => new BoxEl
    {
        Key = "zs:switch", Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, MinHeight = Lay.Target, MinWidth = Lay.Target,
        Children = [ToggleSwitch.Create(new Signal<bool>(on), onChange, isEnabled: enabled)],
    };

    /// <summary>A check box (a square 18) with its words, at least the tier's target tall.</summary>
    public Element Check(string label, Signal<bool> isChecked, Action<bool>? onChange = null, bool enabled = true)
    {
        var parts = new TemplateParts();
        parts.Set<TextEl>(CheckBox.PartLabel, t => ZuneText.Cased(t, LabelKind.Label) with { Wrap = TextWrap.Wrap, MinWidth = 0f, Shrink = 1f });
        var style = Touch ? CheckBox.DefaultStyle with { MinHeight = Lay.Target } : CheckBox.DefaultStyle;
        return CheckBox.Create(label, isChecked, onChange, style: style, isEnabled: enabled, parts: parts);
    }

    /// <summary>A choice cell: chips where the choices are short and the tier has the room, else the ruled combo.</summary>
    public Element Choice(string id, ZuneChoice choice)
        => Embed.Comp(() => new ChoiceCell(choice)) with { Key = choice.KeyFor(id) };

    /// <summary>A choice cell over a document row: the row's current index and its <c>Choose</c> writer, then the page bump when the writer asks for it.</summary>
    public Element Choice(SettingsRow row, IReadOnlyList<string> labels, bool chips, IReadOnlyList<string>? descriptions = null, IReadOnlyList<bool>? itemEnabled = null, float width = Z.ComboWidth, bool? enabled = null)
        => Choice(row.Tab + "." + row.Id, new ZuneChoice(labels,
            () => row.Current is int at ? at : 0,
            i => { if (row.Writer.Choose!(i)) Settings.Bump(); },
            chips, enabled is { } e ? () => e : null, descriptions, itemEnabled, width));

    /// <summary>A slider cell with its value beside it.</summary>
    public Element Slide(string id, ZuneSlide slide) => Embed.Comp(() => new SliderCell(slide)) with { Key = "zs:slide:" + id };

    /// <summary>The ruled combo over a caller-owned signal (a combo freezes its items, signal and handler at mount: the caller keeps the signal in step). At a hit
    /// target it is the Large (40) field filling the stacked row with the touch rows.</summary>
    public Element Combo(IReadOnlyList<string> items, Signal<int> selected, float width, bool enabled = true, Action<int>? onChange = null,
        IReadOnlyList<string>? descriptions = null, IReadOnlyList<bool>? itemEnabled = null)
    {
        bool touch = Touch;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Grow = touch ? 1f : 0f, AlignSelf = touch ? FlexAlign.Stretch : FlexAlign.Auto,
            Children =
            [
                ComboBox.Create(items, selected, width: Lay.StacksRows ? float.NaN : width, isEnabled: enabled, onChange: onChange,
                    touchInputMode: touch, itemDescriptions: descriptions, itemEnabled: itemEnabled,
                    size: touch ? ControlSize.Large : ControlSize.Medium),
            ],
        };
    }

    // ── commands and discs ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A command: a lowercase word in the secondary ink that turns to the primary on hover, at least the tier's target tall.</summary>
    public BoxEl Cmd(string text, Action onClick, bool enabled = true, bool accent = false) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinHeight = Lay.Target, MinWidth = Lay.Target, Padding = Z.CmdPadding,
        Role = AutomationRole.Button, Focusable = enabled, Cursor = enabled ? CursorId.Hand : default, OnClick = enabled ? onClick : null,
        Children =
        [
            ZuneText.Command(text) with
            {
                Color = !enabled ? Tok.TextDisabled : accent ? Tok.AccentDefault : Tok.TextSecondary,
                HoverColor = !enabled ? Tok.TextDisabled : accent ? Tok.AccentSecondary : Tok.TextPrimary,
                FocusedColor = !enabled ? Tok.TextDisabled : Tok.TextPrimary,
            },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>A command that opens a web address.</summary>
    public BoxEl Link(string text, string uri) => Cmd(text, () => SettingsVerbs.OpenUri(uri));

    /// <summary>The round accent button with its word beside it (the board's "go" disc): the disc carries the arrow, the word sits to its right.</summary>
    public BoxEl Go(string label, Action onClick, bool enabled = true, string glyph = Icons.Forward) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.DiscGap, Shrink = 0f, MinHeight = Lay.Target,
        Role = AutomationRole.Button, Focusable = enabled, Cursor = enabled ? CursorId.Hand : default, OnClick = enabled ? onClick : null,
        Children =
        [
            Disc(glyph, enabled),
            ZuneText.Cased(R.Type.TrackTitle(label), LabelKind.Command) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Color = enabled ? Tok.TextPrimary : Tok.TextDisabled },
        ],
    };

    /// <summary>The round accent disc with a glyph (36, the Medium metro button).</summary>
    public static BoxEl Disc(string glyph, bool enabled = true) => new BoxEl
    {
        Width = Z.DiscSize, Height = Z.DiscSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(ZuneControls.Round), Fill = enabled ? Tok.AccentDefault : Tok.AccentDisabled,
        HitTestVisible = false,
        Children = [Icon(glyph, Z.DiscGlyph, enabled ? Tok.TextOnAccentPrimary : Tok.TextOnAccentDisabled)],
    };

    /// <summary>A pair of status words under a banner: an engine info bar (square, no ring in Zune) with the words as the page wrote them.</summary>
    public static Element Notice(InfoBarSeverity severity, string title, string message, Element? action = null)
        => InfoBar.Create(severity, title, message, isClosable: false, actionButton: action);

    /// <summary>A quiet word row for a fact (label | value).</summary>
    public Element Fact(string key, string label, string? value) => Row(key, label, null,
        R.Type.DenseMeta(Z.OrDash(value)) with { Color = Tok.TextPrimary, MinWidth = 0f, Wrap = TextWrap.Wrap, Grow = 1f });

    /// <summary>A progress line of the tier's width.</summary>
    public static Element Bar(float fraction, float width, ProgressBarState state = ProgressBarState.Normal)
        => ProgressBar.Determinate(fraction, width, state);

    // ══ the stateful cells ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One choice: a row of chips, or the ruled combo. It keeps ONE signal for its life (the combo freezes items, signal and handler at mount)
    /// and re-seeds it from the document on every page epoch.</summary>
    sealed class ChoiceCell(ZuneChoice choice) : ZuneScreenComponent
    {
        readonly Signal<int> _selected = new(choice.Current());

        public override Element Render()
        {
            var lay = UseLayout();
            _ = Settings.PageEpoch.Value;   // a writer's Bump (and any other writer of the document) re-reads the choice
            _ = ThemeHost.Options.EpochSignal.Value;   // so does a theme option written from the palette
            int current = choice.Current();
            UseEffect(() => _selected.SetIfChanged(current), DepKey.From(current));
            bool enabled = choice.Enabled?.Invoke() ?? true;

            if (choice.Chips && lay.ChipsStay)
            {
                var items = new SegmentedItem[choice.Labels.Count];
                for (int i = 0; i < items.Length; i++) items[i] = new SegmentedItem(choice.Labels[i]);
                return new BoxEl
                {
                    Direction = 0, MinWidth = 0f, MinHeight = lay.Target, AlignItems = FlexAlign.Center, Opacity = enabled ? 1f : Z.DisabledOpacity,
                    HitTestVisible = enabled,
                    Children = [Segmented.Create(items, _selected, choice.Pick, new Segmented.SegmentedOptions { IsEnabled = enabled })],
                };
            }

            return new ZuneKit(lay).Combo(choice.Labels, _selected, choice.Width, enabled, choice.Pick, choice.Descriptions, choice.ItemEnabled);
        }
    }

    /// <summary>One slider with its value: a line slider (one signal for its life, re-seeded from the document on every page epoch) and the formatted value.</summary>
    sealed class SliderCell(ZuneSlide slide) : ZuneScreenComponent
    {
        readonly FloatSignal _value = new(slide.Current());
        readonly Slider.SliderOptions _options = new()
        {
            Min = slide.Min, Max = slide.Max, Step = slide.Step, TickFrequency = MathF.Max(slide.Step, (slide.Max - slide.Min) / Z.SliderTicks),
            IsThumbToolTipEnabled = true, ThumbToolTipValueConverter = v => slide.Format(v),
        };

        public override Element Render()
        {
            var lay = UseLayout();
            _ = Settings.PageEpoch.Value;
            _ = ThemeHost.Options.EpochSignal.Value;
            float current = slide.Current();
            UseEffect(() => _value.SetIfChanged(current), DepKey.From(current));
            bool enabled = slide.Enabled?.Invoke() ?? true;
            var meta = R.Type.DenseMeta("");
            Element slider = lay.Target > 0f
                ? Slider.Create(_value, v => slide.Commit(v), _options, lay.SliderLength, thickness: lay.Target, isEnabled: enabled)
                : Slider.Create(_value, v => slide.Commit(v), _options, lay.SliderLength, isEnabled: enabled);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Shrink = 0f, Opacity = enabled ? 1f : Z.DisabledOpacity,
                Children =
                [
                    slider,
                    new TextEl(Prop.Of(() => slide.Format(_value.Value)))
                    {
                        Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary, MaxLines = 1, MinWidth = Z.SliderValueWidth, Shrink = 0f,
                    },
                ],
            };
        }
    }
}
