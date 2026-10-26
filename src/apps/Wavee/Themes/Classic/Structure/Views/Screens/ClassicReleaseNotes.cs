// ── Themes/Classic/Structure/Views/Screens/ClassicReleaseNotes.cs — Classic's `Pages.WhatsNew` (themes plan 6.1, T11c; P3t TC8) ────────────────────────
//
// From `Screens\ReleaseNotes.UI.cs`: the What's-new page (the release head, the highlight cards, the changelog, the release rail, the since-banner, loading and
// empty). `ReleaseNotes.PageHost` (a `[CoreHost]`) owns the load and its cancellation, the last-seen read BEFORE the mark-seen write, the issue-state republish and
// the "only the latest" state, and hands this view the state, the view model and the verbs; the view draws them.
//
// WINUI PARTS (2026-10-26, the owner: "grounded in WinUI patterns, less eyebrows and dots"; no custom expander). The Fluent type ramp (Title, BodyLarge, Body,
// Caption, Subtitle), a plain release head (no plate, no pills: one caption line of facts), cards for the highlights with their whole text, the changelog as the
// stock SelectorBar over groups of stock Expanders (one per change: its headline as the header, the rest when opened), and a rail that is a list with WinUI's
// selection pill.
//
// EVERY TIER (TC8). The page reads its record, `ClassicReleaseNotesTiers.Of(ctx.Tier())` (Rules\Tiers.ReleaseNotes.cs): the release rail at Standard and Huge, a picker
// button at Compact (under the title, 40) and Small (in the title row, 32); the head's actions, the highlight cards' form and the body column's cap are the record's.
// The first frame is already in the final tier; a crossing swaps the rail and the picker, and the two card forms, with the shared fade, and the rest FLIPs.
//
//   Frame ── head grid (key `rn:head`): title · [Compact, Small: picker slot `rn:picker-slot`, beside the title at Small, under it at Compact]
//          └ row ── body column (<= 1200 at Huge) ── [stacked] since InfoBar
//                │               └ ScrollView "whatsnew:<ver>" → Head · Highlights (cards) · "All changes" · SelectorBar
//                │                  · per release [Divider] · InfoBar per notice · a group per section (heading · an Expander per change)
//                └ ReleaseRail (208 / 280, Flow.For keyed by version)          — absent while loading / empty / offline, and at Compact / Small
//
// The page is a DOCUMENT, not a virtualized list (ch 28 §9.2): ≤ 3 cards, a few dozen changes, ≤ ~20 rail rows; posters are resolved in the loader, and nothing here
// touches the disk or the network. The cards, the changelog and the viewer's parts are the sibling files of this class.

using FluentGpu.Hooks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.Classic.ClassicReleaseNotesRules;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's release-notes surfaces: the What's-new page, the highlight viewer and the after-update plate.</summary>
internal static partial class ClassicReleaseNotes
{
    /// <summary>Registers the three keys.</summary>
    public static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.WhatsNewPage, PageTemplate, "classic.release-notes.page");
        views.Add(ViewKeys.ReleaseNotesViewer, ViewerTemplate, "classic.release-notes.viewer");
        views.Add(ViewKeys.ReleaseNotesAfterUpdatePlate, PlateTemplate, "classic.release-notes.after-update-plate");
    }

    static string ReleaseDate(string? iso) => Links.Date(iso, Loc.Get(Strings.WhatsNew.DateFormat));

    static readonly Action<string> IgnoreLink = static _ => { };
    static readonly Action Inert = static () => { };

    /// <summary>A button at the tier's size: the stock size, or Large (40) where the record asks for a hit target.</summary>
    static BoxEl Btn(string label, Action click, ButtonAppearance appearance, ControlSize size, float target, bool enabled = true)
        => Button.Create(label, click, appearance, target > 0f ? ControlSize.Large : size, isEnabled: enabled);

    /// <summary>A block's heading on the page: WinUI's Subtitle (20/28 semibold).</summary>
    static Element SectionHeading(string text) => new TextEl(text)
    {
        Size = K.HeadingSize, LineHeight = K.HeadingLine, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
        Margin = new Edges4(0f, K.HeadingTop, 0f, 0f),
    };

    // ══ THE PAGE ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.WhatsNewPage"/>.</summary>
    public static Element PageTemplate(WhatsNewPageProps props, in ViewContext ctx)
    {
        var tiers = ctx.Tier();
        var layout = ClassicReleaseNotesTiers.Of(tiers);
        float gutter = ClassicTiers.PageOf(tiers).Gutter;   // the one page gutter (F3)
        var filter = ctx.Hooks.UseSignal(0);                // the SelectorBar's choice: 0 is every section, then one per kind
        if (props.State != WhatsNewState.Loaded || props.View is not { } view)
            return Frame(props.State == WhatsNewState.Empty ? EmptyState(props, layout) : LoadingState(), rail: null, picker: null, gutter, layout);

        var releases = view.Releases;
        bool stacked = releases.Length > 1 && !props.OnlyLatest;
        var shown = stacked ? releases : releases[..1];

        var main = new List<Element>(12)
        {
            Head(props, shown[0].Doc, layout),
            Highlights(view.MergedHighlights, props.OpenHighlight, props.OpenStoreListing, layout),
        };

        // The kinds the shown releases carry, in document order: the SelectorBar's words after "All".
        var kinds = new List<string>(6);
        foreach (var entry in shown)
            foreach (var section in entry.Doc.Sections)
                if (section.Items is { Length: > 0 } && !kinds.Contains(section.Kind)) kinds.Add(section.Kind);
        int chosen = filter.Value;
        string? only = chosen > 0 && chosen <= kinds.Count ? kinds[chosen - 1] : null;
        if (kinds.Count > 0)
        {
            main.Add(SectionHeading(Loc.Get(Strings.WhatsNew.AllChanges)));
            main.Add(ChangeFilter(kinds, filter));
        }

        for (int i = 0; i < shown.Length; i++)
        {
            var entry = shown[i];
            if (shown.Length > 1 && i > 0) main.Add(ReleaseDivider(entry.Doc));
            foreach (var notice in entry.Doc.Notices)
                main.Add(InfoBar.Create(NoticeIsWarning(notice.Kind) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
                    notice.Text, "", isClosable: false));
            foreach (var section in entry.Doc.Sections)
                if (section.Items is { Length: > 0 })   // document order; empty sections are skipped entirely
                    main.Add(ChangeGroup(section, entry.Doc.Version, props.OpenUrl, visible: only is null || only == section.Kind));
        }
        main.Add(new BoxEl { Height = K.PageTailHeight, HitTestVisible = false });

        var column = new List<Element>(2);
        if (stacked) column.Add(SinceBanner(props, releases, layout));   // ABOVE the scroller, never inside it
        column.Add(ScrollView(new BoxEl
        {
            Direction = 1, Gap = layout.BodyGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(0f, 0f, 6f, 0f),
            Children = main.ToArray(),
        }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:" + view.SelectedVersion });

        // The release switch: the rail beside the body, or a picker (a zero-row index draws neither).
        Element? rail = null, picker = null;
        if (layout.HasRail) rail = ReleaseRail(props, view.Index, view.SelectedVersion, props.RunningVersion, layout);
        else if (RailRows(view.Index) is { Count: > 0 } rows) picker = ReleasePicker(props, rows, view.SelectedVersion, layout);

        return Frame(
            new BoxEl
            {
                Key = "rn:body", Direction = 1, Gap = Spacing.M, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, MaxWidth = layout.BodyMaxW,
                Children = column.ToArray(),
            },
            rail, picker, gutter, layout);
    }

    /// <summary>The changelog's filter: the stock SelectorBar, "All" then one word per section kind. Keyed by its words: the bar keeps the items it was built with,
    /// so another release's set of sections mounts a fresh one.</summary>
    static Element ChangeFilter(List<string> kinds, Signal<int> filter)
    {
        var words = new string[kinds.Count + 1];
        words[0] = Loc.Get(Strings.WhatsNew.All);
        for (int i = 0; i < kinds.Count; i++) words[i + 1] = Loc.Get(SectionTitleLocKey(kinds[i]));
        if (filter.Peek() >= words.Length) filter.Value = 0;
        return new BoxEl
        {
            Key = "rn:filter:" + string.Join("|", kinds), Direction = 0, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children = [SelectorBar.Create(words, filter)],
        };
    }

    /// <summary>"Since you last looked": the stock informational InfoBar with "Only the latest" as its action.</summary>
    static Element SinceBanner(WhatsNewPageProps props, ReleaseEntry[] releases, ClassicReleaseNotesLayout layout)
        => InfoBar.Create(InfoBarSeverity.Informational, "", Strings.WhatsNew.Since(releases.Length, releases[^1].Doc.Version, releases[0].Doc.Version),
            isClosable: false, actionButton: Btn(Loc.Get(Strings.WhatsNew.OnlyLatest), props.ShowOnlyLatest, ButtonAppearance.Standard, ControlSize.Medium, layout.Target));

    /// <summary>The page's frame: the head (the title, and the picker beside it at Small or under it at Compact, its height reserved even while there is no picker
    /// yet), and the body beside the rail. The rail and the picker are keyed, fading wrappers, so a crossing at 1008 cross-fades them instead of snapping one for
    /// the other. The head is ONE grid at every tier (one track, or the title's and the picker's), so a crossing at 528 moves the SAME picker (its open list,
    /// its focus) from beside the title to under it instead of remounting it in another row.</summary>
    static Element Frame(Element body, Element? rail, Element? picker, float gutter, ClassicReleaseNotesLayout layout)
    {
        bool switcher = !layout.HasRail;                         // Compact and Small switch releases with a picker
        bool under = switcher && !layout.PickerInTitleRow;       // Compact: the picker sits under the title
        var head = new List<Element>(2)
        {
            new BoxEl
            {
                Key = "rn:title", Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Margin = new Edges4(0f, 0f, 0f, under ? layout.TitleBottom : 0f),
                Children = [R.Type.SurfaceDisplay(Loc.Get(Strings.WhatsNew.Title)) with { Grow = 1f }],
            },
        };
        if (switcher)
            head.Add(new BoxEl
            {
                Key = "rn:picker-slot", Direction = 0, MinHeight = layout.PickerHeight, MinWidth = 0f,
                AlignItems = FlexAlign.Center, Layout = ClassicTierMotion.Shove,   // a crossing moves the same slot beside or under the title
                Children = picker is null ? [] : [picker.Value],
            });

        var rows = new List<Element>(2)
        {
            new GridEl
            {
                Key = "rn:head", Columns = switcher && !under ? s_titleAndPicker : s_titleOnly, ColGap = Spacing.M,
                Padding = new Edges4(gutter, layout.PageTop, gutter, under ? Spacing.XS : layout.TitleBottom),
                Children = head.ToArray(),
            },
        };
        rows.Add(new BoxEl
        {
            Key = "rn:frame", Direction = 0, Gap = layout.FrameGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(gutter, 0f, gutter, 0f),
            Children = rail is null ? [body] : [body, rail.Value],   // loading / empty: the rail's column AND its gap are gone
        });
        return new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Children = rows.ToArray() };
    }

    // The head's track lists (shared, never mutated: a grid keeps its track array by reference).
    static readonly TrackSize[] s_titleOnly = [TrackSize.Star()];
    static readonly TrackSize[] s_titleAndPicker = [TrackSize.Star(), TrackSize.Auto];

    static Element LoadingState() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [ProgressRing.Create(size: K.LoadingRingSize)],
    };

    static Element EmptyState(WhatsNewPageProps props, ClassicReleaseNotesLayout layout)
    {
        var openUrl = props.OpenUrl;
        return new BoxEl
        {
            Grow = 1f, Direction = 1, Gap = Spacing.M, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                Icon(Icons.Tag, 32f, Tok.TextTertiary),
                R.Type.DenseMeta(Loc.Get(Strings.WhatsNew.Empty)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = K.EmptyTextMaxWidth },
                Btn(Loc.Get(Strings.WhatsNew.OpenOnGitHub), () => openUrl(Links.ReleasesUrl), ButtonAppearance.Standard, ControlSize.Medium, layout.Target),
            ],
        };
    }

    /// <summary>An older release's name over its changes when several are stacked: a Subtitle, like the page's other blocks.</summary>
    static Element ReleaseDivider(ReleaseNotesDocument doc) => SectionHeading(DividerLabel(doc.Version, doc.Name, ReleaseDate(doc.Date)));

    // ── the release head ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What this release IS, when it landed, and the two links out: the title, the tagline, one caption line of facts and the actions. Document data only —
    /// it renders identically for the running build, an older release the rail selected and a beta the user is only reading about.</summary>
    static Element Head(WhatsNewPageProps props, ReleaseNotesDocument doc, ClassicReleaseNotesLayout layout)
    {
        var openUrl = props.OpenUrl;
        var copy = props.CopyLink;
        string release = doc.Links.Release is { Length: > 0 } r ? r : Links.ReleaseTagUrl(doc.Version);

        // Loc keys, not concatenation: the quotation marks are typography a locale must be able to move.
        string headline = doc.Name is { Length: > 0 } name ? Strings.WhatsNew.Headline(doc.Version, name) : Strings.WhatsNew.HeadlineBare(doc.Version);
        var kids = new List<Element>(4)
        {
            new TextEl(headline) { Size = layout.HeadlineSize, LineHeight = layout.HeadlineSize + K.HeadlineLeading, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new TextEl(doc.Tagline) { Size = K.TaglineSize, LineHeight = K.TaglineLine, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = K.HeroTaglineMaxWidth },
        };
        if (layout.HeroMeta && Facts(doc) is { Length: > 0 } facts)
            kids.Add(new TextEl(facts) { Size = K.MetaSize, LineHeight = K.MetaLine, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
        kids.Add(HeadActions(layout, () => openUrl(release), () => copy(release)));
        return new BoxEl { Key = "rn:hero", Direction = 1, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>"Released 26 Oct 2026 · Requires 10.0.19041.0 · Tag wavee-v0.3.6", with "Beta" first on a beta.</summary>
    static string Facts(ReleaseNotesDocument doc)
    {
        var parts = new List<string>(4);
        if (string.Equals(doc.Channel, "beta", StringComparison.OrdinalIgnoreCase)) parts.Add(Loc.Get(Strings.WhatsNew.Beta));
        if (ReleaseDate(doc.Date) is { Length: > 0 } released) parts.Add(Loc.Get(Strings.WhatsNew.Released) + " " + released);
        if (doc.MinOs is { Length: > 0 } minOs) parts.Add(Loc.Get(Strings.WhatsNew.Requires) + " " + minOs);
        parts.Add(Loc.Get(Strings.WhatsNew.Tag) + " " + ReleaseNotesValidation.TagPrefix + doc.Version);
        return string.Join("  ·  ", parts);
    }

    /// <summary>The head's two actions under the words: a row (a Standard button, then a Subtle one), or a two-column grid of 40-high buttons at Compact.</summary>
    static Element HeadActions(ClassicReleaseNotesLayout layout, Action openRelease, Action copyLink)
    {
        string openLabel = Loc.Get(Strings.WhatsNew.OpenOnGitHub), copyLabel = Loc.Get(Strings.WhatsNew.CopyLink);
        float target = layout.Target;
        ControlSize size = layout.ActionSize;
        if (layout.HeroActions == ClassicReleaseHeroActions.RowBelow)
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.S, 0f, 0f), AlignSelf = FlexAlign.Start, AlignItems = FlexAlign.Center,
                Children =
                [
                    Btn(openLabel, openRelease, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:open" },
                    Btn(copyLabel, copyLink, ButtonAppearance.Subtle, size, target) with { Key = "rn:hero:copy" },
                ],
            };
        return new BoxEl   // GridBelow: two equal columns
        {
            Direction = 0, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.S, 0f, 0f), AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                Btn(openLabel, openRelease, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:open", Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f },
                Btn(copyLabel, copyLink, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:copy", Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f },
            ],
        };
    }

    // ══ THE RELEASE PICKER (Compact and Small) ═══════════════════════════════════════════════════════════════════════

    /// <summary>What the picker is drawn from: the releases (newest first), the one on screen, the verb that opens another and the tier's hit target.</summary>
    sealed record PickerArgs(IReadOnlyList<ReleaseNotesIndexEntry> Rows, string Selected, Action<string> Open, float Target);

    /// <summary>The picker in its keyed, fading wrapper: where the rail was, a tier crossing cross-fades one for the other.</summary>
    static Element ReleasePicker(WhatsNewPageProps props, IReadOnlyList<ReleaseNotesIndexEntry> rows, string selected, ClassicReleaseNotesLayout layout) => new BoxEl
    {
        Key = "rn:picker", Animate = K.FormFade, Shrink = 0f, MinHeight = layout.PickerHeight, AlignItems = FlexAlign.Center,
        Children = [Embed.Comp(new PickerArgs(rows, selected, props.OpenRelease, layout.Target), static () => new ReleasePickerView())],
    };

    /// <summary>"0.3.0  ·  Oct 2026": a release's line in the picker and on its button.</summary>
    static string PickerLabel(ReleaseNotesIndexEntry e)
        => ReleaseDate(e.Date) is { Length: > 0 } date ? e.Version + "  ·  " + date : e.Version;

    /// <summary>Choosing a release opens its page (a NEW keep-alive slot keyed by the version, like a rail row). At the hit target the choices are the stock combo at its
    /// 40-DIP field (`CommonTouch.Picker`); below it the stock combo at its own. The combo freezes its items, signal and handler at mount, so the signal follows the
    /// selection through an effect, the handler reads the CURRENT rows, and the combo is keyed by what it was built from.</summary>
    sealed class ReleasePickerView : Component
    {
        readonly Signal<int> _selected = new(0);
        PickerArgs _args = null!;

        public override Element Render()
        {
            var a = _args = UseProps<PickerArgs>();
            int index = 0;
            var labels = new string[a.Rows.Count];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = PickerLabel(a.Rows[i]);
                if (string.Equals(a.Rows[i].Version, a.Selected, StringComparison.Ordinal)) index = i;
            }
            UseEffect(() => { _selected.Value = index; }, DepKey.From(index));

            return Wavee.Themes.Common.CommonTouch.Picker(labels, _selected, K.PickerWidth, a.Target, Pick)
                with { Key = "rn:picker:combo:" + labels.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }

        void Pick(int i)
        {
            var a = _args;
            if ((uint)i < (uint)a.Rows.Count) a.Open(a.Rows[i].Version);
        }
    }

    // ══ THE RELEASE RAIL ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every release the index knows, newest first, as a list: the version over "Crest · 26 Oct 2026", "Installed" on the running build, and WinUI's
    /// selection pill on the release on screen. No index ⇒ a zero-width, hit-invisible box.</summary>
    static Element ReleaseRail(WhatsNewPageProps props, ReleaseNotesIndex? index, string selected, string running, ClassicReleaseNotesLayout layout)
    {
        IReadOnlyList<ReleaseNotesIndexEntry> rows = RailRows(index);   // snapshotted ONCE: the For thunk stays cheap
        if (rows.Count == 0) return new BoxEl { Width = 0f, HitTestVisible = false };
        var open = props.OpenRelease;
        return new BoxEl
        {
            Key = "rn:rail", Animate = K.FormFade,
            Direction = 1, Gap = K.RailGap, Width = layout.RailWidth, Shrink = 0f, MinHeight = 0f,
            Children =
            [
                new TextEl(Loc.Get(Strings.WhatsNew.Releases)) { Size = K.BodySize, LineHeight = K.BodyLine, Weight = 600, Color = Tok.TextPrimary, Margin = new Edges4(K.RailInset, 0f, 0f, 0f) },
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = K.RailListGap, MinWidth = 0f,
                    Children = [Flow.For(() => rows, static e => e.Version, e => RailRow(e, selected, running, open))],
                }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:rail" },
            ],
        };
    }

    static Element RailRow(ReleaseNotesIndexEntry e, string selected, string running, Action<string> open)
    {
        bool isSelected = string.Equals(e.Version, selected, StringComparison.Ordinal);
        string caption = RailSubtitle(e.Name, ReleaseDate(e.Date));
        if (string.Equals(e.Channel, "beta", StringComparison.OrdinalIgnoreCase)) caption += "  ·  " + Loc.Get(Strings.WhatsNew.Beta);
        if (string.Equals(e.Version, running, StringComparison.Ordinal)) caption += "  ·  " + Loc.Get(Strings.WhatsNew.Installed);

        string version = e.Version;
        var row = new BoxEl
        {
            Key = "rail:" + version,
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(0f, K.RailRowPadY, K.RailInset, K.RailRowPadY), Corners = Radii.ControlAll,
            Role = AutomationRole.NavigationItem, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => open(version),   // a NEW keep-alive slot, keyed by the arg
            Children =
            [
                // WinUI's selection pill (NavigationViewItem): 3 x 16 at the row's leading edge; a clear one keeps the column on every row.
                new BoxEl
                {
                    Width = K.RailPillW, Height = K.RailPillH, Shrink = 0f, Corners = CornerRadius4.All(K.RailPillW / 2f),
                    Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Margin = new Edges4(K.RailInset - K.RailPillW, 0f, 0f, 0f),
                    Children =
                    [
                        new TextEl(version) { Size = K.BodySize, LineHeight = K.BodyLine, Weight = isSelected ? (ushort)600 : (ushort)400, Color = Tok.TextPrimary, MaxLines = 1 },
                        new TextEl(caption) { Size = K.MetaSize, LineHeight = K.MetaLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
            ],
        }.Interactive(Interaction.ListRow);
        return isSelected ? row with { Fill = Tok.FillSubtleSecondary, HoverFill = Tok.FillSubtleTertiary, PressedFill = Tok.FillSubtleTertiary } : row;
    }
}
