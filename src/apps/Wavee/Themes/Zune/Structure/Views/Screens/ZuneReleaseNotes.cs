// ── Themes/Zune/Structure/Views/Screens/ZuneReleaseNotes.cs — Zune's `Pages.WhatsNew` (themes plan 6.3, P5 Z8b) ───────────────────────────────
//
// Board Z-ReleaseNotes (and -Compact, -Small, -Huge): the panorama title "what's new", then a main column (the since line, the release title at 40/300, the tagline, the
// highlights as panels in a row, the changelog groups: a 22/300 heading and a line per change with its issue chip, its contributor and their name) beside the RELEASE RAIL
// (208: "releases", a word per version with its tags). `ReleaseNotes.PageHost` (a `[CoreHost]`) owns the load and its cancellation, the last-seen read BEFORE the mark-seen
// write, the issue-state republish and the "only the latest" state, and hands this view the state, the view model and the verbs; the view draws them.
//
// EVERY TIER. The page reads its record, `ZuneReleaseNotesTiers.Of(ctx.Tier())` (Rules\ZuneSetupRules.cs): the rail at Small and up (160, 208 at Standard), a picker under the title
// at Compact; the highlights in three columns (one at Compact); the title the panorama or its compact step. The first frame is already in the final tier; the rail and the picker swap
// with the shared fade and the rest reflows (`ZuneMotion`).
//
//   Frame ── head (key `zrn:head`): the title, [Compact: the picker slot, its height reserved while loading]
//          └ row ── body column ── [stacked] since line
//                │               └ ScrollView "whatsnew:<ver>" → Hero · Highlights (panel x ≤ 3) · per release [heading] · notices · groups · as-of
//                └ ReleaseRail (Flow.For keyed by version)          absent while loading / empty / offline, and at Compact
//
// The page is a DOCUMENT, not a virtualized list: at most three highlights, eight plus the expansion of rows per group, about twenty rail rows; posters are resolved in the loader
// and nothing here touches the disk or the network.

using System.Globalization;
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
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.Zune.ZuneReleaseNotesRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's release-notes surfaces: the What's-new page, the highlight viewer and the after-update plate.</summary>
internal static partial class ZuneReleaseNotes
{
    /// <summary>Registers the three keys.</summary>
    public static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.WhatsNewPage, PageTemplate, "zune.release-notes.page");
        views.Add(ViewKeys.ReleaseNotesViewer, ViewerTemplate, "zune.release-notes.viewer");
        views.Add(ViewKeys.ReleaseNotesAfterUpdatePlate, PlateTemplate, "zune.release-notes.after-update-plate");
    }

    static string ReleaseDate(string? iso) => Links.Date(iso, Loc.Get(Strings.WhatsNew.DateFormat));

    static readonly Action<string> IgnoreLink = static _ => { };
    static readonly Action Inert = static () => { };

    static ZuneKit KitOf(ZuneReleaseNotesLayout layout) => new(ZuneSettingsTiers.For(layout.Tier));

    // ══ THE PAGE ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.WhatsNewPage"/>.</summary>
    public static Element PageTemplate(WhatsNewPageProps props, in ViewContext ctx)
    {
        var tiers = ctx.Tier();
        var layout = ZuneReleaseNotesTiers.Of(tiers);
        if (props.State != WhatsNewState.Loaded || props.View is not { } view)
            return Frame(props.State == WhatsNewState.Empty ? EmptyState(props, layout) : LoadingState(), rail: null, picker: null, layout);

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
            if (shown.Length > 1 && i > 0) main.Add(ReleaseHeading(entry.Doc));
            foreach (var notice in entry.Doc.Notices)
                main.Add(ZuneKit.Notice(NoticeIsWarning(notice.Kind) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational, notice.Text, ""));
            foreach (var section in entry.Doc.Sections)
                if (section.Items is { Length: > 0 })   // document order; empty sections are skipped entirely
                    main.Add(SectionView.Create(section, entry.IssueStates, props.OpenUrl, "zrn:sec:" + entry.Doc.Version + ":" + section.Kind, layout));
        }
        if (shown[0].Doc.GeneratedAt is { Length: > 0 } generated)
            main.Add(ZuneText.Hint(Strings.WhatsNew.AsOf(ReleaseDate(generated[..Math.Min(K.IsoDateLength, generated.Length)]))));
        main.Add(new BoxEl { Height = K.PageTailHeight, HitTestVisible = false });

        var column = new List<Element>(2);
        if (stacked) column.Add(SinceLine(props, releases, layout));   // ABOVE the scroller, never inside it
        column.Add(ScrollView(new BoxEl
        {
            Direction = 1, Gap = layout.BodyGap, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Padding = new Edges4(0f, 0f, K.ScrollInset, 0f),
            Children = main.ToArray(),
        }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:" + view.SelectedVersion });

        // The release switch: the rail beside the body, or a picker (a zero-row index draws neither).
        Element? rail = null, picker = null;
        if (layout.HasRail) rail = ReleaseRail(props, view.Index, view.SelectedVersion, props.RunningVersion, view.LastSeen, layout);
        else if (RailRows(view.Index) is { Count: > 0 } rows) picker = ReleasePicker(props, rows, view.SelectedVersion, layout);

        return Frame(
            new BoxEl
            {
                Key = "zrn:body", Direction = 1, Gap = Spacing.M, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, MaxWidth = layout.BodyMaxW, Animate = ZuneMotion.ColumnCollapse,
                Children = column.ToArray(),
            },
            rail, picker, layout);
    }

    /// <summary>The since line: "since you last looked: 3 releases · 1.4.0 → 1.6.0" and "only the latest".</summary>
    static Element SinceLine(WhatsNewPageProps props, ReleaseEntry[] releases, ZuneReleaseNotesLayout layout) => new BoxEl
    {
        Key = "zrn:since", Direction = 0, Wrap = true, Gap = K.SinceGap, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Children =
        [
            R.Type.DenseMeta(Strings.WhatsNew.Since(releases.Length, releases[^1].Doc.Version, releases[0].Doc.Version))
                with { Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
            KitOf(layout).Cmd(Loc.Get(Strings.WhatsNew.OnlyLatest), props.ShowOnlyLatest),
        ],
    };

    /// <summary>The page's frame: the title (the panorama; its compact step under the compact width), the picker beside or under it where there is no rail, and the body beside the
    /// rail. The rail and the picker are keyed, fading wrappers, so a crossing cross-fades them instead of snapping one for the other. The head is ONE node at every tier whose
    /// direction and wrap are the record's, so a crossing moves the SAME picker (its open list, its focus) instead of remounting it.</summary>
    static Element Frame(Element body, Element? rail, Element? picker, ZuneReleaseNotesLayout layout)
    {
        bool switcher = !layout.HasRail;   // Compact switches releases with a picker
        string text = Loc.Get(Strings.WhatsNew.Title);
        TextEl title = layout.CompactTitle ? R.Type.PanoramaCompact(text) : R.Type.Panorama(text);
        var head = new List<Element>(2)
        {
            new BoxEl
            {
                Key = "zrn:title", Direction = 0, Shrink = 1f, MinWidth = 0f, Height = layout.TitleH, ClipToBounds = true, Animate = ZuneMotion.TitleScale,
                Padding = new Edges4(MathF.Max(0f, layout.Gutter - K.PanoramaBleed), 0f, 0f, 0f),
                Children = [ZuneText.Cased(title, LabelKind.Heading) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
            },
        };
        if (switcher)
            head.Add(new BoxEl
            {
                Key = "zrn:picker-slot", Direction = 0, MinHeight = layout.PickerHeight, MinWidth = 0f, AlignItems = FlexAlign.Center, Animate = ZuneMotion.PivotSlide,
                Padding = new Edges4(layout.Gutter, 0f, layout.Gutter, 0f),
                Children = picker is null ? [] : [picker],
            });
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
            Children =
            [
                new BoxEl { Key = "zrn:head", Direction = 1, Shrink = 0f, MinWidth = 0f, Children = head.ToArray() },
                new BoxEl
                {
                    Key = "zrn:frame", Direction = 0, Gap = layout.FrameGap, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                    Padding = new Edges4(layout.Gutter, 0f, layout.Gutter, 0f),
                    Children = rail is null ? [body] : [body, rail],   // loading / empty: the rail's column AND its gap are gone
                },
            ],
        };
    }

    static Element LoadingState() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [ProgressRing.Create(size: K.LoadingRingSize)],
    };

    static Element EmptyState(WhatsNewPageProps props, ZuneReleaseNotesLayout layout)
    {
        var openUrl = props.OpenUrl;
        return new BoxEl
        {
            Grow = 1f, Direction = 1, Gap = Spacing.M, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                ZuneText.Prose(Loc.Get(Strings.WhatsNew.Empty)) with { MaxWidth = K.EmptyTextMaxWidth },
                KitOf(layout).Go(Loc.Get(Strings.WhatsNew.OpenOnGitHub), () => openUrl(Links.ReleasesUrl)),
            ],
        };
    }

    /// <summary>A stacked release's own heading: "1.5.2 · Oct 2026" over the group voice.</summary>
    static Element ReleaseHeading(ReleaseNotesDocument doc) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0f, Spacing.S, 0f, 0f),
        Children = [ZuneText.Group(DividerLabel(doc.Version, doc.Name, ReleaseDate(doc.Date))) with { Wrap = TextWrap.Wrap }],
    };

    // ── the hero ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What this release IS, when it landed, and the two links out. Document data only: it renders identically for the running build, an older release the rail selected
    /// and a beta the user is only reading about. The title is the 40/300 voice; its tags are one line of meta.</summary>
    static Element Hero(WhatsNewPageProps props, ReleaseNotesDocument doc, bool isLatest, ZuneReleaseNotesLayout layout)
    {
        var k = KitOf(layout);
        var openUrl = props.OpenUrl;
        var copy = props.CopyLink;
        string release = doc.Links.Release is { Length: > 0 } r ? r : Links.ReleaseTagUrl(doc.Version);
        var tags = new List<string>(4);
        if (isLatest) tags.Add(Loc.Get(Strings.WhatsNew.Latest));
        tags.Add(Loc.Get(string.Equals(doc.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? Strings.WhatsNew.Beta : Strings.WhatsNew.Stable));
        if (doc.PackageVersion is { Length: > 0 } quad) tags.Add(quad);
        if (ReleaseDate(doc.Date) is { Length: > 0 } released) tags.Add(Loc.Get(Strings.WhatsNew.Released) + " " + released);
        if (doc.MinOs is { Length: > 0 } minOs) tags.Add(Loc.Get(Strings.WhatsNew.Requires) + " " + minOs);
        tags.Add(ReleaseNotesValidation.TagPrefix + doc.Version);

        // Loc keys, not concatenation: the quotation marks are typography a locale must be able to move.
        string headline = doc.Name is { Length: > 0 } name ? Strings.WhatsNew.Headline(doc.Version, name) : Strings.WhatsNew.HeadlineBare(doc.Version);
        var kids = new List<Element>(4)
        {
            R.Type.PageHero(headline) with { MinWidth = 0f, Wrap = TextWrap.Wrap },
            ZuneText.Hint(string.Join("  ·  ", tags)),
            R.Type.DenseTitle(doc.Tagline) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxWidth = K.HeroTaglineMaxWidth },
            new BoxEl
            {
                Direction = 0, Wrap = true, Gap = K.HeroActionGap, AlignItems = FlexAlign.Center, Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
                Children =
                [
                    k.Go(Loc.Get(Strings.WhatsNew.OpenOnGitHub), () => openUrl(release)) with { Key = "zrn:hero:open" },
                    k.Cmd(Loc.Get(Strings.WhatsNew.CopyLink), () => copy(release)) with { Key = "zrn:hero:copy" },
                ],
            },
        };
        return new BoxEl { Key = "zrn:hero", Direction = 1, Gap = Spacing.S, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
    }

    // ══ THE RELEASE PICKER (Compact) ═══════════════════════════════════════════════════════════════════════

    /// <summary>What the picker is drawn from: the releases (newest first), the one on screen, the verb that opens another and the tier's hit target.</summary>
    sealed record PickerArgs(IReadOnlyList<ReleaseNotesIndexEntry> Rows, string Selected, Action<string> Open, float Target);

    /// <summary>The picker in its keyed, fading wrapper: where the rail was, a tier crossing cross-fades one for the other.</summary>
    static Element ReleasePicker(WhatsNewPageProps props, IReadOnlyList<ReleaseNotesIndexEntry> rows, string selected, ZuneReleaseNotesLayout layout) => new BoxEl
    {
        Key = "zrn:picker", Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Shrink = 0f, MinHeight = layout.PickerHeight, AlignItems = FlexAlign.Center,
        Children = [Embed.Comp(new PickerArgs(rows, selected, props.OpenRelease, layout.Target), static () => new ReleasePickerView())],
    };

    /// <summary>"0.3.0  ·  Oct 2026": a release's line in the picker and on its button.</summary>
    static string PickerLabel(ReleaseNotesIndexEntry e) => ReleaseDate(e.Date) is { Length: > 0 } date ? e.Version + "  ·  " + date : e.Version;

    /// <summary>Choosing a release opens its page (a NEW keep-alive slot keyed by the version, like a rail row). The combo freezes its items, signal and handler at mount, so the
    /// signal follows the selection through an effect, the handler reads the CURRENT rows, and the combo is keyed by what it was built from.</summary>
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
            return CommonTouch.Picker(labels, _selected, K.PickerWidth, a.Target, Pick) with { Key = "zrn:picker:combo:" + labels.Length.ToString(CultureInfo.InvariantCulture) };
        }

        void Pick(int i)
        {
            var a = _args;
            if ((uint)i < (uint)a.Rows.Count) a.Open(a.Rows[i].Version);
        }
    }

    // ══ THE RELEASE RAIL ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every release the index knows, newest first. "you" is the RUNNING build; the dot is "not looked at yet" (newer than the lastSeen the page read before advancing). No
    /// index: a zero-width, hit-invisible box. The open one carries the accent rule at its left edge (the board's inset 2).</summary>
    static Element ReleaseRail(WhatsNewPageProps props, ReleaseNotesIndex? index, string selected, string running, string lastSeen, ZuneReleaseNotesLayout layout)
    {
        IReadOnlyList<ReleaseNotesIndexEntry> rows = RailRows(index);   // snapshotted ONCE: the For thunk stays cheap
        if (rows.Count == 0) return new BoxEl { Width = 0f, HitTestVisible = false };
        var open = props.OpenRelease;
        return new BoxEl
        {
            Key = "zrn:rail", Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
            Direction = 1, Gap = K.RailGap, Width = layout.RailWidth, Shrink = 0f, MinHeight = 0f,
            Children =
            [
                ZuneText.Heading(Loc.Get(Strings.WhatsNew.Releases)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = K.RailListGap, MinWidth = 0f,
                    Children = [Flow.For(() => rows, static e => e.Version, e => RailRow(e, selected, running, lastSeen, open))],
                }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:rail" },
                ZuneText.Hint(Loc.Get(Strings.WhatsNew.RailFoot)),
            ],
        };
    }

    static Element RailRow(ReleaseNotesIndexEntry e, string selected, string running, string lastSeen, Action<string> open)
    {
        bool isSelected = string.Equals(e.Version, selected, StringComparison.Ordinal);
        var tags = new List<string>(3);
        switch (RailMarkerFor(e.Version, selected, running, lastSeen))
        {
            case RailMarker.You: tags.Add(Loc.Get(Strings.WhatsNew.You)); break;
        }
        if (string.Equals(e.Channel, "beta", StringComparison.OrdinalIgnoreCase)) tags.Add(Loc.Get(Strings.WhatsNew.Beta));   // independent of the marker
        var title = new List<Element>(2)
        {
            R.Type.TrackTitle(e.Version) with { Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary, Weight = isSelected ? K.RailSelectedWeight : K.RailRestWeight, MaxLines = 1 },
        };
        if (RailMarkerFor(e.Version, selected, running, lastSeen) == RailMarker.Unread)
            title.Add(new BoxEl { Width = K.RailDot, Height = K.RailDot, Shrink = 0f, Corners = CornerRadius4.All(ZuneControls.Round), Fill = R.Ink.AccentAction });

        string version = e.Version;
        string sub = RailSubtitle(e.Name, ReleaseDate(e.Date));
        if (tags.Count > 0) sub = string.Join("  ·  ", tags) + (sub.Length > 0 ? "  ·  " + sub : "");
        return new BoxEl
        {
            Key = "zrn:rail:" + version, Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, MinWidth = 0f, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
            Role = AutomationRole.NavigationItem, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => open(version),   // a NEW keep-alive slot, keyed by the arg
            Children =
            [
                new BoxEl { Width = K.RailRule, Shrink = 0f, AlignSelf = FlexAlign.Stretch, Fill = isSelected ? R.Ink.AccentAction : ColorF.Transparent },
                new BoxEl
                {
                    Direction = 1, Gap = K.RailLineGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = K.RailRowGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = title.ToArray() },
                        R.Type.MicroMeta(sub) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }
}
