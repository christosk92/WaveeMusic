// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Body.cs — a category's column drawn from the registry's body (settings rework phase 4) ─────────────
//
// Boards: Z-Settings-Appearance, Z-Settings-Playback, Z-Settings-Sections, Z-Theme-Options (every size). The body is the core's (`SettingsPageProps.Body.Of`: the
// groups in order, each with the dials the live facts compose, worded, with values, writers, enablement, children, explanation badges and "also in" places); this
// file draws it in Zune's unchanged grammar: a 26/32 light group heading (its caption beside it) over label | control rows (200 | rest at Standard, the control
// under the label at Compact), chips for short choices, square switches, ruled combos, line sliders, and a "reset" word beside a heading.
//
//   · a ROW is keyed `zs:row:<dialId>` (a link's arrival finds it): its label (the accent while it is the arrival's), an accent "see it" word after the label when
//     the dial explains itself (design 6.5: the clip opens under the row, `ZuneSettings.Explain.cs`), the description under it (the reason while it is greyed,
//     else the caption), its "also in" places, and its control;
//   · a dial's CHILDREN follow it as rows of their own (Zune draws no expanders: every row is open);
//   · a run of plain actions (no words of their own, no confirmation, no children) is one row of command words (the board's "manage on spotify.com · profile ·
//     log out");
//   · Zune's own COMPOSITE sections stay Zune's (the account card, the AI lyrics card, the equalizer curve, the runtime, the attached videos, the Storage census
//     and budget, the crash reporting, the saved reports, the update hero, the receipts, the licences, the feedback actions, the key caps): each is drawn where its
//     dial lives (`Composite`), and draws its dial's children itself.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>What every row of one category's column is drawn with: the tier's kit, the page's props (the Storage census, the arrival) and the facts. A paint
    /// for a row outside the page (<see cref="OutsideRow"/>: the setup wizard) has no page: no arrival, no "also in" places, and it draws only the rows whose
    /// picture reads nothing of the page.</summary>
    sealed class Paint(ZuneKit k, SettingsPageProps? props, string category)
    {
        public ZuneKit K { get; } = k;
        public SettingsPageProps Props => props ?? throw new InvalidOperationException("A row drawn outside the Settings page reads no page props.");
        public string Category { get; } = category;
        public SettingsArrival Focus => props?.Focus ?? SettingsArrival.None;
        /// <summary>The row is drawn outside the page (<see cref="OutsideRow"/>).</summary>
        public bool Outside => props is null;
        public ISettingsFacts Facts { get; } = SettingsFacts.Live;
    }

    /// <summary>The open category's column: its heading (from Small up; at Compact its name is the page's panorama) over its groups.</summary>
    static Element CategoryColumn(ZuneKit k, SettingsPageProps props, int index, bool heading)
    {
        var lay = k.Lay;
        SettingsBody body = props.Body.Of(index);
        var d = new Paint(k, props, body.Category.Slug);
        var kids = new List<Element>(body.Groups.Length + 1);
        if (heading) kids.Add(SectionHeading(body.Category.Title));
        foreach (var group in body.Groups) kids.Add(GroupBlock(d, group));
        return new BoxEl
        {
            Key = "zs:column", Direction = 1, Gap = Z.SectionGap, AlignSelf = FlexAlign.Start, MinWidth = 0f,
            Basis = lay.ColumnW, MaxWidth = lay.ColumnW, Grow = lay.ColumnGrows ? 1f : 0f, Shrink = 1f, Animate = ZuneMotion.ColumnCollapse,
            Children = kids.ToArray(),
        };
    }

    /// <summary>One group: its heading (the theme options' "reset" beside it), what the group leads with (the Storage census, the notification banners), then
    /// its rows; a theme option group's sub-heading over its options.</summary>
    static Element GroupBlock(Paint d, SettingsBodyGroup group)
    {
        var k = d.K;
        var rows = new List<Element>(group.Rows.Length + 2);
        GroupLead(d, group, rows);
        Element? beside = null;
        string? subgroup = null;
        bool options = IsOptionsGroup(group);
        var links = new List<Element>(4);
        foreach (var row in group.Rows)
        {
            if (Skipped(row.Id)) continue;
            // The theme options' reset is the "reset" word beside the group's heading (Z-Theme-Options).
            if (options && row.Option is null && row.Subgroup is null && row.Kind == SettingsRowKind.Action && row.Confirm is not null)
            {
                beside = ConfirmCmd(k, row, Loc.Get(Strings.Settings.ThemeOptions.Reset)) with { Key = SettingsKeys.ZuneRow(row.Id), OnRealized = NoteRow(row.Id) };
                continue;
            }
            if (row.Subgroup is { } sub && !string.Equals(sub, subgroup, StringComparison.Ordinal))
            {
                FlushLinks(d, links, rows);
                subgroup = sub;
                rows.Add(ZuneText.Cased(R.Type.Eyebrow(sub), LabelKind.Heading) with
                {
                    Key = "zs:sub:" + group.Id + ":" + sub, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Margin = new Edges4(0f, 0f, 0f, -Z.RowGap + Z.SubgroupGap),
                });
            }
            if (row.NoteBefore is { } before) { FlushLinks(d, links, rows); rows.Add(NoteOf(before)); }
            if (IsPlainAction(row))
            {
                links.Add(LinkCmd(d, row));
                continue;
            }
            FlushLinks(d, links, rows);
            DrawRow(d, row, rows);
            if (row.NoteAfter is { } after) rows.Add(NoteOf(after));
        }
        FlushLinks(d, links, rows);
        return k.Block("zs:group:" + d.Category + ":" + group.Id, group.Title, group.Subtitle, rows, beside);
    }

    /// <summary>The theme options' group (the registry's live group of the active theme's options).</summary>
    static bool IsOptionsGroup(SettingsBodyGroup group)
    {
        foreach (var row in group.Rows)
            if (row.Option is not null) return true;
        return false;
    }

    /// <summary>A run of plain actions as one wrapping row of command words.</summary>
    static void FlushLinks(Paint d, List<Element> links, List<Element> into)
    {
        if (links.Count == 0) return;
        into.Add(new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Z.LinkGap, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            // The commands' padding stands their words in from the column; the row stands back so the first word sits on the column's edge.
            Margin = new Edges4(-Z.CmdPadding.Left, 0f, 0f, 0f),
            Children = links.ToArray(),
        });
        links.Clear();
    }

    /// <summary>An action with nothing to say beside its own name: no description, no confirmation, no children, no picture of its own.</summary>
    static bool IsPlainAction(SettingsBodyRow row)
        => row.Kind == SettingsRowKind.Action && row.Description is null && row.Confirm is null && row.Children.IsEmpty && row.Explain is null
           && row.AlsoIn.IsEmpty && !HasComposite(row.Id);

    /// <summary>A plain action as a command word, keyed as its row (What's new carries the unread dot while the running release's notes were never opened).</summary>
    static Element LinkCmd(Paint d, SettingsBodyRow row)
    {
        var writer = row.Writer;
        Element cmd = d.K.Cmd(row.Label, () => { if (writer.Run?.Invoke() == true) Settings.Bump(); }, row.Enabled);
        if (string.Equals(row.Id, WhatsNewDial, StringComparison.Ordinal) && ReleaseNotes.RunningNotesUnread())
            cmd = new BoxEl { Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [cmd, InfoBadge.Dot()] };
        return new BoxEl { Key = SettingsKeys.ZuneRow(row.Id), Direction = 0, Shrink = 0f, OnRealized = NoteRow(row.Id), Children = [cmd] };
    }

    const string WhatsNewDial = "help.whatsNew";

    /// <summary>A note a row carries (the sidebar layout file could not be read, what a migration dropped): the engine's square info bar in Zune's style.</summary>
    static Element NoteOf(SettingsBodyNote note)
        => ZuneKit.Notice(note.Glyph == "Warning" ? InfoBarSeverity.Warning : InfoBarSeverity.Informational, note.Title, note.Text ?? "") with { Key = note.Key };

    /// <summary>One row and, after it, its children's rows (a composite draws its own children).</summary>
    static void DrawRow(Paint d, SettingsBodyRow row, List<Element> into)
    {
        if (Skipped(row.Id)) return;
        if (Composite(d, row) is { } composite)
        {
            into.Add(composite);
            return;
        }
        if (row.Option is { } option)
        {
            Element? control = OptionControl(d.K, option, d.Facts, out bool optionEnabled);
            into.Add(DialRow(d, row, control, row.Description, optionEnabled));
        }
        else into.Add(DialRow(d, row, Control(d, row), row.Description));
        foreach (var child in row.Children) DrawRow(d, child, into);
    }

    // ── a row outside the page ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The app mode row (system · light · dark), as Settings draws it and as the setup wizard's theme step shows it: one row, one writer.</summary>
    internal static Element AppModeRow(ZuneKit k) => OutsideRow(k, "appearance.appMode");

    /// <summary>The language row, as Settings draws it and as the setup wizard's theme step shows it: one row, one writer.</summary>
    internal static Element LanguageRow(ZuneKit k) => OutsideRow(k, "system.language");

    /// <summary>The audio quality row, as Settings draws it and as the setup wizard's playback step shows it: one row, one writer.</summary>
    internal static Element QualityRow(ZuneKit k) => OutsideRow(k, "sound.quality");

    /// <summary>A dial for a surface outside the page: the registry's row (<see cref="SettingsPageModel.RowOf"/>) drawn as the page draws it, its children after
    /// it, with a paint that has no page (<see cref="Paint.Outside"/>). Nothing shows while the facts compose the dial away.</summary>
    static Element OutsideRow(ZuneKit k, string dialId)
    {
        if (SettingsPageModel.RowOf(SettingsSpec.Dial(dialId)) is not { } row) return new BoxEl { Visible = false };
        string category = SettingsSpec.TryPlace(row.Dial, out SettingsCategory placed, out _) ? placed.Slug : "";
        var rows = new List<Element>(1 + row.Children.Length);
        DrawRow(new Paint(k, null, category), row, rows);
        return rows.Count == 1 ? rows[0] : new BoxEl { Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = rows.ToArray() };
    }

    // ── the row ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A dial's row: its label (lit on arrival), "see it" when it explains itself (its clip under the row), the description, its "also in" places and
    /// its control; keyed <c>zs:row:&lt;dialId&gt;</c> and remembered for the arrival.</summary>
    static BoxEl DialRow(Paint d, SettingsBodyRow row, Element? control, string? hint, bool? enabled = null, string? label = null)
    {
        string key = SettingsKeys.ZuneRow(row.Id);
        Element labelEl = LabelOf(d.Focus, row.Id, ZuneText.Label(label ?? row.Label));
        Element? seeIt = row.Explain is not null ? SeeIt(d.K, row.Id) : null;
        BoxEl line = d.K.Row(key, labelEl, hint, control, enabled ?? row.Enabled, after: seeIt, below: AlsoIn(d, row), onControl: NoteControl(row.Id));
        if (row.Explain is not { } info) return line with { OnRealized = NoteRow(row.Id) };
        return new BoxEl
        {
            Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
            Children = [line with { Key = key + ":line" }, ExplainSlot(info)],
        };
    }

    /// <summary>"also in: queue · full screen › options" in the hint voice under the description: each place with a door of its own is a word that opens it.
    /// None outside the page (its doors lead away from the surface the row is drawn on).</summary>
    static Element? AlsoIn(Paint d, SettingsBodyRow row)
    {
        if (d.Outside || row.AlsoIn.IsEmpty || row.AlsoInLine is not { } line) return null;
        bool doors = false;
        foreach (var place in row.AlsoIn) doors |= place.Open is not null;
        if (!doors) return ZuneText.Hint(line) with { Key = SettingsKeys.ZuneRow(row.Id) + ":also" };
        var kids = new List<Element>(row.AlsoIn.Length + 1) { ZuneText.Hint(Strings.Settings.AlsoIn.Line("").TrimEnd()) with { Shrink = 0f, Wrap = TextWrap.NoWrap } };
        foreach (var place in row.AlsoIn)
            kids.Add(place.Open is { } open
                ? d.K.Cmd(place.Place, open) with { Key = SettingsKeys.ZuneRow(row.Id) + ":also:" + place.Surface }
                : ZuneText.Hint(place.Place) with { Shrink = 0f, Wrap = TextWrap.NoWrap, Margin = Z.CmdPadding });
        return new BoxEl { Key = SettingsKeys.ZuneRow(row.Id) + ":also", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    // ── the controls ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The control a dial's kind draws: a switch, chips or a combo (by its words), a line slider, a command word, its value as words, or none.</summary>
    static Element? Control(Paint d, SettingsBodyRow row)
    {
        var k = d.K;
        bool enabled = row.Enabled;
        SettingsDial dial = row.Dial;
        var writer = row.Writer;
        switch (row.Kind)
        {
            case SettingsRowKind.Toggle:
                return k.Switch(row.Value is true, on => { if (writer.Toggle?.Invoke(on) == true) Settings.Bump(); }, enabled);

            case SettingsRowKind.Choice:
            case SettingsRowKind.Segmented:
            case SettingsRowKind.Picker:
            {
                IReadOnlyList<string> labels = ChoiceLabels(row);
                if (labels.Count == 0) return ValueWords(row);
                return k.Choice(row.Id, new ZuneChoice(labels, () => dial.Current is int at ? at : 0, i => { if (writer.Choose?.Invoke(i) == true) Settings.Bump(); },
                    Chips: row.Kind == SettingsRowKind.Segmented, Enabled: enabled ? null : s_disabled, Width: ComboWidthOf(row.Id)));
            }

            case SettingsRowKind.Slider:
                return k.Slide(row.Id, SlideOf(row));

            case SettingsRowKind.Action:
                return row.Confirm is not null ? ConfirmCmd(k, row, Loc.Get(row.Confirm.PrimaryKey)) : k.Cmd(row.Label, () => { if (writer.Run?.Invoke() == true) Settings.Bump(); }, enabled);

            case SettingsRowKind.Fact:
                return ValueWords(row);

            default:
                return null;
        }
    }

    static readonly Func<bool> s_disabled = static () => false;

    /// <summary>A command that asks first (the ONE confirm dialog, Cancel the default), then runs the dial's writer.</summary>
    static BoxEl ConfirmCmd(ZuneKit k, SettingsBodyRow row, string word)
    {
        var confirm = row.Confirm!;
        var writer = row.Writer;
        return k.Cmd(word, () => ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey),
            () => { if (writer.Run?.Invoke() == true) Settings.Bump(); }), row.Enabled);
    }

    /// <summary>A confirmed destructive action: the ONE confirm dialog, Cancel the default.</summary>
    static void ConfirmThen(string title, string body, string primary, Action onConfirm)
        => Controls.Confirm(SettingsContext.Overlay, title, body, primary, onConfirm);

    /// <summary>A fact's value in words (a name, a chord, a number), or none.</summary>
    static Element? ValueWords(SettingsBodyRow row)
    {
        string words = row.Value switch
        {
            null => "",
            string s => s,
            bool on => Loc.Get(on ? Strings.Settings.Value.On : Strings.Settings.Value.Off),
            int i when (uint)i < (uint)row.Choices.Length => row.Choices[i],
            IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
            var other => other.ToString() ?? "",
        };
        return words.Length == 0 ? null : R.Type.TrackTitle(words) with { Color = Tok.TextPrimary, MinWidth = 0f, Wrap = TextWrap.Wrap, Grow = 1f };
    }

    /// <summary>The words a choice offers: the registry's, or the ones Zune spells for a dial whose choices are not words (video heights).</summary>
    static IReadOnlyList<string> ChoiceLabels(SettingsBodyRow row) => row.Id switch
    {
        "playback.video.quality" => [Loc.Get(Strings.Settings.Playback.VideoQualityAuto), "180p", "240p", "320p", "480p", "720p", "1080p"],
        "playback.video.meteredQuality" => [Loc.Get(Strings.Settings.Playback.VideoQualityUnlimited), "480p", "720p", "1080p"],
        "storage.metadata.budget" => Settings.StorageFormat.MetaBudgetLabels,
        _ => row.Choices,
    };

    /// <summary>A combo's width: the wide field for the long words (qualities, faces, styles, devices, adapters), the short one for the sizes, else the stock.</summary>
    static float ComboWidthOf(string id) => id switch
    {
        "sound.quality" or "sound.meteredQuality" or "sound.outputDevice" or "lyrics.playerStyle" or "lyrics.stage.visualizer"
            or "playback.video.quality" or "playback.video.meteredQuality" or "system.gpu" => Z.WideComboWidth,
        "storage.metadata.budget" => Z.BudgetComboWidth,
        _ => Z.ComboWidth,
    };

    /// <summary>A slider dial's range, step and words (the registry carries the value and the writer; the range is the control's).</summary>
    static ZuneSlide SlideOf(SettingsBodyRow row)
    {
        SettingsDial dial = row.Dial;
        var writer = row.Writer;
        Action<float> commit = v => writer.Slide?.Invoke(v);
        Func<bool>? enabled = row.Enabled ? null : s_disabled;
        float Live(float fallback) => dial.Current is float f ? f : fallback;
        return row.Id switch
        {
            "playback.crossfade.duration" => new ZuneSlide(0f, (float)Settings.Quality.CrossfadeMaxSeconds, Z.CrossfadeStep, () => Live(0f), commit,
                static v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s", static () => SettingsFacts.Live.CrossfadeEnabled),
            "lyrics.blur" => new ZuneSlide(0f, Z.BlurMax, Z.BlurStep, () => Live(0f), commit, Percent, enabled),
            "lyrics.stage.heroDim" => new ZuneSlide(0f, Z.DimMax, Z.DimStep, () => Live(0f), commit, Percent, enabled),
            "lyrics.stage.sensitivity" => new ZuneSlide(Z.SensitivityMin, Z.SensitivityMax, Z.SensitivityStep, () => Live(1f), commit,
                static v => ((int)MathF.Round(v * 100f)).ToString(CultureInfo.InvariantCulture) + "%", enabled),
            "lyrics.stage.syncOffset" => new ZuneSlide(Z.SyncOffsetMin, Z.SyncOffsetMax, Z.SyncOffsetStep, () => Live(0f), commit,
                static v => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + " ms", enabled),
            "playback.episodeSpeed" => new ZuneSlide(Z.SpeedMin, Z.SpeedMax, Z.SpeedStep, () => Live(1f), commit,
                static v => v.ToString("0.##", CultureInfo.InvariantCulture) + "×", enabled),
            _ => row.Option is { } g
                ? new ZuneSlide(g.Min, g.Max, g.Step, () => g.Live() is float f ? f : g.Min, commit, v => SliderText(v, g.UnitKey), enabled)
                : new ZuneSlide(0f, Z.SliderFallbackMax, Z.SliderFallbackStep, () => Live(0f), commit, static v => v.ToString("0.##", CultureInfo.InvariantCulture), enabled),
        };
    }

    static string Percent(float v) => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + "%";
}
