// ── Themes/Classic/Structure/Views/Screens/ClassicReleaseNotes.cs — Classic's `Pages.WhatsNew` (themes plan 6.1, T11c) ────────────────────────
//
// From `Screens\ReleaseNotes.UI.cs`: the What's-new page (hero, highlight strip + cards, changelog sections / rows / issue chips / avatars, the release rail, the
// since-banner, loading and empty). `ReleaseNotes.PageHost` (a `[CoreHost]`) owns the load and its cancellation, the last-seen read BEFORE the mark-seen write,
// the issue-state republish and the "only the latest" state, and hands this view the state, the view model and the verbs; the view draws them. The elements,
// keys and numbers are what the page built before the move (the numbers are `ClassicReleaseNotesRules`).
//
//   Frame ── header (tag 22 · PageHero)
//          └ row ── body column ── [stacked] SinceBanner
//                │               └ ScrollView "whatsnew:<ver>" → Hero · Strip(CardView ×≤3)
//                │                  · per release [Divider] · InfoBar per notice · SectionView(props) per section · as-of
//                └ ReleaseRail (208, Flow.For keyed by version)          — absent while loading / empty / offline
//
// The page is a DOCUMENT, not a virtualized list (ch 28 §9.2): ≤ 3 cards, ≤ 8 + expansion rows per section, ≤ ~20 rail rows; posters are resolved in the loader,
// and nothing here touches the disk or the network. The cards, the changelog and the viewer's parts are the sibling files of this class.

using FluentGpu.Hooks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
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

    // ══ THE PAGE ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.WhatsNewPage"/>.</summary>
    public static Element PageTemplate(WhatsNewPageProps props, in ViewContext ctx)
    {
        float gutter = ClassicTiers.PageOf(ctx.Tier()).Gutter;   // the one page gutter (F3)
        if (props.State != WhatsNewState.Loaded || props.View is not { } view)
            return Frame(props.State == WhatsNewState.Empty ? EmptyState(props) : LoadingState(), rail: null, gutter);

        var releases = view.Releases;
        bool stacked = releases.Length > 1 && !props.OnlyLatest;
        var shown = stacked ? releases : releases[..1];
        var merged = view.MergedHighlights;

        var main = new List<Element>(8)
        {
            Hero(props, shown[0].Doc, IsLatest(view.Index, shown[0].Doc.Version)),
            Strip(merged, props.OpenHighlight, props.OpenStoreListing),
        };
        for (int i = 0; i < shown.Length; i++)
        {
            var entry = shown[i];
            if (shown.Length > 1 && i > 0) main.Add(ReleaseDivider(entry.Doc));
            foreach (var notice in entry.Doc.Notices)
                main.Add(InfoBar.Create(NoticeIsWarning(notice.Kind) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
                    notice.Text, "", isClosable: false));
            foreach (var section in entry.Doc.Sections)
                if (section.Items is { Length: > 0 })   // document order; empty sections are skipped entirely
                    main.Add(SectionView.Create(section, entry.IssueStates, props.OpenUrl, "sec:" + entry.Doc.Version + ":" + section.Kind));
        }
        if (shown[0].Doc.GeneratedAt is { Length: > 0 } generated)
            main.Add(R.Type.MicroMeta(Strings.WhatsNew.AsOf(ReleaseDate(generated[..Math.Min(10, generated.Length)])))
                with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(0f, 6f, 0f, 0f) });
        main.Add(new BoxEl { Height = K.PageTailHeight, HitTestVisible = false });

        var column = new List<Element>(2);
        if (stacked) column.Add(SinceBanner(props, releases));   // ABOVE the scroller, never inside it
        column.Add(ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(0f, 0f, 6f, 0f),
            Children = main.ToArray(),
        }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:" + view.SelectedVersion });

        return Frame(
            new BoxEl { Direction = 1, Gap = Spacing.M, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = column.ToArray() },
            ReleaseRail(props, view.Index, view.SelectedVersion, props.RunningVersion, view.LastSeen), gutter);
    }

    static Element SinceBanner(WhatsNewPageProps props, ReleaseEntry[] releases) => new BoxEl
    {
        Direction = 0, Gap = K.SinceGap, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(12f, 8f, 12f, 8f), Corners = CornerRadius4.All(6f),
        Fill = Tok.AccentSubtle, BorderWidth = 1f, BorderColor = Tok.AccentDefault,
        Children =
        [
            Icon(Icons.RefineSparkle, 14f, Tok.AccentTextPrimary),
            R.Type.DenseMeta(Strings.WhatsNew.Since(releases.Length, releases[^1].Doc.Version, releases[0].Doc.Version))
                with { Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
            Button.Create(Loc.Get(Strings.WhatsNew.OnlyLatest), props.ShowOnlyLatest,
                ButtonAppearance.Subtle, ControlSize.Small) with { Shrink = 0f },
        ],
    };

    static Element Frame(Element body, Element? rail, float gutter) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(gutter, Spacing.L, gutter, Spacing.M),
                Children = [Icon(Icons.Tag, 22f, Tok.TextPrimary), R.Type.SurfaceDisplay(Loc.Get(Strings.WhatsNew.Title)) with { Grow = 1f }],
            },
            new BoxEl
            {
                Direction = 0, Gap = K.FrameGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Padding = new Edges4(gutter, 0f, gutter, 0f),
                Children = rail is null ? [body] : [body, rail],   // loading / empty: the 208 column AND the 14 gap are gone
            },
        ],
    };

    static Element LoadingState() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [ProgressRing.Create(size: K.LoadingRingSize)],
    };

    static Element EmptyState(WhatsNewPageProps props)
    {
        var openUrl = props.OpenUrl;
        return new BoxEl
        {
            Grow = 1f, Direction = 1, Gap = Spacing.M, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                Icon(Icons.Tag, 32f, Tok.TextTertiary),
                R.Type.DenseMeta(Loc.Get(Strings.WhatsNew.Empty)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = K.EmptyTextMaxWidth },
                Button.Standard(Loc.Get(Strings.WhatsNew.OpenOnGitHub), () => openUrl(Links.ReleasesUrl)),
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
    /// for the running build, an older release the rail selected and a beta the user is only reading about.</summary>
    static Element Hero(WhatsNewPageProps props, ReleaseNotesDocument doc, bool isLatest)
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

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = new Edges4(24f, 22f, 24f, 20f), Corners = CornerRadius4.All(12f),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = pills.ToArray() },
                        new TextEl(headline) { Size = K.HeroHeadlineSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new TextEl(doc.Tagline) { Size = K.BodySize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = K.HeroTaglineMaxWidth },
                        new BoxEl { Direction = 0, Gap = K.HeroMetaGap, Wrap = true, Margin = new Edges4(0f, 4f, 0f, 0f), Children = meta.ToArray() },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.End,
                    Children =
                    [
                        Button.Create(Loc.Get(Strings.WhatsNew.OpenOnGitHub), () => openUrl(release), ButtonAppearance.Standard, ControlSize.Small),
                        Button.Create(Loc.Get(Strings.WhatsNew.CopyLink), () => copy(release), ButtonAppearance.Subtle, ControlSize.Small),
                    ],
                },
            ],
        };
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

    // ══ THE RELEASE RAIL ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every release the index knows, newest first. YOU is the RUNNING build; the dot is "not looked at yet"
    /// (newer than the lastSeen the page read before advancing). No index ⇒ a zero-width, hit-invisible box.</summary>
    static Element ReleaseRail(WhatsNewPageProps props, ReleaseNotesIndex? index, string selected, string running, string lastSeen)
    {
        IReadOnlyList<ReleaseNotesIndexEntry> rows = RailRows(index);   // snapshotted ONCE: the For thunk stays cheap
        if (rows.Count == 0) return new BoxEl { Width = 0f, HitTestVisible = false };
        var open = props.OpenRelease;
        return new BoxEl
        {
            Direction = 1, Gap = K.RailGap, Width = K.RailWidth, Shrink = 0f, MinHeight = 0f,
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
