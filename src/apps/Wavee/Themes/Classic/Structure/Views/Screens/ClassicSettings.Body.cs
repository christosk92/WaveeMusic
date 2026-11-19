// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Body.cs — a category's body over the registry, a dial's row, the arrival (settings rework 7.1) ──
//
// A category is what `SettingsPageProps.Body.Of(category)` composes right now: its groups in order, each a section header (the group's 16 glyph, its BodyStrong
// title, its caption) over its rows, the rows 4 apart, the groups balanced over two reading columns at Huge (`TabStack`). A row is one dial:
//   · a dial Classic draws as its own composite card (the theme picker, the AI lyrics card, the equalizer, crossfade, the storage census rows, the update hero, the
//     attached videos, the runtime card, the saved reports, the privacy expanders, the log cards, ...) is drawn by that card, its children inside it (`TryCustom`);
//   · every other dial is the generic grammar by its kind (`GenericCard`): a SettingsCard with the dial's glyph, label and description (its reason while greyed),
//     the control its kind asks for (a switch, a combo over its words, segments, a button that confirms, a clickable card, a value), and a SettingsExpander when
//     the dial has children, each child an expander item drawn the same way.
// Every row is keyed `settings:dial:<id>` (`DialFrame`): the inspector, `lint` and a link's arrival find it there. A dial on the badge list carries Classic's
// explanation badge beside its control (`ClassicSettings.Explain.cs`); a dial a panel draws too carries its "Also in" line under its card, each place a link where
// the core can open it.
//
// THE ARRIVAL (design 4.9). The page host has opened the link's category; the template opens the expanders on the dial's way (its parents, the theme
// options expander, the shortcuts expander): on a lane already on screen each on the expander's own FlowReveal motion (`OpenArrival`), on a lane the link
// brings in already open (`OpenArrivingLane`). The row's frame, when it mounts or its link repeats, brings itself into view at 0.3 of the scroller
// (`SceneStore.BringIntoView`: a glide on a lane on screen; a cut on a lane that arrives under its cross-fade, and with reduced motion), lights its highlight
// (the accent border and the accent at the subtle-fill alpha, `Glow`) and moves the keyboard to its control. The highlight holds, then fades back over the policy's HighlightMs through the
// colours' brush transition (reduced motion: holds, then cuts). An arrival plays once per link (`s_arrivalDone`): a frame that remounts later does not replay it.

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

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. THE PAGE'S PER-RENDER CONTEXT (set by `Template` before any body builds) ════════════════════════════════

    /// <summary>The link's arrival the bodies hand their frames (see <see cref="Template"/>).</summary>
    static SettingsArrival s_arrival = SettingsArrival.None;

    /// <summary>This render brings a new lane in (a category the link or a door opened): its rows arrive already in place, so an arrival cuts to its row
    /// under the lane's cross-fade instead of gliding through a page that is still fading in (see <see cref="Template"/>).</summary>
    static bool s_laneArriving;

    /// <summary>The page's expanders' open cells by dial (page lifetime; see <see cref="Template"/>).</summary>
    static Dictionary<string, Signal<bool>> s_expanders = new(StringComparer.Ordinal);

    /// <summary>The last link whose arrival played (root lifetime): a frame that mounts again later does not replay it.</summary>
    static readonly RootState<int[]> s_arrivalDone = new("Settings.Classic.ArrivalDone", static () => new int[1]);

    /// <summary>The theme options expander's and the shortcuts expander's cells (they hold dials that have no parent of their own).</summary>
    const string OptionsCell = "settings:options", ShortcutsCell = "settings:shortcuts";

    /// <summary>The appearance category's theme options group, and the help category's shortcuts group.</summary>
    const string OptionsGroup = "options", ShortcutsGroup = "shortcuts";

    /// <summary>The open cell of the expander that holds <paramref name="id"/>'s children (made at <paramref name="initial"/> the first time it is asked for).</summary>
    static Signal<bool> ExpandCell(string id, bool initial)
    {
        if (!s_expanders.TryGetValue(id, out var cell)) s_expanders[id] = cell = new Signal<bool>(initial);
        return cell;
    }

    /// <summary>A link's arrival opens the expanders on its way, outermost first, on a mounted lane: each opens on its own motion (an effect: never in a render).</summary>
    static void OpenArrival(SettingsArrival focus)
    {
        if (focus.IsNone) return;
        foreach (string id in focus.Expand) ExpandCell(id, false).Value = true;
        if (string.Equals(focus.Group, OptionsGroup, StringComparison.Ordinal)) ExpandCell(OptionsCell, true).Value = true;
        if (string.Equals(focus.Group, ShortcutsGroup, StringComparison.Ordinal)) ExpandCell(ShortcutsCell, false).Value = true;
    }

    /// <summary>A link that brings its category's lane in: the expanders on its way get FRESH open cells, which the lane's expanders read as they mount
    /// (no signal is written: nothing mounted holds these cells yet), so they arrive open, with no reveal under the cross-fade.</summary>
    static void OpenArrivingLane(SettingsArrival focus)
    {
        if (focus.IsNone) return;
        foreach (string id in focus.Expand) s_expanders[id] = new Signal<bool>(true);
        if (string.Equals(focus.Group, OptionsGroup, StringComparison.Ordinal)) s_expanders[OptionsCell] = new Signal<bool>(true);
        if (string.Equals(focus.Group, ShortcutsGroup, StringComparison.Ordinal)) s_expanders[ShortcutsCell] = new Signal<bool>(true);
    }

    // ══ 2. A CATEGORY ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Category <paramref name="index"/>'s body: a section header per group over its rows (the theme options and the shortcuts are one expander each,
    /// the expander their heading), split over two columns at Huge.</summary>
    static Element CategoryBody(SettingsPageProps props, int index)
    {
        SettingsBody body = props.Body.Of(index);
        string slug = body.Category.Slug;
        var kids = new List<Element>(48);
        foreach (SettingsBodyGroup group in body.Groups)
        {
            if (string.Equals(group.Id, OptionsGroup, StringComparison.Ordinal)) { kids.Add(ThemeOptionsBlock(group)); continue; }
            if (string.Equals(group.Id, ShortcutsGroup, StringComparison.Ordinal)) { kids.Add(ShortcutsBlock(group, slug)); continue; }
            if (LeadBefore(slug, group.Id) is { } before) kids.Add(before);
            kids.Add(SectionHeader(group.Title, GroupGlyph(slug, group.Id, body.Category.Glyph), group.Subtitle));
            LeadAfter(slug, group.Id, props, kids);
            foreach (SettingsBodyRow row in group.Rows)
                if (DialElement(row, group, props) is { } element) kids.Add(element);
        }
        return TabStack(kids.ToArray());
    }

    /// <summary>What a group shows above its header: the notification banner Windows' own setting forces (the honesty layer FIRST: while Windows suppresses
    /// Wavee every switch below is theatre until that is fixed).</summary>
    static Element? LeadBefore(string slug, string group)
        => slug == "notifications" && group == "delivery" ? BlockedBar(Settings.NotifyRules.Blocked(SettingsFacts.ToastDelivery())) : null;

    /// <summary>What a group shows between its header and its rows: the storage census (spinner, usage card or failed bar), the topics' sentence and the
    /// Windows-off note.</summary>
    static void LeadAfter(string slug, string group, SettingsPageProps props, List<Element> kids)
    {
        if (slug == "storage" && group == "onThisPc") kids.Add(StorageCensus(props.Storage));
        else if (slug == "notifications" && group == "topics") TopicsLead(kids);
    }

    /// <summary>A dial's row, framed (its key, its "Also in" line, the arrival); null for a dial another card draws or that has no row here.</summary>
    static Element? DialElement(SettingsBodyRow row, SettingsBodyGroup? group, SettingsPageProps props)
    {
        if (!TryCustom(row, group, props, out Element? card)) card = GenericCard(row);
        return card is { } c ? Framed(row, c) : null;
    }

    // ── a row outside the page ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The app mode row (System · Light · Dark), as Settings draws it and as the setup wizard's Theme step shows it: one row, one writer.</summary>
    internal static Element AppModeRow() => OutsideRow("appearance.appMode", static row => GenericCard(row));

    /// <summary>The language row, as Settings draws it and as the setup wizard's Theme step shows it (one row, one writer), its combo on
    /// <paramref name="selected"/> (the wizard's own signal, seeded from the dial: <see cref="LanguageIndex"/>).</summary>
    internal static Element LanguageRow(Signal<int> selected) => OutsideRow("system.language", row => LanguageRow(row, selected));

    /// <summary>The audio quality row, as Settings draws it and as the setup wizard's playback step shows it (one row, one writer), its combo on
    /// <paramref name="selected"/> (the wizard's own signal, seeded from the dial: <see cref="QualityIndex"/>).</summary>
    internal static Element QualityRow(Signal<int> selected) => OutsideRow("sound.quality", row => CardRow(row, QualityCombo(selected)));

    /// <summary>The language the dial holds, for a surface that seeds its own signal (the setup wizard).</summary>
    internal static int LanguageIndex() => Dial("system.language").Current is int language ? language : 0;

    /// <summary>The audio quality the dial holds, for a surface that seeds its own signal (the setup wizard).</summary>
    internal static int QualityIndex() => Dial("sound.quality").Current is int quality ? quality : Platform.Keys.PlaybackQuality.Default;

    /// <summary>A dial's card for a surface outside the page: the registry's row (<see cref="SettingsPageModel.RowOf"/>) in the card the page draws, unframed
    /// (no "Also in" line: its doors lead away from the surface; no arrival). Nothing shows while the facts compose the dial away.</summary>
    static Element OutsideRow(string dialId, Func<SettingsBodyRow, Element> card)
        => SettingsPageModel.RowOf(Dial(dialId)) is { } row ? card(row) : new BoxEl { Visible = false };

    /// <summary>The 16-DIP glyph of a group's header: the group's subject (the old sections' glyphs where a group took one over), else the category's.</summary>
    static string GroupGlyph(string slug, string group, string categoryGlyph) => (slug, group) switch
    {
        ("appearance", "theme") => Icons.Brush,
        ("appearance", "mode") => Icons.Sun,
        ("appearance", "sizeAndText") => Icons.Font,
        ("appearance", "motion") => Icons.Forward,
        ("appearance", "homeAndLibrary") => Icons.Home,
        ("sound", "quality") => Icons.Headphones,
        ("sound", "loudness") => Icons.Volume,
        ("sound", "equalizer") => Icons.Equalizer,
        ("sound", "output") => Icons.Speakers,
        ("playback", "betweenSongs") => Icons.Play,
        ("playback", "playerBar") => Icons.Pin,
        ("playback", "podcasts") => Icons.Microphone,
        ("playback", "videos") => Icons.Movie,
        ("playback", "runtime") => Icons.Code,
        ("lyrics", "lyrics") => Icons.Microphone,
        ("lyrics", "nowPlaying") => Icons.Album,
        ("lyrics", "fullScreen") => Icons.TvMonitor,
        ("lyrics", "miniPlayer") => Icons.Picture,
        ("notifications", "delivery") => Icons.Bell,
        ("notifications", "topics") => Icons.Settings,
        ("notifications", "tips") => Icons.RefineSparkle,
        ("system", "startup") => Icons.Contact,
        ("system", "window") => Icons.ThisPc,
        ("system", "language") => Icons.Globe,
        ("system", "links") => Icons.Link,
        ("system", "graphics") => Icons.Devices,
        ("storage", "onThisPc") => Icons.ThisPc,
        ("storage", "playbackCache") => Icons.Download,
        ("storage", "metadataCache") => Icons.Document,
        ("storage", "reset") => Icons.Delete,
        ("account", "account") => Icons.Contact,
        ("account", "privacy") => Icons.Shield,
        ("account", "crashReports") => Icons.Warning,
        ("help", "about") => Icons.Info,
        ("help", "feedback") => Icons.Attention,
        ("help", "diagnostics") => Icons.Document,
        ("help", "developer") => Icons.Code,
        ("help", "links") => Icons.Link,
        _ => Glyph(categoryGlyph),
    };

    /// <summary>The glyph Classic always drew beside a dial the registry gives none (the old rows' explicit icons).</summary>
    static string? ClassicGlyphOf(string id) => id switch
    {
        "account.identity" or "account.profile" => Icons.Contact,
        "account.manage" => Icons.Globe,
        "account.signOut" => Icons.SignOut,
        "help.feedback.report" => Icons.Attention,
        "help.feedback.suggest" => Icons.Add,
        "help.feedback.ask" => Icons.Info,
        "help.feedback.copyDiagnostics" => Icons.Copy,
        "help.update.store" => Icons.Refresh,
        "help.update.channel" => Icons.Devices,
        "help.update.installOnQuit" => Icons.Download,
        "help.update.onMetered" => Icons.RadioTower,
        "help.update.autoShow" or "help.whatsNew" => Icons.RefineSparkle,
        "help.showSummaryAgain" => Icons.Document,
        "help.connectDiagnostics" => Icons.Devices,
        "notifications.windows" or "notifications.sound" => Icons.Bell,
        "notifications.quiet" => Icons.Moon,
        "notifications.quiet.from" or "notifications.quiet.to" => Icons.Clock,
        "notifications.tips" => Icons.RefineSparkle,
        "notifications.resetTips" => Icons.Refresh,
        "sound.connectName" => Icons.Device,
        "playback.runtime" => Icons.Code,
        "playback.runtime.signature" => Icons.Shield,
        _ => null,
    };

    /// <summary>The width of a dial's combo (the widths the old rows held).</summary>
    static float ComboWidthOf(string id) => id switch
    {
        "system.tray.icon" or "system.language" => ClassicSettingsRules.GeneralComboWidth,
        "sound.outputDevice" or "sound.quality" or "sound.meteredQuality" => ClassicSettingsRules.WideComboWidth,
        "sound.normalization.mode" or "sound.equalizer.preset" => ClassicSettingsRules.NarrowComboWidth,
        "lyrics.playerStyle" or "lyrics.playerStyle.finish" => ClassicSettingsRules.NpvComboWidth,
        "help.logs.viewerLevel" or "help.logs.fileLevel" => ClassicSettingsRules.LevelComboWidth,
        _ => ClassicSettingsRules.StageComboWidth,
    };

    // ══ 3. THE GENERIC ROW (a dial by its kind) ═══════════════════════════════════════════════════════════════════════

    /// <summary>A dial's card by its kind; a SettingsExpander (its control in the header, its children as items) when it has children.</summary>
    static Element GenericCard(SettingsBodyRow row)
    {
        var (control, clickable, onClick) = GenericControl(row);
        if (row.Children.IsDefaultOrEmpty) return CardRow(row, control, isClickEnabled: clickable, onClick: onClick);
        var items = new List<Element>(row.Children.Length);
        foreach (SettingsBodyRow child in row.Children)
            if (ChildElement(child) is { } item) items.Add(item);
        // A switch's expander opens with the switch on (the old crossfade and equalizer rows), every other one closed.
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = row.Label,
            Description = row.Description,
            HeaderIcon = GlyphOf(row.Dial),
            Content = Badged(row, control),
            Style = ToggleHeaderStyle(control),
            Items = items,
            IsExpanded = ExpandCell(row.Id, row.Kind == SettingsRowKind.Toggle && row.Value is true),
        }) with { Key = "settings:expander:" + row.Id };
    }

    /// <summary>A child dial as an expander item: Classic's own item where it draws one, else the generic item by its kind; framed like a row.</summary>
    static Element? ChildElement(SettingsBodyRow child)
    {
        if (!TryCustomItem(child, out Element? item)) item = GenericItem(child);
        return item is { } i ? Framed(child, i, item: true) : null;
    }

    /// <summary>A child dial's generic item.</summary>
    static Element GenericItem(SettingsBodyRow row)
    {
        var (control, clickable, onClick) = GenericControl(row);
        return Item(row.Label, row.Description, Badged(row, control), isEnabled: row.Enabled, isClickEnabled: clickable, onClick: onClick, icon: GlyphOf(row.Dial));
    }

    /// <summary>The control a dial's kind asks for: a switch, a combo over its words, segments, a button that confirms first, a value; an action with nothing to
    /// confirm is the clickable card itself.</summary>
    static (Element? Control, bool Clickable, Action? OnClick) GenericControl(SettingsBodyRow row)
    {
        SettingsDial dial = row.Dial;
        bool enabled = row.Enabled;
        switch (row.Kind)
        {
            case SettingsRowKind.Toggle:
                return (Toggle(new Signal<bool>(row.Value is true), on =>
                {
                    if (dial.Writer.Toggle!(on)) Settings.Bump();
                }, enabled), false, null);

            case SettingsRowKind.Choice or SettingsRowKind.Picker when !row.Choices.IsDefaultOrEmpty:
                return (DialComboControl(row), false, null);

            case SettingsRowKind.Segmented when !row.Choices.IsDefaultOrEmpty:
                return (Selector(row.Choices, new Signal<int>(row.Value is int at ? at : -1), onChange: i =>
                {
                    if (dial.Writer.Choose!(i)) Settings.Bump();
                }), false, null);

            case SettingsRowKind.Action when dial.Confirm is { } confirm:
                return (StdButton(Loc.Get(confirm.PrimaryKey), () =>
                    ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey), () =>
                    {
                        if (dial.Writer.Run!()) Settings.Bump();
                    }), isEnabled: enabled), false, null);

            case SettingsRowKind.Action when dial.Writer.Run is not null:
                return (null, enabled, () =>
                {
                    if (dial.Writer.Run!()) Settings.Bump();
                });

            case SettingsRowKind.Fact when row.Value is string { Length: > 0 } value:
                return (ValueTag(value), false, null);

            default:
                return (null, false, null);
        }
    }

    /// <summary>A card over a body row: its label, its description (<paramref name="sub"/> overrides it for a live sentence), its glyph, its badge, its control.</summary>
    static Element CardRow(SettingsBodyRow row, Element? control = null, string? sub = null,
                           SettingsCard.ContentAlignment align = SettingsCard.ContentAlignment.Right,
                           bool isClickEnabled = false, Action? onClick = null, string? actionIcon = null)
        => Row(row.Label, sub ?? row.Description, Badged(row, control), GlyphOf(row.Dial), align, isClickEnabled, onClick, row.Enabled, actionIcon);

    /// <summary>A dial's combo: the words its row carries, its value kept in step with every other writer of the dial.</summary>
    static Element DialComboControl(SettingsBodyRow row)
        => Embed.Comp(new ComboProps(row.Dial, row.Choices, row.Value is int at ? at : -1, ComboWidthOf(row.Id), row.Enabled), static () => new DialCombo());

    sealed record ComboProps(SettingsDial Dial, ImmutableArray<string> Labels, int Value, float Width, bool Enabled);

    /// <summary>A combo over a dial. A ComboBox freezes its items, its signal and its onChange at mount: the component keeps ONE signal in step with the dial (the
    /// page's value on a re-render, and the dial's own epoch where its value reads one, so a pick made in a panel shows here live), and re-keys the combo when the
    /// words change.</summary>
    sealed class DialCombo : SettingsComponent
    {
        readonly Signal<int> _selected = new(-1);
        SettingsDial? _dial;

        public override Element Render()
        {
            UseLayout();
            var p = UseProps<ComboProps>();
            _dial = p.Dial;
            int value = p.Value;
            UseEffect(() => _selected.SetIfChanged(value), DepKey.From(value));
            UseSignalEffect(() => { if (_dial?.Current is int live) _selected.SetIfChanged(live); });
            SettingsDial dial = p.Dial;
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Grow = s_layout.Touch ? 1f : 0f, AlignSelf = s_layout.Touch ? FlexAlign.Stretch : FlexAlign.Auto,
                Children =
                [
                    Combo(p.Labels, _selected, width: p.Width, isEnabled: p.Enabled, onChange: i =>
                    {
                        if (dial.Writer.Choose!(i)) Settings.Bump();
                    }) with { Key = "combo:" + string.Join('|', p.Labels) },
                ],
            };
        }
    }

    // ══ 4. THE BADGE AND "ALSO IN" ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The control with the dial's explanation badge in front of it (a dial on the badge list for this theme); the control alone otherwise. A toggle
    /// keeps its label | switch row at Compact (the pair is still the toggle's cell).</summary>
    static Element? Badged(SettingsBodyRow row, Element? control) => Badged(row.Id, row.Explain, control);

    /// <summary>The control with the explanation badge of <paramref name="dialId"/> in front of it (see <see cref="Badged(SettingsBodyRow, Element?)"/>).</summary>
    static Element? Badged(string dialId, SettingsExplainInfo? explain, Element? control)
    {
        if (explain is not { } info) return control;
        Element badge = ExplainBadge(new ExplainerProps
        {
            DialId = dialId, Caption = info.Caption, ClipPath = info.ClipPath, ReducedMotion = Design.Reduced, Surface = SettingsSurfaces.Settings,
        });
        if (control is null) return badge;
        bool touch = s_layout.Touch;
        // The control's own box is remembered by dial, so an arrival gives the keyboard to the control, not to the badge in front of it.
        return new BoxEl
        {
            Key = IsToggle(control) ? ToggleKey : null, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f, MinWidth = 0f,
            Grow = touch ? 1f : 0f,
            Children =
            [
                badge,
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = touch ? 1f : 0f, OnRealized = h => s_controlNodes[dialId] = h,
                    Children = [control.Value],
                },
            ],
        };
    }

    /// <summary>The box of a badged row's control by dial (an arrival focuses inside it; see <see cref="Badged(string, SettingsExplainInfo?, Element?)"/>).</summary>
    static readonly Dictionary<string, NodeHandle> s_controlNodes = new(StringComparer.Ordinal);

    /// <summary>"Also in: Queue · Full screen › Options" under a row whose dial a panel draws too: each place a link where the core can open it, a word where it
    /// has no door of its own (a menu, the bar that is always on screen).</summary>
    static Element AlsoInLine(SettingsBodyRow row, bool item)
    {
        bool anyDoor = false;
        foreach (SettingsAlsoIn place in row.AlsoIn) anyDoor |= place.Open is not null;
        var kids = new List<Element>(row.AlsoIn.Length * 2 + 1);
        if (!anyDoor)
            kids.Add(Caption(row.AlsoInLine ?? "") with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, Shrink = 1f });
        else
        {
            kids.Add(Caption(Strings.Settings.AlsoIn.Line("").TrimEnd()) with { Color = Tok.TextSecondary, Shrink = 0f });
            for (int i = 0; i < row.AlsoIn.Length; i++)
            {
                SettingsAlsoIn place = row.AlsoIn[i];
                if (i > 0) kids.Add(Caption(Loc.Get(Strings.Settings.AlsoIn.Separator)) with { Color = Tok.TextTertiary, Shrink = 0f });
                kids.Add(place.Open is { } open
                    ? LinkButton(place.Place, open) with { Key = "settings:also-in:" + row.Id + ":" + place.Surface }
                    : Caption(place.Place) with { Color = Tok.TextSecondary, Shrink = 0f });
            }
        }
        return new BoxEl
        {
            // A place's link keeps its own side padding as the space after the words before it (no gap: the boxes never overlap).
            Key = "settings:also-in:" + row.Id, Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = anyDoor ? 0f : ClassicSettingsRules.AlsoInGap,
            Padding = item ? ClassicSettingsRules.AlsoInItemPadding : ClassicSettingsRules.AlsoInPadding, MinHeight = MathF.Max(ClassicSettingsRules.AlsoInMinHeight, s_layout.Target),
            MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = kids.ToArray(),
        };
    }

    // ══ 5. THE FRAME: THE ROW'S KEY, ITS "ALSO IN" AND THE ARRIVAL ═════════════════════════════════════════════════════

    /// <summary>A row in its frame: keyed `settings:dial:&lt;id&gt;`, its "Also in" line under it (on an expander item's text column for an
    /// <paramref name="item"/>), and the arrival when a link names this dial.</summary>
    static Element Framed(SettingsBodyRow row, Element card, bool item = false)
    {
        Element content = row.AlsoIn.IsDefaultOrEmpty
            ? card
            : new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = [card, AlsoInLine(row, item)] };
        return FrameOf(row.Id, content);
    }

    /// <summary>The child row <paramref name="id"/> of <paramref name="row"/>, or null when the facts compose it away right now.</summary>
    static SettingsBodyRow? ChildRow(SettingsBodyRow row, string id)
    {
        foreach (SettingsBodyRow child in row.Children)
            if (string.Equals(child.Id, id, StringComparison.Ordinal)) return child;
        return null;
    }

    /// <summary>A composite card's part keyed as the child dial it is (its "Also in" line under it), or keyed by the id alone.</summary>
    static Element ChildFrame(SettingsBodyRow parent, string id, Element part)
        => ChildRow(parent, id) is { } child ? Framed(child, part) : FrameOf(id, part);

    /// <summary>A dial's frame around any element (a composite card's part: a button, a link, an item).</summary>
    static Element FrameOf(string dialId, Element content)
    {
        var arrival = s_arrival;
        int seq = !arrival.IsNone && string.Equals(arrival.DialId, dialId, StringComparison.Ordinal) ? arrival.Seq : 0;
        return Embed.Comp(new FrameProps(dialId, content, seq, arrival.HighlightMs, seq > 0 && s_laneArriving), static () => new DialFrame());
    }

    sealed record FrameProps(string Id, Element Content, int Seq, int HighlightMs, bool Cut);

    /// <summary>A row's frame (see the file header): a stack whose first layer is the highlight while it plays and whose last is the row. The row is keyed inside
    /// the stack, so the highlight arriving and leaving never remounts it.</summary>
    sealed class DialFrame : Component
    {
        NodeHandle _node;
        readonly Action<NodeHandle> _realized;

        public DialFrame() => _realized = h => _node = h;

        public override Element Render()
        {
            var p = UseProps<FrameProps>();
            var hooks = UseContext(InputHooks.Current);
            var (glow, setGlow) = UseState(0);
            int seq = p.Seq;
            bool cut = p.Cut;
            UseLayoutEffect(() =>
            {
                int[] done = s_arrivalDone.Value;
                if (seq <= 0 || seq <= done[0] || _node.IsNull) return;
                done[0] = seq;
                // A lane the link brought in arrives scrolled to the row (a cut under its cross-fade); a lane on screen glides to it (a cut with reduced motion).
                Context.Scene?.BringIntoView(_node, ClassicSettingsRules.ArrivalAlign, cut || Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide);
                setGlow(seq);
                // The keyboard goes to the row's control (past its badge where it has one).
                NodeHandle within = s_controlNodes.TryGetValue(p.Id, out var control) && Context.Scene is { } scene && scene.IsLive(control) ? control : _node;
                NodeHandle target = hooks.FirstFocusableIn?.Invoke(within) ?? NodeHandle.Null;
                if (!target.IsNull) hooks.FocusNode?.Invoke(target, true);
            }, DepKey.From(seq));
            Element row = p.Content with { Key = "row" };
            return new BoxEl
            {
                Key = SettingsKeys.Dial(p.Id), ZStack = true, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = _realized,
                Children = glow > 0 ? [Embed.Comp(new GlowProps(glow, p.HighlightMs), static () => new Glow()) with { Key = "glow:" + glow }, row] : [row],
            };
        }
    }

    sealed record GlowProps(int Seq, int HighlightMs);

    /// <summary>The arrival's highlight under the row: the accent border (hanging 1 outside the card) and the accent at the subtle-fill alpha. It mounts lit,
    /// holds while the row glides in, then fades back over the policy's HighlightMs through the brush transition; with reduced motion it holds the whole time
    /// and then cuts (a value, not a branch). Faded, it stays in the stack, drawing nothing, until the next link to the row lights a new one.</summary>
    sealed class Glow : Component
    {
        public override Element Render()
        {
            var p = UseProps<GlowProps>();
            var (fading, setFading) = UseState(false);
            bool reduced = Design.Reduced;
            float hold = reduced ? p.HighlightMs : ClassicSettingsRules.HighlightHoldMs;
            float fade = reduced ? 0f : p.HighlightMs;
            UseTimeout(() => setFading(true), hold, DepKey.From(p.Seq));
            ColorF accent = Tok.AccentDefault;
            var card = SettingsCard.DefaultStyle;
            // The component's anchor fills the stack's layer; the plate fills the anchor (it has no content of its own to size it).
            return new BoxEl
            {
                Grow = 1f, AlignSelf = FlexAlign.Stretch, Margin = Edges4.All(ClassicSettingsRules.HighlightOutset), HitTestVisible = false,
                Corners = CornerRadius4.All(card.CornerRadius - ClassicSettingsRules.HighlightOutset),
                Fill = accent with { A = fading ? 0f : ClassicSettingsRules.HighlightFillAlpha },
                BorderWidth = ClassicSettingsRules.HighlightBorder, BorderColor = accent with { A = fading ? 0f : accent.A },
                BrushTransitionMs = fade,
            };
        }
    }
}
