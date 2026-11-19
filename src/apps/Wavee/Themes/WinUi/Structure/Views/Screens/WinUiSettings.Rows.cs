// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Rows.cs — a category body over the registry, and the row host that plays a link's arrival ─────
//
// THE BODY (`CategoryBody`). A category is what the registry composes for it right now (`SettingsPageProps.Body.Of(category)`): its groups in order, each
// a section header (Body Strong over its caption) over its top-level rows; a row with children is an expander whose items are the children. WinUI's own
// placements over that model: the "WinUI options" group is drawn INSIDE the Theme card (W-Settings: the board's one expander holds the picker and the options),
// the Keyboard shortcuts group is one collapsed expander (owner decision 1), and a few groups lead with what the old pages drew above their rows (the
// Notifications block bar, the Storage census).
//
// THE ROW HOST (`DialRow`), one per row, keyed `settings:dial:<dialId>` (a child row too, inside its parent's expander). It picks the row's presenter
// (`Present`: a WinUI card of its own for the dials WinUI draws as composites, the generic card per kind for the rest), owns the expander's open state
// and plays the ARRIVAL of a link (design 4.9), each step in WinUI's own motion:
//   1. the page has opened the category already (the host moved it);
//   2. a row the arrival lists in `Expand` opens its expander (the expander's FlowReveal);
//   3. the target row glides into view, a third of the way down its scroller (`SceneStore.BringIntoView(node, 0.3, ScrollMove.Glide)`);
//   4. it takes the 2-DIP accent ring over the accent's subtle fill, which holds while it glides and then fades over `SettingsArrival.HighlightMs`
//      (`BrushTransitionMs`; reduced motion holds the whole time and cuts: a value, not a branch), and keyboard focus moves into its control.
// The ring is a layer OVER the row (never a border on the card: the card does not grow by a pixel). A target that never draws (a child of a card that is not
// in the form that shows it, the AI lyrics card before its setup) hands the arrival to its parent row after the expander's reveal. An arrival is played once
// (a root-lifetime mark), so a category opened again later does not replay it.

using System.Collections.Immutable;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    // ══ 1. THE BODY ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The body's column node: the tier tests and the lint find it.</summary>
    internal const string ColumnKey = "settings:column";

    /// <summary>The category's sections: one column of section headers over their cards, 4 DIP between cards.</summary>
    static Element CategoryBody(SettingsBody body, SettingsPageProps props)
    {
        var kids = new List<Element>(48);
        bool first = true;
        foreach (var group in body.Groups)
        {
            // The theme options live inside the Theme card (W-Settings); the card reads this group from the body.
            if (string.Equals(group.Id, OptionsGroupId, StringComparison.Ordinal) && HasGroup(body, ThemeGroupId)) continue;
            // The shortcuts are ONE collapsed expander whose header is the group's own title (owner decision 1): no section header over it.
            if (string.Equals(group.Id, ShortcutsGroupId, StringComparison.Ordinal))
            {
                kids.Add(Embed.Comp(new ShortcutsProps(group.Rows, body, props.Focus, props), static () => new ShortcutsGroup()) with { Key = "settings:shortcuts" });
                first = false;
                continue;
            }
            kids.Add(SectionHeader(group.Id, group.Title, group.Subtitle, first));
            first = false;
            AddGroupLead(kids, body, group, props);
            foreach (var row in group.Rows)
            {
                if (!Draws(row)) continue;
                if (row.NoteBefore is { } before) kids.Add(BodyNote(before));
                kids.Add(RowHost(row, body, props, item: false));
                if (row.NoteAfter is { } after) kids.Add(BodyNote(after));
            }
        }
        return new BoxEl { Key = ColumnKey, Direction = 1, Gap = WinUiSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
    }

    const string ThemeGroupId = "theme", OptionsGroupId = "options", ShortcutsGroupId = "shortcuts";

    static bool HasGroup(SettingsBody body, string id)
    {
        foreach (var g in body.Groups)
            if (string.Equals(g.Id, id, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The group <paramref name="id"/> of <paramref name="body"/>, or null.</summary>
    static SettingsBodyGroup? GroupOf(SettingsBody body, string id)
    {
        foreach (var g in body.Groups)
            if (string.Equals(g.Id, id, StringComparison.Ordinal)) return g;
        return null;
    }

    /// <summary>What a group draws under its header before its rows (the old pages' bars and blocks, now at their dial's group).</summary>
    static void AddGroupLead(List<Element> kids, SettingsBody body, SettingsBodyGroup group, SettingsPageProps props)
    {
        switch (body.Category.Slug, group.Id)
        {
            case ("notifications", "delivery"):
                if (NotifyBlockedBar() is { } blocked) kids.Add(blocked);
                break;
            case ("notifications", "topics"):
                kids.Add(TopicsHint());
                break;
            case ("storage", "onThisPc"):
                kids.Add(StorageCensus(props.Storage));
                break;
        }
    }

    /// <summary>A note a row carries (the sidebar file fault, what a migration dropped): an InfoBar, not a dial.</summary>
    static Element BodyNote(SettingsBodyNote note)
        => InfoBar.Create(note.Glyph == "Warning" ? InfoBarSeverity.Warning : InfoBarSeverity.Informational, note.Title, note.Text ?? "", isClosable: false)
            with { Key = note.Key };

    /// <summary>One row's host, keyed by its dial (see the file header).</summary>
    static Element RowHost(SettingsBodyRow row, SettingsBody body, SettingsPageProps props, bool item, Element? prebuilt = null)
        => Embed.Comp(new DialRowProps(row, body, item, props.Focus, props, prebuilt), static () => new DialRow()) with { Key = SettingsKeys.Dial(row.Id) };

    /// <summary>A child row of <paramref name="x"/>'s row as an expander item, hosted (its key, its ring, its arrival).</summary>
    static Element ChildHost(RowCtx x, SettingsBodyRow child, Element? prebuilt = null) => RowHost(child, x.Body, x.Page, item: true, prebuilt);

    /// <summary>The child of <paramref name="x"/>'s row with id <paramref name="id"/>, or null when the facts compose it away.</summary>
    static SettingsBodyRow? Child(RowCtx x, string id)
    {
        foreach (var c in x.Row.Children)
            if (string.Equals(c.Id, id, StringComparison.Ordinal)) return c;
        return null;
    }

    /// <summary>Host a bespoke item of a composite card as its child row (when the registry composes it), else the item bare.</summary>
    static Element HostedItem(RowCtx x, string childId, Element item) => Child(x, childId) is { } child ? ChildHost(x, child, item) : item;

    // ══ 2. THE ROW HOST ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a row host is handed.</summary>
    /// <param name="Row">The row.</param>
    /// <param name="Body">The body it is part of (a card that draws a sibling group reads it: the Theme card's options).</param>
    /// <param name="Item">It is an expander item (the item style, square corners), not a card of its own.</param>
    /// <param name="Arrival">The page's arrival.</param>
    /// <param name="Page">The page's props (the verbs, the storage census).</param>
    /// <param name="Prebuilt">A composite card's own item for this child row, drawn instead of a presenter's.</param>
    sealed record DialRowProps(SettingsBodyRow Row, SettingsBody Body, bool Item, SettingsArrival Arrival, SettingsPageProps Page, Element? Prebuilt)
    {
        /// <summary>The row's expander opens at mount (a results page whose result's child matched too).</summary>
        public bool Open { get; init; }
    }

    /// <summary>What a presenter is handed: the row, where it sits, its host's expander state and control-cell callback.</summary>
    sealed record RowCtx(SettingsBodyRow Row, SettingsBody Body, bool Item, Signal<bool> Expanded, Action<NodeHandle> OnControl, SettingsPageProps Page, TierFacts Tiers)
    {
        public SettingsArrival Arrival => Page.Focus;
    }

    /// <summary>The arrival last played on this root (its <see cref="SettingsArrival.Seq"/>): a body mounted again later plays nothing.</summary>
    static readonly RootState<int> s_arrivalPlayed = new("WinUi.Settings.ArrivalPlayed");

    sealed class DialRow : SettingsComponent
    {
        NodeHandle _node, _control;
        readonly Action<NodeHandle> _onNode, _onControl;

        public DialRow()
        {
            _onNode = h => _node = h;
            _onControl = h => _control = h;
        }

        public override Element Render()
        {
            var layout = UseLayout();
            var p = UseProps<DialRowProps>();
            var row = p.Row;
            var hooks = UseContext(InputHooks.Current);
            var expanded = UseSignal(p.Open || InitiallyOpen(row));
            var phase = UseSignal(0);                 // 0 at rest, 1 lit, 2 fading back
            var lit = UseSignal(0);                   // grows with every light-up (the timers' restart key)
            var latest = UseRef(p);
            latest.Value = p;

            void Land()
            {
                var a = latest.Value.Arrival;
                s_arrivalPlayed.Value = a.Seq;
                if (!_node.IsNull && Context.Scene is { } scene)
                    scene.BringIntoView(_node, WinUiSettingsRules.ArrivalAlign, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide);
                phase.Value = 1;
                lit.Value = lit.Peek() + 1;
                NodeHandle focus = NodeHandle.Null;
                if (!_control.IsNull) focus = hooks.FirstFocusableIn?.Invoke(_control) ?? NodeHandle.Null;
                if (focus.IsNull && !_node.IsNull) focus = hooks.FirstFocusableIn?.Invoke(_node) ?? NodeHandle.Null;
                if (!focus.IsNull) hooks.FocusNode?.Invoke(focus, true);
            }

            var arrival = p.Arrival;
            UseLayoutEffect(() =>
            {
                var a = latest.Value.Arrival;
                string id = latest.Value.Row.Id;
                if (a.IsNone || a.Seq <= s_arrivalPlayed.Value) return;
                if (a.Expand.Contains(id) && !expanded.Peek()) expanded.Value = true;
                if (string.Equals(a.DialId, id, StringComparison.Ordinal)) Land();
            }, DepKey.From(arrival.Seq));

            // The fallback: the arrival's target never drew under this row's expander, so this row takes it.
            UseTimeout(() =>
            {
                var a = latest.Value.Arrival;
                if (!a.IsNone && a.Seq > s_arrivalPlayed.Value && string.Equals(a.Parent, latest.Value.Row.Id, StringComparison.Ordinal)) Land();
            }, WinUiSettingsRules.ArrivalFallbackMs, DepKey.From(arrival.Seq));

            // The highlight: it holds, then fades back (reduced motion: holds for the whole duration, then cuts).
            float hold = Design.Reduced ? arrival.HighlightMs : WinUiSettingsRules.HighlightHoldMs;
            float fade = Design.Reduced ? 0f : arrival.HighlightMs;
            int stamp = lit.Value;
            UseTimeout(() => { if (phase.Peek() == 1) phase.Value = 2; }, hold, DepKey.From(stamp));
            UseTimeout(() => { if (phase.Peek() == 2) phase.Value = 0; }, hold + fade + WinUiSettingsRules.HighlightSettleMs, DepKey.From(stamp));

            Element body = p.Prebuilt ?? Present(new RowCtx(row, p.Body, p.Item, expanded, _onControl, p.Page, Tier));
            int ph = phase.Value;
            var accent = Tok.AccentDefault;
            return new BoxEl
            {
                ZStack = true, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = _onNode,
                Children =
                [
                    body,
                    new BoxEl
                    {
                        HitTestVisible = false, AlignSelf = FlexAlign.Stretch,
                        Corners = CornerRadius4.All(p.Item ? 0f : SettingsCard.DefaultStyle.CornerRadius),
                        BorderWidth = WinUiSettingsRules.HighlightBorder,
                        BorderColor = ph == 1 ? accent : accent with { A = 0f },
                        Fill = ph == 1 ? Tok.AccentSubtle : Tok.AccentSubtle with { A = 0f },
                        BrushTransitionMs = ph == 2 ? fade : 0f,
                    },
                ],
            };
        }
    }

    /// <summary>An expander row opens collapsed, except where its switch is on and its items are its settings (crossfade, the equalizer).</summary>
    static bool InitiallyOpen(SettingsBodyRow row) => row.Id switch
    {
        "playback.crossfade" or "sound.equalizer" => row.Value is true,
        "appearance.theme" => true,
        _ => false,
    };

    // ══ 3. THE PRESENTERS ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The row's picture: WinUI's own card for the dials it draws as composites (or with a control the registry cannot describe: a range, a
    /// device list), the generic card of its kind for every other.</summary>
    static Element Present(RowCtx x)
    {
        string id = x.Row.Id;
        switch (id)
        {
            // Appearance
            case "appearance.theme": return ThemeCard(x);
            case "appearance.appMode": return AppModeCard(x);
            case "appearance.zoom": return ZoomCard(x);
            case "appearance.option.reset": return OptionsResetCard(x);
            // Sound
            case "sound.quality": return QualityCard(x, metered: false);
            case "sound.meteredQuality": return QualityCard(x, metered: true);
            case "sound.normalization.mode": return NormalizationModeCard(x);
            case "sound.equalizer": return EqualizerCard(x);
            case "sound.outputDevice": return OutputDeviceCard(x);
            // Playback
            case "playback.crossfade": return CrossfadeCard(x);
            case "playback.episodeSpeed": return EpisodeSpeedCard(x);
            case "playback.video.quality": return VideoQualityCard(x);
            case "playback.video.meteredQuality": return MeteredVideoCard(x);
            case "playback.videoOverrides": return VideoOverridesCard(x);
            case "playback.runtime": return RuntimeCard(x);
            // Lyrics & Now playing
            case "lyrics.blur": return LyricsBlurCard(x);
            case "lyrics.ai": return AiLyricsCard(x);
            case "lyrics.playerStyle.finish": return FinishCard(x);
            case "lyrics.stage.spectrum": return SpectrumCard(x);
            case "lyrics.stage.heroDim": return HeroDimCard(x);
            case "lyrics.stage.sensitivity": return StageSensitivityCard(x);
            case "lyrics.stage.syncOffset": return StageSyncCard(x);
            case "lyrics.mini.face": return MiniFaceCard(x);
            // Notifications
            case "notifications.quiet.from" or "notifications.quiet.to": return QuietHourCard(x);
            // Startup & system
            case "system.language": return LanguageCard(x);
            case "system.gpu": return GpuCard(x);
            // Storage
            case "storage.library" or "storage.runtime" or "storage.logs" or "storage.localStore" or "storage.imageCache" or "storage.aiModels"
                or "storage.otherData" or "storage.cache.audioBodies" or "storage.cache.licenseKeys": return StorageRowCard(x);
            case "storage.cache.budget": return BudgetCard(x);
            case "storage.cache.location": return CacheLocationCard(x);
            case "storage.metadata.budget": return MetadataBudgetCard(x);
            case "storage.metadata.clear": return MetadataClearCard(x);
            // Account & privacy
            case "account.identity": return IdentityCard(x);
            case "account.plan": return PlanCard(x);
            case "account.privacy.whatLeaves": return WhatLeavesCard(x);
            case "account.privacy.crashReports": return CrashReportsCard(x);
            case "account.privacy.crashService": return CrashServiceCardOf(x);
            case "account.reports.saved": return SavedReportsCard(x);
            // Help & about
            case "help.update.hero": return UpdatePanelCard(x);
            case "help.update.channel": return ChannelCard(x);
            case "help.receipts": return ReceiptsCardOf(x);
            case "help.whatsNew": return WhatsNewCard(x);
            case "help.feedback.reportHolds": return ReportHoldsCard(x);
            case "help.logs.viewer": return LogViewerCardOf(x);
            case "help.logs.detailLevel": return DetailLevelCard(x);
            case "help.logs.files": return LogFilesCard(x);
            case "help.tools.capture": return CaptureCard(x);
            case "help.developer.mode": return DeveloperCard(x);
            case "help.links.licenses": return LicensesCard(x);
        }
        if (id.StartsWith(SettingsSpec.ThemeOptionPrefix, StringComparison.Ordinal) && x.Row.Option is not null) return OptionRowCard(x);
        if (id.StartsWith(TopicPrefix, StringComparison.Ordinal) && !id.EndsWith(".test", StringComparison.Ordinal)) return TopicCard(x);
        if (id.StartsWith("help.shortcuts.", StringComparison.Ordinal)) return ShortcutItem(x);
        if (id.StartsWith("help.links.", StringComparison.Ordinal) || id == "account.privacy.policy") return LinkCard(x);
        return GenericCard(x);
    }

    /// <summary>The card every presenter starts from: the row's label, description, enablement, badge and "Also in" footer, in the item or card style.</summary>
    static Card BaseCard(RowCtx x, Element? control)
    {
        var row = x.Row;
        var style = x.Item ? SettingsExpander.DefaultStyle.ItemCardStyle : SettingsCard.DefaultStyle;
        return new Card(row.Label, row.Description, control, style)
        {
            Enabled = row.Enabled, Explain = ExplainOf(row), Footer = AlsoInFooter(row), OnControl = x.OnControl, WrapLabel = true,
        };
    }

    // ── a row outside the page ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The app mode row (System · Light · Dark), as Settings draws it and as the setup wizard's Theme step shows it: one row, one writer.</summary>
    internal static Element AppModeRow() => OutsideRow("appearance.appMode", static (row, card) => AppModeCard(row, card));

    /// <summary>The language row, as Settings draws it and as the setup wizard's Theme step shows it: one row, one writer.</summary>
    internal static Element LanguageRow() => OutsideRow("system.language", static (row, card) => LanguageCard(row, card));

    /// <summary>The audio quality row, as Settings draws it and as the setup wizard's playback step shows it: one row, one writer.</summary>
    internal static Element QualityRow() => OutsideRow("sound.quality", static (row, card) => QualityCard(row, card));

    /// <summary>A dial's card for a surface outside the page: the registry's row (<see cref="SettingsPageModel.RowOf"/>) in the card the page draws, with no
    /// host (no arrival ring) and no "Also in" footer (its doors lead away from the surface). Nothing shows while the facts compose the dial away.</summary>
    static Element OutsideRow(string dialId, Func<SettingsBodyRow, Card, Element> present)
    {
        if (SettingsPageModel.RowOf(Dial(dialId)) is not { } row) return new BoxEl { Visible = false };
        return present(row, new Card(row.Label, row.Description, null, SettingsCard.DefaultStyle) { Enabled = row.Enabled, Explain = ExplainOf(row), WrapLabel = true });
    }

    /// <summary>A clickable card (an action without a control): the clickable style where it is an expander item.</summary>
    static Card DoorCard(RowCtx x, Action onClick, string? actionIcon = null)
    {
        var card = BaseCard(x, null) with { OnClick = onClick, ActionIcon = actionIcon };
        return x.Item ? card with { Style = SettingsExpander.DefaultStyle.ClickableItemCardStyle } : card;
    }

    /// <summary>The badge's props (design 6.5): the core carries them only where the dial's clip exists for the theme and app mode.</summary>
    static ExplainerProps? ExplainOf(SettingsBodyRow row) => row.Explain?.Props;

    /// <summary>The generic card of a row's kind; a row with children is an expander whose items are the children.</summary>
    static Element GenericCard(RowCtx x)
    {
        var row = x.Row;
        var dial = row.Dial;
        Element? control = row.Kind switch
        {
            SettingsRowKind.Toggle => DialToggle(dial, row.Enabled),
            SettingsRowKind.Choice or SettingsRowKind.Picker when row.Choices.Length > 0 => DialCombo(row, row.Choices, WinUiSettingsRules.StageComboWidth),
            SettingsRowKind.Segmented when row.Choices.Length > 0 => Selector(row.Choices, new Signal<int>(row.Value is int at ? at : -1), i =>
            {
                if (dial.Writer.Choose is { } choose && choose(i)) Settings.Bump();
            }),
            SettingsRowKind.Action when row.Confirm is { } confirm => StdButton(Loc.Get(confirm.PrimaryKey), () =>
                ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey), () =>
                {
                    if (dial.Writer.Run is { } run && run()) Settings.Bump();
                }), row.Enabled),
            SettingsRowKind.Fact when row.Value is string s && s.Length > 0 => ValueTag(s),
            _ => null,
        };
        bool door = row.Kind == SettingsRowKind.Action && row.Confirm is null && dial.Writer.Run is not null;
        Card card = door ? DoorCard(x, () => Press(dial)) : BaseCard(x, control);
        if (row.Children.IsEmpty) return DialCard(card);
        var items = new Element[row.Children.Length];
        for (int i = 0; i < items.Length; i++) items[i] = ChildHost(x, row.Children[i]);
        return Expander(card with { OnClick = null }, items, x.Expanded);
    }

    /// <summary>Run an action dial, then the page bump when it asks.</summary>
    static void Press(SettingsDial dial)
    {
        if (dial.Writer.Run is { } run && run()) Settings.Bump();
    }

    /// <summary>A door that leaves the app (a web page): the open-in-new glyph instead of the chevron.</summary>
    static Element LinkCard(RowCtx x) => DialCard(DoorCard(x, () => Press(x.Row.Dial), Icons.OpenInNewWindow));

    // ── a choice over its words ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A combo over a dial's choices: its own component (the ComboBox freezes its items, signal and handler at mount), keyed by the dial and its
    /// words so a changed list remounts it; the selection follows the dial's value on every render.</summary>
    static Element DialCombo(SettingsBodyRow row, IReadOnlyList<string> labels, float width, IReadOnlyList<string>? descriptions = null,
        IReadOnlyList<bool>? itemEnabled = null, int? value = null)
        => Embed.Comp(new DialComboProps(row.Dial, labels, width, descriptions, itemEnabled, value ?? (row.Value is int at ? at : -1), row.Enabled),
            static () => new DialComboView()) with { Key = "combo:" + row.Id + ":" + labels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Signature(labels) };

    static string Signature(IReadOnlyList<string> labels)
    {
        var hash = new HashCode();
        for (int i = 0; i < labels.Count; i++) hash.Add(labels[i]);
        return hash.ToHashCode().ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    sealed record DialComboProps(SettingsDial Dial, IReadOnlyList<string> Labels, float Width, IReadOnlyList<string>? Descriptions,
        IReadOnlyList<bool>? ItemEnabled, int Value, bool Enabled);

    sealed class DialComboView : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var p = UseProps<DialComboProps>();
            var selected = UseSignal(p.Value);
            int at = p.Value;
            UseEffect(() => selected.SetIfChanged(at), DepKey.From(at));
            var dial = p.Dial;
            return Combo(p.Labels, selected, width: p.Width, isEnabled: p.Enabled, itemDescriptions: p.Descriptions, itemEnabled: p.ItemEnabled, onChange: i =>
            {
                if (dial.Writer.Choose is { } choose && choose(i)) Settings.Bump();
            });
        }
    }

    // ── "Also in" ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The places outside Settings that draw the same dial (design 5.1, W-Arrival): "Also in" and each place, a link where the core can open it.</summary>
    static Element? AlsoInFooter(SettingsBodyRow row)
    {
        if (row.AlsoIn.IsEmpty) return null;
        var kids = new List<Element>(row.AlsoIn.Length * 2 + 2)
        {
            Icon(Icons.Link, WinUiSettingsRules.CaptionSize, Tok.TextSecondary) with { Shrink = 0f },
            Caption(Loc.Get(Strings.Theme.Winui.Screens.Settings.AlsoIn)) with { Color = Tok.TextSecondary, Shrink = 0f },
        };
        for (int i = 0; i < row.AlsoIn.Length; i++)
        {
            var place = row.AlsoIn[i];
            if (i > 0) kids.Add(Caption(Loc.Get(Strings.Settings.AlsoIn.Separator).Trim()) with { Color = Tok.TextTertiary, Shrink = 0f });
            kids.Add(place.Open is { } open
                ? LinkButton(place.Place, open) with { Key = "settings:alsoin:" + row.Id + ":" + place.Surface }
                : Caption(place.Place) with { Color = Tok.TextSecondary, Shrink = 0f });
        }
        return new BoxEl
        {
            Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = WinUiSettingsRules.AlsoInGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }
}
