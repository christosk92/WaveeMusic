// ── Themes/Classic/Structure/Views/Screens/ClassicReleaseNotes.cs — Classic's `Pages.WhatsNew` (themes plan 6.1, T11c; P3t TC8) ────────────────────────
//
// From `Screens\ReleaseNotes.UI.cs`: the What's-new page (hero, highlight strip + cards, changelog sections / rows / issue chips / avatars, the release rail, the
// since-banner, loading and empty). `ReleaseNotes.PageHost` (a `[CoreHost]`) owns the load and its cancellation, the last-seen read BEFORE the mark-seen write,
// the issue-state republish and the "only the latest" state, and hands this view the state, the view model and the verbs; the view draws them. The elements,
// keys and numbers are what the page built before the move (the numbers are `ClassicReleaseNotesRules`).
//
// EVERY TIER (TC8). The page reads its record, `ClassicReleaseNotesTiers.Of(ctx.Tier())` (Rules\Tiers.ReleaseNotes.cs): the release rail at Standard and Huge, a picker
// button at Compact (under the title, 40) and Small (in the title row, 32); the hero's actions, the highlight cards (a strip, a 2-column grid or a column of ROW
// cards), the changelog's section columns and the body column's cap are all the record's. The first frame is already in the final tier; a crossing swaps the rail and
// the picker with the shared fade and the rest FLIPs.
//
//   Frame ── title row (key `rn:title`: glyph · PageHero · [Small: picker])
//          ├ [Compact] picker row (key `rn:picker-row`, its height reserved while loading)
//          └ row ── body column (<= 1600 at Huge) ── [stacked] SinceBanner
//                │               └ ScrollView "whatsnew:<ver>" → Hero · Strip(CardView ×≤3 | row cards)
//                │                  · per release [Divider] · InfoBar per notice · SectionView(props) per section (1 or 3 columns) · as-of
//                └ ReleaseRail (208 / 280, Flow.For keyed by version)          — absent while loading / empty / offline, and at Compact / Small
//
// The page is a DOCUMENT, not a virtualized list (ch 28 §9.2): ≤ 3 cards, ≤ 8 + expansion rows per section, ≤ ~20 rail rows; posters are resolved in the loader,
// and nothing here touches the disk or the network. The cards, the changelog and the viewer's parts are the sibling files of this class.

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

    // ══ THE PAGE ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.WhatsNewPage"/>.</summary>
    public static Element PageTemplate(WhatsNewPageProps props, in ViewContext ctx)
    {
        var tiers = ctx.Tier();
        var layout = ClassicReleaseNotesTiers.Of(tiers);
        float gutter = ClassicTiers.PageOf(tiers).Gutter;   // the one page gutter (F3)
        if (props.State != WhatsNewState.Loaded || props.View is not { } view)
            return Frame(props.State == WhatsNewState.Empty ? EmptyState(props, layout) : LoadingState(), rail: null, picker: null, gutter, layout);

        var releases = view.Releases;
        bool stacked = releases.Length > 1 && !props.OnlyLatest;
        var shown = stacked ? releases : releases[..1];
        var merged = view.MergedHighlights;

        var main = new List<Element>(8)
        {
            Hero(props, shown[0].Doc, IsLatest(view.Index, shown[0].Doc.Version), layout),
            Strip(merged, props.OpenHighlight, props.OpenStoreListing, layout),
        };
        for (int i = 0; i < shown.Length; i++)
        {
            var entry = shown[i];
            if (shown.Length > 1 && i > 0) main.Add(ReleaseDivider(entry.Doc));
            foreach (var notice in entry.Doc.Notices)
                main.Add(InfoBar.Create(NoticeIsWarning(notice.Kind) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
                    notice.Text, "", isClosable: false));
            var sections = new List<Element>(4);
            foreach (var section in entry.Doc.Sections)
                if (section.Items is { Length: > 0 })   // document order; empty sections are skipped entirely
                    sections.Add(SectionView.Create(section, entry.IssueStates, props.OpenUrl, "sec:" + entry.Doc.Version + ":" + section.Kind, layout));
            AddSections(main, sections, entry.Doc.Version, layout);
        }
        if (shown[0].Doc.GeneratedAt is { Length: > 0 } generated)
            main.Add(R.Type.MicroMeta(Strings.WhatsNew.AsOf(ReleaseDate(generated[..Math.Min(10, generated.Length)])))
                with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(0f, 6f, 0f, 0f) });
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
        if (layout.HasRail) rail = ReleaseRail(props, view.Index, view.SelectedVersion, props.RunningVersion, view.LastSeen, layout);
        else if (RailRows(view.Index) is { Count: > 0 } rows) picker = ReleasePicker(props, rows, view.SelectedVersion, layout);

        return Frame(
            new BoxEl
            {
                Key = "rn:body", Direction = 1, Gap = Spacing.M, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, MaxWidth = layout.BodyMaxW,
                Children = column.ToArray(),
            },
            rail, picker, gutter, layout);
    }

    /// <summary>One release's sections: stacked (every tier but Huge) or set side by side in rows of <see cref="ClassicReleaseNotesLayout.SectionColumns"/>, each
    /// column an equal share, a short last row padded so its columns keep the same width.</summary>
    static void AddSections(List<Element> main, List<Element> sections, string version, ClassicReleaseNotesLayout layout)
    {
        if (layout.SectionColumns <= 1)
        {
            main.AddRange(sections);
            return;
        }
        int columns = layout.SectionColumns;
        for (int start = 0; start < sections.Count; start += columns)
        {
            var cells = new Element[columns];
            for (int c = 0; c < columns; c++)
            {
                int at = start + c;
                cells[c] = new BoxEl
                {
                    Key = "secw:" + version + ":" + at.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children = at < sections.Count ? [sections[at]] : [],
                };
            }
            main.Add(new BoxEl
            {
                Key = "secrow:" + version + ":" + (start / columns).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Direction = 0, Gap = layout.SectionColumnGap, AlignItems = FlexAlign.Start, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children = cells,
            });
        }
    }

    static Element SinceBanner(WhatsNewPageProps props, ReleaseEntry[] releases, ClassicReleaseNotesLayout layout) => new BoxEl
    {
        Direction = 0, Gap = K.SinceGap, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(12f, 8f, 12f, 8f), Corners = CornerRadius4.All(6f),
        Fill = Tok.AccentSubtle, BorderWidth = 1f, BorderColor = Tok.AccentDefault,
        Children =
        [
            Icon(Icons.RefineSparkle, 14f, Tok.AccentTextPrimary),
            R.Type.DenseMeta(Strings.WhatsNew.Since(releases.Length, releases[^1].Doc.Version, releases[0].Doc.Version))
                with { Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
            Btn(Loc.Get(Strings.WhatsNew.OnlyLatest), props.ShowOnlyLatest, ButtonAppearance.Subtle, ControlSize.Small, layout.Target) with { Shrink = 0f },
        ],
    };

    /// <summary>The page's frame: the title row (the picker rides it at Small), the Compact picker row (its height reserved even while there is no picker yet), and the
    /// body beside the rail. The rail and the picker are keyed, fading wrappers, so a tier crossing cross-fades them instead of snapping one for the other.</summary>
    static Element Frame(Element body, Element? rail, Element? picker, float gutter, ClassicReleaseNotesLayout layout)
    {
        var title = new List<Element>(3);
        if (layout.TitleGlyph) title.Add(Icon(Icons.Tag, 22f, Tok.TextPrimary));
        title.Add(R.Type.SurfaceDisplay(Loc.Get(Strings.WhatsNew.Title)) with { Grow = 1f });
        if (layout.PickerInTitleRow && picker is not null) title.Add(picker);

        var rows = new List<Element>(3)
        {
            new BoxEl
            {
                Key = "rn:title", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(gutter, layout.PageTop, gutter, layout.TitleBottom),
                Children = title.ToArray(),
            },
        };
        if (!layout.HasRail && !layout.PickerInTitleRow)   // Compact: under the title, its height held while the release list loads
            rows.Add(new BoxEl
            {
                Key = "rn:picker-row", Direction = 0, MinHeight = layout.PickerHeight, AlignItems = FlexAlign.Start,
                Padding = new Edges4(gutter, 0f, gutter, Spacing.XS),
                Children = picker is null ? [] : [picker],
            });
        rows.Add(new BoxEl
        {
            Key = "rn:frame", Direction = 0, Gap = layout.FrameGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(gutter, 0f, gutter, 0f),
            Children = rail is null ? [body] : [body, rail],   // loading / empty: the rail's column AND its gap are gone
        });
        return new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Children = rows.ToArray() };
    }

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

    static Element ReleaseDivider(ReleaseNotesDocument doc) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
        Margin = new Edges4(0f, Spacing.S, 0f, 0f),
        Children =
        [
            new TextEl(DividerLabel(doc.Version, doc.Name, ReleaseDate(doc.Date))) { Size = K.MetaSize, Weight = 600, Color = Tok.TextTertiary, Shrink = 0f },
            new BoxEl { Height = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    // ── the hero ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What this release IS, when it landed, and the two links out. Document data only — it renders identically
    /// for the running build, an older release the rail selected and a beta the user is only reading about. The record decides
    /// the headline's size, the padding and where the two actions sit.</summary>
    static Element Hero(WhatsNewPageProps props, ReleaseNotesDocument doc, bool isLatest, ClassicReleaseNotesLayout layout)
    {
        var openUrl = props.OpenUrl;
        var copy = props.CopyLink;
        string release = doc.Links.Release is { Length: > 0 } r ? r : Links.ReleaseTagUrl(doc.Version);
        var pills = new List<Element>(3);
        if (isLatest) pills.Add(Pill(Loc.Get(Strings.WhatsNew.Latest), accent: true));
        pills.Add(Pill(Loc.Get(string.Equals(doc.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? Strings.WhatsNew.Beta : Strings.WhatsNew.Stable)));
        if (doc.PackageVersion is { Length: > 0 } quad) pills.Add(Pill(quad, mono: true));

        var meta = new List<Element>(3);
        if (ReleaseDate(doc.Date) is { Length: > 0 } released) meta.Add(MetaItem(Loc.Get(Strings.WhatsNew.Released), released));
        if (doc.MinOs is { Length: > 0 } minOs) meta.Add(MetaItem(Loc.Get(Strings.WhatsNew.Requires), minOs));
        meta.Add(MetaItem(Loc.Get(Strings.WhatsNew.Tag), ReleaseNotesValidation.TagPrefix + doc.Version));

        // Loc keys, not concatenation: the quotation marks are typography a locale must be able to move.
        string headline = doc.Name is { Length: > 0 } name ? Strings.WhatsNew.Headline(doc.Version, name) : Strings.WhatsNew.HeadlineBare(doc.Version);

        bool beside = layout.ActionsBeside;
        var copyKids = new List<Element>(4)
        {
            new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = pills.ToArray() },
            new TextEl(headline) { Size = layout.HeadlineSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new TextEl(doc.Tagline) { Size = K.BodySize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = K.HeroTaglineMaxWidth },
        };
        if (layout.HeroMeta)
            copyKids.Add(new BoxEl { Direction = 0, Gap = K.HeroMetaGap, Wrap = true, Margin = new Edges4(0f, 4f, 0f, 0f), Children = meta.ToArray() });

        return new BoxEl
        {
            Key = "rn:hero",
            Direction = beside ? (byte)0 : (byte)1, Gap = Spacing.M, AlignItems = beside ? FlexAlign.End : FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = layout.HeroPadding, Corners = CornerRadius4.All(12f),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = beside ? 1f : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN, MinWidth = 0f,
                    Children = copyKids.ToArray(),
                },
                HeroActions(layout, () => openUrl(release), () => copy(release)),
            ],
        };
    }

    /// <summary>The hero's two actions where the record puts them: a stack beside the copy (Standard), a row beside it (Huge), a row under it (Small) or a two-column
    /// grid of 40-high buttons under it (Compact).</summary>
    static Element HeroActions(ClassicReleaseNotesLayout layout, Action openRelease, Action copyLink)
    {
        string openLabel = Loc.Get(Strings.WhatsNew.OpenOnGitHub), copyLabel = Loc.Get(Strings.WhatsNew.CopyLink);
        float target = layout.Target;
        ControlSize size = layout.ActionSize;
        switch (layout.HeroActions)
        {
            case ClassicReleaseHeroActions.StackBeside:
                return new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.End,
                    Children =
                    [
                        Btn(openLabel, openRelease, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:open" },
                        Btn(copyLabel, copyLink, ButtonAppearance.Subtle, size, target) with { Key = "rn:hero:copy" },
                    ],
                };
            case ClassicReleaseHeroActions.RowBeside:
                return new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Btn(openLabel, openRelease, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:open" },
                        Btn(copyLabel, copyLink, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:copy" },
                    ],
                };
            case ClassicReleaseHeroActions.RowBelow:
                return new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.XS, 0f, 0f), AlignSelf = FlexAlign.Start, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Btn(openLabel, openRelease, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:open" },
                        Btn(copyLabel, copyLink, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:copy" },
                    ],
                };
            default:   // GridBelow: two equal columns
                return new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.XS, 0f, 0f), AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                    Children =
                    [
                        Btn(openLabel, openRelease, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:open", Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f },
                        Btn(copyLabel, copyLink, ButtonAppearance.Standard, size, target) with { Key = "rn:hero:copy", Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f },
                    ],
                };
        }
    }

    static Element MetaItem(string label, string value) => new BoxEl
    {
        Direction = 0, Gap = K.MetaItemGap, Shrink = 0f, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = K.MetaSize, Weight = 600, Color = Tok.TextSecondary },
            new TextEl(value) { Size = K.MetaSize, Color = Tok.TextTertiary },
        ],
    };

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

    /// <summary>Every release the index knows, newest first. YOU is the RUNNING build; the dot is "not looked at yet"
    /// (newer than the lastSeen the page read before advancing). No index ⇒ a zero-width, hit-invisible box.</summary>
    static Element ReleaseRail(WhatsNewPageProps props, ReleaseNotesIndex? index, string selected, string running, string lastSeen, ClassicReleaseNotesLayout layout)
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
                R.Type.Eyebrow(Loc.Get(Strings.WhatsNew.Releases)) with { Color = Tok.TextTertiary, Margin = new Edges4(4f, 6f, 4f, 2f) },
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = K.RailListGap, MinWidth = 0f,
                    Children = [Flow.For(() => rows, static e => e.Version, e => RailRow(e, selected, running, lastSeen, open))],
                }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:rail" },
                R.Type.MicroMeta(Loc.Get(Strings.WhatsNew.RailFoot)) with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(4f, 6f, 4f, 6f) },
            ],
        };
    }

    static Element RailRow(ReleaseNotesIndexEntry e, string selected, string running, string lastSeen, Action<string> open)
    {
        bool isSelected = string.Equals(e.Version, selected, StringComparison.Ordinal);
        var title = new List<Element>(3)
        {
            R.Type.DenseTitle(e.Version) with { Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1 },
        };
        switch (RailMarkerFor(e.Version, selected, running, lastSeen))
        {
            case RailMarker.You: title.Add(RailPill(Loc.Get(Strings.WhatsNew.You), Tok.AccentDefault, Tok.TextOnAccentPrimary)); break;
            case RailMarker.Unread: title.Add(new BoxEl { Width = K.RailDot, Height = K.RailDot, Shrink = 0f, Corners = CornerRadius4.All(3f), Fill = Tok.AccentDefault }); break;
        }
        if (string.Equals(e.Channel, "beta", StringComparison.OrdinalIgnoreCase))   // independent of the two markers
            title.Add(RailPill(Loc.Get(Strings.WhatsNew.Beta), Tok.FillSubtleSecondary, Tok.TextTertiary));

        string version = e.Version;
        return new BoxEl
        {
            Key = "rail:" + version,
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Padding = Edges4.All(8f), Corners = CornerRadius4.All(6f),
            Role = AutomationRole.NavigationItem, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => open(version),   // a NEW keep-alive slot, keyed by the arg
            Children =
            [
                new BoxEl
                {
                    Width = K.RailRadio, Height = K.RailRadio, Shrink = 0f, Corners = CornerRadius4.All(4.5f), Margin = new Edges4(2f, 4f, 0f, 0f),
                    BorderWidth = K.RailRadioRing, BorderColor = isSelected ? Tok.AccentDefault : Tok.TextTertiary,
                    Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,
                },
                new BoxEl
                {
                    Direction = 1, Gap = 1f, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = K.RailRowGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = true, Children = title.ToArray() },
                        R.Type.MicroMeta(RailSubtitle(e.Name, ReleaseDate(e.Date))) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        }.Interactive(isSelected ? Interaction.ListRow : Interaction.Subtle);
    }

    static Element RailPill(string text, ColorF fill, ColorF ink) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(5f, 1f, 5f, 1f), Corners = CornerRadius4.All(6f), Fill = fill,
        Children = [R.Type.MicroMeta(text) with { Weight = 700, Color = ink }],
    };
}
