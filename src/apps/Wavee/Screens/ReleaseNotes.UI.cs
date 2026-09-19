// ── Screens/ReleaseNotes.UI.cs ─────────────────────────────────────────────────────────────────────────────────────
// the What's-new page (hero, highlight strip + cards, changelog sections / rows / issue chips / avatars, the release
// rail, the since-banner, loading and empty), the highlight viewer (+ HighlightViewerMotion in its own region), the
// after-update plate and its zero-size chrome, and the doors other owners call (InstallUi, AfterUpdateChrome,
// CrashNoticeThisLaunch, ShowSummaryAgain, RunningNotesUnread, VersionDisplayText, Pill)
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 1950 lines
// Spec: ch 28 §9.5
//
//   Shell.SetPage(WhatsNew) → Page(versionArg)                 signals: view · loaded · onlyLatest (ch 28 §1.2)
//   └ Frame ── header (tag 22 · PageHero)
//            └ row ── body column ── [stacked] SinceBanner
//                  │               └ ScrollView "whatsnew:<ver>" → Hero · Strip(CardView ×≤3)
//                  │                  · per release [Divider] · InfoBar per notice · SectionView(props) per section · as-of
//                  └ ReleaseRail (208, Flow.For keyed by version)          — absent while loading / empty / offline
//   ViewerView (overlay, ScrimVisual=false) → veil · Plate[Band(keyed img + STABLE chrome) · Pager · TextStack(sizers + slide)]
//   AfterUpdateChromeView (zero-size) → gates → loads FIRST → PlateView opens at its final height (ch 28 §9.4 option a)
//
// Every decision a test can reach is in ReleaseNotes.Host.cs's PURE region; this file is layout and wiring. The page is a
// DOCUMENT, not a virtualized list (ch 28 §9.2): ≤ 3 cards, ≤ 8 + expansion rows per section, ≤ ~20 rail rows; posters
// are resolved in the loader, and nothing here touches the disk or the network.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using M = Wavee.ReleaseNotes.HighlightCardMetrics;
using VL = Wavee.ReleaseNotes.HighlightViewerLayout;

namespace Wavee;

public static partial class ReleaseNotes
{
    // ══ 1. THE DOORS ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Registers the What's-new page (the route arg is the semver; empty = the running build, stacked with every
    /// release the reader skipped) and fills the updater's notes seams + the diagnostics receipts. Called once by
    /// <c>Settings.InstallScreens</c>.</summary>
    public static void InstallUi()
    {
        Shell.SetPage(Shell.RouteKind.WhatsNew, static (in Shell.Route r) => PageFor(Entities.Strings.Resolve(r.Arg)));
        InstallSeams();
    }

    /// <summary>Set by the report chrome (<c>Feedback.Chrome</c>, mounted BEFORE <see cref="AfterUpdateChrome"/>) when this
    /// launch surfaced a crash notice: the welcome plate then defers to the NEXT launch, still armed.</summary>
    public static bool CrashNoticeThisLaunch { get; set; }

    /// <summary>The zero-size after-update chrome, mounted THIRD in the overlay layer (after the wizard and report chromes).</summary>
    // MOUNT POINT (WP-6.R contract)
    public static Element AfterUpdateChrome() => Embed.Comp(static () => new AfterUpdateChromeView());

    /// <summary>A dev build has no update story to summarize: Settings › About shows no "Show the update summary again".</summary>
    public static bool CanShowSummaryAgain => !Platform.Version.IsDev;

    /// <summary>Settings › About's "Show the update summary again": the after-update plate on demand. Reads the DURABLE
    /// previousVersion (the running quad on an install that never updated), loads first like the automatic path, and
    /// opens even when the notes resolve to nothing — the user asked for it, and "Full release notes" still leads out.</summary>
    public static void ShowSummaryAgain(IOverlayService overlay, Action<Action> post)
    {
        if (!CanShowSummaryAgain) return;
        string from = Platform.Settings.Get(Platform.Keys.ReleaseNotesPreviousVersion);
        _ = LoadThenOpenAsync(overlay, post, from.Length > 0 ? from : Platform.Version.Quad, automatic: false);
    }

    /// <summary>"Open What's new" carries an unread dot while the running release's notes have never been opened.</summary>
    public static bool RunningNotesUnread()
        => NotesUnread(Platform.Settings.Get(Platform.Keys.ReleaseNotesLastSeen), Platform.Version.Core);

    /// <summary>The build as a localized sentence — <c>Wavee 0.3.0 “Crest”</c> / <c>Wavee 0.3.0</c> /
    /// <c>Wavee 0.3.0 “Crest” · Beta 2</c> (a dev build names its SemVer). The plate's welcome and About's version line.</summary>
    public static string VersionDisplayText()
    {
        var me = Platform.Version;
        var d = VersionDisplay.Of(me.SemVer, me.Core, me.IsDev, me.Codename, me.Beta);
        return d.Shape switch
        {
            VersionDisplayShape.Bare => Strings.Update.About.DisplayBare(d.Version),
            VersionDisplayShape.Beta => Strings.Update.About.DisplayBeta(d.Version, d.Codename, d.Beta),
            _ => Strings.Update.About.Display(d.Version, d.Codename),
        };
    }

    static RunningBuild Running()
    {
        var me = Platform.Version;
        return new RunningBuild(me.Core, me.Channel, me.IsStore);
    }

    static string ReleaseDate(string? iso) => Links.Date(iso, Loc.Get(Strings.WhatsNew.DateFormat));

    static void OpenUrl(string url)
    {
        if (Actions.Services.OpenExternal is { } open) { open(url); return; }
        InputHooks.Current.Default.OpenUri?.Invoke(url);
    }

    static void OpenStoreListing() => OpenUrl(Update.StorePageUrl(Links.StoreIdOrFallback(Platform.Version.StoreId)));

    static readonly Action<string> IgnoreLink = static _ => { };
    static readonly Action<string> OpenLink = static url => OpenUrl(url);
    static readonly Action Inert = static () => { };

    // ══ 2. THE PAGE ══════════════════════════════════════════════════════════════════════════════════════════════════

    static Element PageFor(string versionArg) => Embed.Comp(() => new Page(versionArg)) with { Key = "whatsnew:" + versionArg };

    /// <summary>Reading the page is what marks the notes seen — and <c>lastSeen</c> is read BEFORE the load starts and
    /// carried into it (ch 28 §0.11): advancing first hands the range (me, me) and the banner + every dot die.</summary>
    sealed class Page(string versionArg) : Component
    {
        readonly Signal<bool> _onlyLatest = new(false);
        readonly Signal<ReleaseNotesView?> _view = new(null);
        readonly Signal<bool> _loaded = new(false);      // "still loading" vs "nothing to show"

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var view = _view.Value;
            bool onlyLatest = _onlyLatest.Value;
            bool loaded = _loaded.Value;

            UseEffect(() =>
            {
                string lastSeen = Platform.Settings.Get(Platform.Keys.ReleaseNotesLastSeen);   // BEFORE the load
                var cts = new CancellationTokenSource();
                _ = LoadAsync(lastSeen, post, cts.Token);
                Platform.Settings.Set(Platform.Keys.ReleaseNotesLastSeen, Platform.Version.Core);   // seen on OPEN
                return (Action?)(() => cts.Cancel());
            }, DepKey.Empty);

            if (view is null) return Frame(loaded ? EmptyState() : LoadingState(), rail: null);

            void Copy(string text)
            {
                hooks.Clipboard?.SetText(text);
                Notify.Say(Loc.Get(Strings.WhatsNew.LinkCopied), InfoBarSeverity.Success);
            }

            var releases = view.Releases;
            bool stacked = releases.Length > 1 && !onlyLatest;
            var shown = stacked ? releases : releases[..1];
            var merged = view.MergedHighlights;

            var main = new List<Element>(8)
            {
                Hero(shown[0].Doc, IsLatest(view.Index, shown[0].Doc.Version), Copy),
                Strip(merged, i => OpenViewer(overlay, merged, i, closeHost: null)),
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
                        main.Add(SectionView.Create(section, entry.IssueStates, "sec:" + entry.Doc.Version + ":" + section.Kind));
            }
            if (shown[0].Doc.GeneratedAt is { Length: > 0 } generated)
                main.Add(new TextEl(Strings.WhatsNew.AsOf(ReleaseDate(generated[..Math.Min(10, generated.Length)])))
                    { Size = 11.5f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(0f, 6f, 0f, 0f) });
            main.Add(new BoxEl { Height = 24f, HitTestVisible = false });

            var column = new List<Element>(2);
            if (stacked) column.Add(SinceBanner(releases));   // ABOVE the scroller, never inside it
            column.Add(ScrollView(new BoxEl
            {
                Direction = 1, Gap = Spacing.L, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(0f, 0f, 6f, 0f),
                Children = main.ToArray(),
            }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:" + view.SelectedVersion });

            return Frame(
                new BoxEl { Direction = 1, Gap = Spacing.M, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = column.ToArray() },
                ReleaseRail(view.Index, view.SelectedVersion, Platform.Version.Core, view.LastSeen));
        }

        /// <summary>One whole view or nothing, then the budgeted issue-state republish over the newest document.</summary>
        async Task LoadAsync(string lastSeen, Action<Action> post, CancellationToken ct)
        {
            try
            {
                var store = Notes;
                var me = Running();
                var view = await Task.Run(() => LoadViewAsync(store, versionArg, lastSeen, me, ct), ct).ConfigureAwait(false);
                post(() => { if (ct.IsCancellationRequested) return; _view.Value = view; _loaded.Value = true; });
                if (view is null) return;
                var enriched = await WithIssueStatesAsync(store, view, ct).ConfigureAwait(false);
                post(() => { if (!ct.IsCancellationRequested) _view.Value = enriched; });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn("whatsnew", "page load failed", ex);
                post(() => { if (!ct.IsCancellationRequested) _loaded.Value = true; });
            }
        }

        Element SinceBanner(ReleaseEntry[] releases) => new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = new Edges4(12f, 8f, 12f, 8f), Corners = CornerRadius4.All(6f),
            Fill = Tok.AccentSubtle, BorderWidth = 1f, BorderColor = Tok.AccentDefault,
            Children =
            [
                Icon(Icons.RefineSparkle, 14f, Tok.AccentTextPrimary),
                new TextEl(Strings.WhatsNew.Since(releases.Length, releases[^1].Doc.Version, releases[0].Doc.Version))
                    { Size = 12.5f, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
                Button.Create(Loc.Get(Strings.WhatsNew.OnlyLatest), () => _onlyLatest.Value = true,
                    ButtonAppearance.Subtle, ControlSize.Small) with { Shrink = 0f },
            ],
        };
    }

    static Element Frame(Element body, Element? rail) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.M),
                Children = [Icon(Icons.Tag, 22f, Tok.TextPrimary), Design.Type.PageHero(Loc.Get(Strings.WhatsNew.Title)) with { Grow = 1f }],
            },
            new BoxEl
            {
                Direction = 0, Gap = 14f, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Padding = new Edges4(Spacing.PageWide, 0f, Spacing.PageWide, 0f),
                Children = rail is null ? [body] : [body, rail],   // loading / empty: the 208 column AND the 14 gap are gone
            },
        ],
    };

    static Element LoadingState() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [ProgressRing.Create(size: 28f)],
    };

    static Element EmptyState() => new BoxEl
    {
        Grow = 1f, Direction = 1, Gap = Spacing.M, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            Icon(Icons.Tag, 32f, Tok.TextTertiary),
            new TextEl(Loc.Get(Strings.WhatsNew.Empty)) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 360f },
            Button.Standard(Loc.Get(Strings.WhatsNew.OpenOnGitHub), static () => OpenUrl(Links.ReleasesUrl)),
        ],
    };

    static Element ReleaseDivider(ReleaseNotesDocument doc) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
        Margin = new Edges4(0f, Spacing.S, 0f, 0f),
        Children =
        [
            new TextEl(DividerLabel(doc.Version, doc.Name, ReleaseDate(doc.Date))) { Size = 12f, Weight = 600, Color = Tok.TextTertiary, Shrink = 0f },
            new BoxEl { Height = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    // ── the hero ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What this release IS, when it landed, and the two links out. Document data only — it renders identically
    /// for the running build, an older release the rail selected and a beta the user is only reading about.</summary>
    static Element Hero(ReleaseNotesDocument doc, bool isLatest, Action<string> copy)
    {
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
                        new TextEl(headline) { Size = 30f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new TextEl(doc.Tagline) { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 640f },
                        new BoxEl { Direction = 0, Gap = 14f, Wrap = true, Margin = new Edges4(0f, 4f, 0f, 0f), Children = meta.ToArray() },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.End,
                    Children =
                    [
                        Button.Create(Loc.Get(Strings.WhatsNew.OpenOnGitHub), () => OpenUrl(release), ButtonAppearance.Standard, ControlSize.Small),
                        Button.Create(Loc.Get(Strings.WhatsNew.CopyLink), () => copy(release), ButtonAppearance.Subtle, ControlSize.Small),
                    ],
                },
            ],
        };
    }

    /// <summary>The hero / plate / About pill: MinHeight 22, 11.5/600, accent or subtle, optionally Cascadia Code.</summary>
    public static Element Pill(string text, bool accent = false, bool mono = false) => new BoxEl
    {
        Shrink = 0f, MinHeight = 22f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(9f, 2f, 9f, 2f), Corners = CornerRadius4.All(Radii.Pill),
        Fill = accent ? Tok.AccentDefault : Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(text)
            {
                Size = 11.5f, Weight = 600, MaxLines = 1,
                Color = accent ? Tok.TextOnAccentPrimary : Tok.TextSecondary,
                FontFamily = mono ? "Cascadia Code" : null,
            },
        ],
    };

    static Element MetaItem(string label, string value) => new BoxEl
    {
        Direction = 0, Gap = 5f, Shrink = 0f, AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
            new TextEl(value) { Size = 12f, Color = Tok.TextTertiary },
        ],
    };

    // ══ 3. THE HIGHLIGHT STRIP AND CARD ══════════════════════════════════════════════════════════════════════════════

    /// <summary>Three is a cap, not a layout. No highlights ⇒ a 0-height, hit-invisible box: the eyebrow AND the row vanish.</summary>
    static Element Strip(IReadOnlyList<HighlightItem> highlights, Action<int> open)
    {
        if (highlights.Count == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
        int n = Math.Min(highlights.Count, HighlightMax);
        var cards = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;   // the row's own index, not the closed-over loop variable
            cards[i] = CardFor(highlights[i], () => open(idx), compact: false) with { Key = "hl:" + SlideId(highlights[i], i) };
        }
        return new BoxEl
        {
            Direction = 1, Gap = 6f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                Design.Type.Eyebrow(Loc.Get(Strings.WhatsNew.Highlights)) with { Color = Tok.TextTertiary },
                new BoxEl { Direction = 0, Gap = 10f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = cards },
            ],
        };
    }

    static Element CardFor(HighlightItem item, Action open, bool compact) => Embed.Comp(() => new CardView(item, open, compact));

    const float PosterDecodePx = 1200f;

    /// <summary>The poster band: 16:9 from the card's own width, and a band EVEN WITHOUT a poster (a tinted plate) — a card
    /// that loses its top third reads as broken. DecodePx 1200 is the authored width; without it the poster is a smear.</summary>
    static Element Media(ReleaseHighlight h, string? poster, bool store)
    {
        var layers = new List<Element>(2);
        if (poster is { Length: > 0 })
            layers.Add(new ImageEl
            {
                Source = poster, Fit = ImageFit.Cover, AspectRatio = M.PosterAspect, DecodePx = PosterDecodePx,
                Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
                Placeholder = Tok.FillSubtleSecondary, AlignSelf = FlexAlign.Stretch,
            });
        layers.Add(new BoxEl
        {
            Grow = 1f, Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(8f), HitTestVisible = false,
            Children = [KindPill(h.Kind, store), Spacer(), PlayGlyph(h)],
        });
        // The ratio sits on a PLAIN column box and the layers on a ZStack inside it (G-256): a ZStack's measure ignores
        // AspectRatio and reports its tallest layer, so without a poster the band collapsed to the pill row (~36 DIP).
        return new BoxEl
        {
            AspectRatio = M.PosterAspect, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Direction = 1, ClipToBounds = true,
            Fill = store ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            Children =
            [
                new BoxEl { ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = layers.ToArray() },
            ],
        };
    }

    // Issue #89 (L3): FillSolidTertiary is a deliberately LIGHTER opaque rung than the plate so the pill is never read as
    // "the plate behind it" where the poster does not paint, plus a 0/1/4 #00000066 shadow; an explicit Height + Radii.Full
    // (clamped to H/2) because Radii.Pill (16) exceeded half this pill's height.
    static Element KindPill(string? kind, bool store) => new BoxEl
    {
        Shrink = 0f, Height = 20f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(8f, 0f, 8f, 0f), Corners = CornerRadius4.All(Radii.Full),
        Fill = store ? Tok.AccentDefault : Tok.FillSolidTertiary,
        Shadow = new ShadowSpec(Blur: 4f, OffsetY: 1f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x66)),
        Children = [new TextEl(Loc.Get(KindPillLocKey(kind, store))) { Size = 11f, Weight = 600, Color = store ? Tok.TextOnAccentPrimary : Tok.TextPrimary }],
    };

    static bool IsVideo(ReleaseHighlight h) => h.Media is { } m && string.Equals(m.Kind, "video", StringComparison.OrdinalIgnoreCase);

    static Element PlayGlyph(ReleaseHighlight h) => !IsVideo(h)
        ? new BoxEl { Width = 0f, HitTestVisible = false }
        : new BoxEl
        {
            Width = 32f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(16f), Fill = Tok.MediaScrim,
            Children = [Icon(Icons.Play, 14f, Tok.OnMediaPrimary)],
        };

    /// <summary>One card. A component only for its <c>overflows</c> signal; the ctor args are legitimately frozen
    /// (<see cref="HighlightItem"/> is immutable, <c>open</c> captures its index). The MOTION is a poster, never live video.</summary>
    sealed class CardView(HighlightItem item, Action open, bool compact) : Component
    {
        readonly Signal<bool> _overflows = new(false);

        public override Element Render()
        {
            var h = item.Highlight;
            bool store = HighlightVisibility.IsStore(h);
            bool overflows = _overflows.Value;   // the slot's edge fade engages when OnBoundsChanged flips the ANSWER

            // The standard translucent Mica card (FillCardDefault, flat StrokeCardDefault) — possible only because the
            // "there is more" cue is a true alpha EdgeFade, not a painted gradient that needs an opaque far stop.
            var frame = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                MaxWidth = compact ? M.CompactCardMaxW : M.CardMaxW,
                Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                Fill = Tok.FillCardDefault, BorderWidth = 1f,
                BorderColor = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
                HoverBorderColor = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
                BrushTransitionMs = Design.Motion.Faster,
            };

            if (!store)   // frame and hit region are ONE node: the whole Interaction.Card (hover ramp, flat stroke, 0.985 press)
                return frame.Interactive(Interaction.Card) with
                {
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children = [Media(h, item.Poster, store: false), Body(h, overflows, store: false)],
                };

            // Store card: the hit region and the footer are SIBLINGS — a card never nests a button in a button — and the
            // frame takes no hover ramp at all (ch 28 W26); the press spring is authored on the hit region itself.
            return frame with
            {
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                        WhilePressed = new MotionTarget { Scale = 0.985f }, Transition = MotionTok.StandardSpring,
                        Children = [Media(h, item.Poster, store: true), Body(h, overflows, store: true)],
                    },
                    new BoxEl
                    {
                        Direction = 0, Padding = new Edges4(M.PadL, M.StoreButtonGap, M.PadR, M.PadB),
                        Children = [Button.Accent(Loc.Get(Strings.WhatsNew.StoreCta), OpenStoreListing)],
                    },
                ],
            };
        }

        Element Body(ReleaseHighlight h, bool overflows, bool store) => new BoxEl
        {
            Direction = 1, Gap = M.TitleBodyGap, MinWidth = 0f,
            Padding = new Edges4(M.PadL, M.PadT, M.PadR, store ? M.HitRegionStoreBottomPad : M.PadB),
            Children = store
                ? [CardTitle(h), BodySlot(h, overflows)]                   // no "Read more": the accent button says it
                : [CardTitle(h), BodySlot(h, overflows), ReadMoreRow()],
        };

        static Element CardTitle(ReleaseHighlight h) => new TextEl(h.Title)
        {
            Size = M.TitleSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
            LineHeight = M.TitleLineHeight, MaxLines = M.TitleMaxLines, Trim = TextTrim.CharacterEllipsis,
        };

        /// <summary>The fixed 68-DIP slot. The paragraph is UNCLAMPED (MaxLines 0) inside a natural-height wrapper whose
        /// bounds decide the fade — and that wrapper is a Shrink=0 child of a FLEX COLUMN, not a bare ZStack layer:
        /// ArrangeZStack clamps an auto-sized layer to the stack, so no child of a 68-DIP ZStack could ever report "more".
        /// Links inside a card are inert (the card is one button); they are live in the viewer.</summary>
        Element BodySlot(ReleaseHighlight h, bool overflows) => new BoxEl
        {
            Height = M.BodySlotHeight, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            ZStack = true, ClipToBounds = true, HitTestVisible = false,
            EdgeFade = overflows ? new EdgeFadeSpec(EdgeMask.Bottom, M.FadeHeight) : null,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Shrink = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                            OnBoundsChanged = r =>
                            {
                                bool v = M.Overflows(r.H);
                                if (_overflows.Peek() != v) _overflows.Value = v;   // only when the ANSWER changes
                            },
                            Children =
                            [
                                RichTextBlock.Paragraph(ToSpans(MarkdownLite.Tokenize(h.Body), IgnoreLink), isTextSelectionEnabled: false)
                                    with { Size = M.BodySize, LineHeight = M.BodyLineHeight, MaxLines = 0, Wrap = TextWrap.Wrap, Color = Tok.TextSecondary },
                            ],
                        },
                    ],
                },
            ],
        };

        /// <summary>On every non-store card, overflowing or not: a short card gives no other cue that a click opens anything.
        /// Its ink eases with the nearest interactive ancestor's hover AND focus.</summary>
        static Element ReadMoreRow() => new BoxEl
        {
            Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Height = M.LabelHeight, HitTestVisible = false,
            Children =
            [
                new TextEl(Loc.Get(Strings.WhatsNew.ReadMore))
                {
                    Size = 12f, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1,
                    HoverColor = Tok.AccentTextPrimary, FocusedColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster,
                },
                Icon(Icons.ChevronRight, 10f, Tok.TextSecondary) with
                    { HoverColor = Tok.AccentTextPrimary, FocusedColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster },
            ],
        };
    }

    // ══ 4. THE CHANGELOG: SECTIONS, ROWS, CHIPS, AVATARS, SPANS ═══════════════════════════════════════════════════════

    /// <summary>One <c>### Added</c> group. A component for its LOCAL "Show all N" state, and its props are RE-PUSHED:
    /// the issue states land after the document, so a frozen section would show the tool's snapshot forever.</summary>
    sealed class SectionView : Component
    {
        /// <summary>Gates on the section and the states by REFERENCE: the loader replaces the states cache wholesale, so the
        /// issue-state republish re-renders the sections and a page re-render with the same document re-renders none.</summary>
        public sealed record Props(ReleaseSection Section, IssueStateCache? States)
        {
            public bool Equals(Props? other) => other is not null && ReferenceEquals(Section, other.Section) && ReferenceEquals(States, other.States);
            public override int GetHashCode() => HashCode.Combine(Section, States);
        }

        readonly Signal<bool> _all = new(false);

        /// <summary><paramref name="key"/> is unique across the page (release + kind), or two stacked releases share one fold.</summary>
        public static Element Create(ReleaseSection section, IssueStateCache? states, string key)
            => Embed.Comp(new Props(section, states), static () => new SectionView()) with { Key = key };

        public override Element Render()
        {
            var p = UseProps<Props>();
            var items = p.Section.Items;
            bool all = _all.Value;
            int shown = ShownRows(items.Length, all);

            var rows = new List<Element>(shown * 2);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, AlignSelf = FlexAlign.Stretch });
                rows.Add(ChangelogRow(items[i], p.States));
            }

            var (glyph, ink, wash) = Badge(p.Section.Kind);
            var header = new List<Element>(4)
            {
                new BoxEl
                {
                    Width = 22f, Height = 22f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(6f), Fill = wash,
                    Children = [new TextEl(glyph) { Size = 12f, Weight = 700, Color = ink }],
                },
                new TextEl(Loc.Get(SectionTitleLocKey(p.Section.Kind))) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                new TextEl(items.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Size = 12f, Color = Tok.TextTertiary, Grow = 1f },
            };
            if (shown < items.Length)   // expands in place and never folds back while the page is open
                header.Add(Button.Create(Strings.WhatsNew.ShowAll(items.Length), () => _all.Value = true, ButtonAppearance.Subtle, ControlSize.Small) with { Shrink = 0f });

            return new BoxEl
            {
                Direction = 1, Gap = 6f, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinHeight = 28f, Children = header.ToArray() },
                    new BoxEl
                    {
                        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                        Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                        Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                        Children = rows.ToArray(),
                    },
                ],
            };
        }

        /// <summary>Plain characters, not the icon font: "+ ~ ✓ − !" is what a changelog reads as (ch 28 §4.4).</summary>
        static (string Glyph, ColorF Ink, ColorF Wash) Badge(string? kind) => kind switch
        {
            "added" => ("+", Tok.SystemFillSuccess, Tok.SystemFillSuccessBackground),
            "changed" => ("~", Tok.AccentTextPrimary, Tok.AccentSubtle),
            "fixed" => ("✓", Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
            "removed" => ("−", Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
            "deprecated" => ("!", Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
            "security" => ("!", Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
            _ => ("!", Tok.TextTertiary, Tok.FillSubtleSecondary),
        };
    }

    /// <summary>One bullet: an optional UPPERCASED scope eyebrow run + the sentence as ONE selectable span paragraph, then
    /// the reference chips on their own wrapping row (a span run cannot carry a box) and the contributor stack.</summary>
    static Element ChangelogRow(ReleaseItem item, IssueStateCache? states)
    {
        var tokens = MarkdownLite.Tokenize(item.Text);
        var spans = new List<TextSpan>(tokens.Length + 1);
        if (item.Scope is { Length: > 0 } scope)
            spans.Add(new TextSpan(scope.ToUpperInvariant() + "   ", Weight: 700, Color: Tok.TextTertiary, Size: 11f));
        spans.AddRange(ToSpans(tokens, OpenLink));

        var refs = new List<Element>(item.Issues.Length + item.Prs.Length);
        foreach (var i in item.Issues) refs.Add(IssueChip(i, states?.Lookup(i)));
        foreach (var pr in item.Prs) refs.Add(PrChip(pr));

        var column = new List<Element>(2)
        {
            RichTextBlock.Paragraph(spans.ToArray()) with { Size = 13f, Grow = 1f, MinWidth = 0f, MaxWidth = float.NaN, Wrap = TextWrap.Wrap },
        };
        if (refs.Count > 0)
            column.Add(new BoxEl { Direction = 0, Gap = 6f, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = refs.ToArray() });

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start, Padding = new Edges4(Spacing.M, 9f, Spacing.M, 9f),
            Children =
            [
                new BoxEl { Direction = 1, Gap = 6f, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = column.ToArray() },
                AvatarStack(item.Contributors),
            ],
        };
    }

    /// <summary>"● #412 closed": the LIVE state when fetched, the release tool's snapshot otherwise — never nothing.</summary>
    static Element IssueChip(ReleaseIssue issue, IssueState? live)
    {
        var state = IssueChipState(live, issue);
        ColorF dot = state switch { ChipState.Open => Tok.SystemFillSuccess, ChipState.NotPlanned => Tok.TextTertiary, _ => Tok.AccentDefault };
        string word = Loc.Get(state switch
        {
            ChipState.Open => Strings.WhatsNew.Issue.Open,
            ChipState.NotPlanned => Strings.WhatsNew.Issue.NotPlanned,
            _ => Strings.WhatsNew.Issue.Closed,
        });
        return Chip("#" + issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), word, dot,
            Links.IssueUrl(issue.Repo, issue.Number, issue.IsPullRequest), IssueChipTip(live, issue));
    }

    /// <summary>A pull request is merged or it is not — there is no "not planned".</summary>
    static Element PrChip(ReleasePr pr) => Chip("!" + pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Loc.Get(pr.Merged ? Strings.WhatsNew.Issue.Merged : Strings.WhatsNew.Issue.Open),
        pr.Merged ? Tok.AccentDefault : Tok.SystemFillSuccess, Links.IssueUrl(pr.Repo, pr.Number, pr: true), pr.Title);

    const float ChipHeight = 18f;

    static Element Chip(string number, string word, ColorF dot, string url, string? tooltip)
    {
        var chip = new BoxEl
        {
            Shrink = 0f, Direction = 0, Gap = 4f, Height = ChipHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(ChipHeight / 2f),
            Role = AutomationRole.Hyperlink, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => OpenUrl(url),
            Children =
            [
                new BoxEl { Width = 7f, Height = 7f, Shrink = 0f, Corners = CornerRadius4.All(3.5f), Fill = dot },
                new TextEl(number) { Size = 11f, Weight = 600, Color = Tok.TextPrimary, FontFamily = "Cascadia Code", MaxLines = 1 },
                new TextEl(word) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        }.Interactive(Interaction.Control);
        return tooltip is { Length: > 0 } t ? ToolTip.Wrap(chip, t) : chip;   // no title anywhere ⇒ no tooltip at all
    }

    // A fixed, deterministic palette (ch 28 §4.3): the same login gets the same tint on every page and every build.
    static readonly ColorF[] s_avatarTints =
    [
        ColorF.FromRgba(0x4A, 0x7A, 0xC0, 0xFF), ColorF.FromRgba(0xC0, 0x7A, 0x4A, 0xFF), ColorF.FromRgba(0x4A, 0xA8, 0x7A, 0xFF),
        ColorF.FromRgba(0x8A, 0x6A, 0xC0, 0xFF), ColorF.FromRgba(0xC0, 0x5A, 0x7A, 0xFF), ColorF.FromRgba(0x5A, 0x8A, 0x8A, 0xFF),
    ];

    const float AvatarSize = 18f;
    const int AvatarMax = 4;

    /// <summary>INITIALS, not avatar images (no third-party image request per contributor). Empty ⇒ zero-size, hit-invisible.</summary>
    static Element AvatarStack(ReleaseContributor[] contributors)
    {
        if (contributors.Length == 0) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        int shown = Math.Min(contributors.Length, AvatarMax);
        var kids = new List<Element>(shown + 1);
        for (int i = 0; i < shown; i++)
        {
            string login = contributors[i].Login;
            kids.Add(ToolTip.Wrap(new BoxEl
            {
                Width = AvatarSize, Height = AvatarSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(AvatarSize / 2f), Fill = s_avatarTints[AvatarTintIndex(login)],
                BorderWidth = 1.5f, BorderColor = Tok.FillSolidBase,             // cuts the disc out of the one behind it
                Margin = i == 0 ? default : new Edges4(-5f, 0f, 0f, 0f),
                Children = [new TextEl(Initials(login)) { Size = 9f, Weight = 700, Color = Tok.TextOnAccentPrimary }],
            }, contributors[i].FirstTime ? Strings.WhatsNew.FirstContribution(login) : login));
        }
        if (contributors.Length > shown)
            kids.Add(new TextEl("+" + (contributors.Length - shown).ToString(System.Globalization.CultureInfo.InvariantCulture))
                { Size = 10f, Weight = 600, Color = Tok.TextTertiary, Margin = new Edges4(4f, 0f, 0f, 0f) });
        return new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }

    /// <summary>The one bridge from the pure tokenizer to span runs: a sentence is ONE paragraph whose bold / code / link
    /// runs are real spans. <paramref name="openUrl"/> decides what a link does (the browser, or nothing inside a card).</summary>
    static TextSpan[] ToSpans(InlineToken[] tokens, Action<string> openUrl)
    {
        if (tokens.Length == 0) return [];
        var spans = new TextSpan[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            spans[i] = t.Kind switch
            {
                InlineKind.Bold => RichTextBlock.Bold(t.Text),
                // Monospace one step down (the WinUI inline-code convention); no plate — a span carries no box.
                InlineKind.Code => new TextSpan(t.Text, Weight: t.Bold ? (ushort)600 : (ushort)0, Size: 12f, FontFamily: "Cascadia Code", Color: Tok.TextSecondary),
                InlineKind.Link or InlineKind.Url => SpanLink(t.Text, t.Target ?? t.Text, openUrl),
                InlineKind.Issue => SpanLink(t.Text, Links.IssueUrl(t.Repo, t.Number, pr: false), openUrl),
                InlineKind.Pr => SpanLink(t.Text, Links.IssueUrl(t.Repo, t.Number, pr: true), openUrl),
                InlineKind.Mention => SpanLink(t.Text, "https://github.com/" + (t.Target ?? t.Text.TrimStart('@')), openUrl),
                _ => RichTextBlock.Run(t.Text),
            };
        }
        return spans;
    }

    static TextSpan SpanLink(string text, string url, Action<string> open) => RichTextBlock.Hyperlink(text, () => open(url));

    // ══ 5. THE RELEASE RAIL ══════════════════════════════════════════════════════════════════════════════════════════

    const float RailWidth = 208f;

    /// <summary>Every release the index knows, newest first. YOU is the RUNNING build; the dot is "not looked at yet"
    /// (newer than the lastSeen the page read before advancing). No index ⇒ a zero-width, hit-invisible box.</summary>
    static Element ReleaseRail(ReleaseNotesIndex? index, string selected, string running, string lastSeen)
    {
        IReadOnlyList<ReleaseNotesIndexEntry> rows = RailRows(index);   // snapshotted ONCE: the For thunk stays cheap
        if (rows.Count == 0) return new BoxEl { Width = 0f, HitTestVisible = false };
        return new BoxEl
        {
            Direction = 1, Gap = 6f, Width = RailWidth, Shrink = 0f, MinHeight = 0f,
            Children =
            [
                Design.Type.Eyebrow(Loc.Get(Strings.WhatsNew.Releases)) with { Color = Tok.TextTertiary, Margin = new Edges4(4f, 6f, 4f, 2f) },
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = 2f, MinWidth = 0f,
                    Children = [Flow.For(() => rows, static e => e.Version, e => RailRow(e, selected, running, lastSeen))],
                }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:rail" },
                new TextEl(Loc.Get(Strings.WhatsNew.RailFoot)) { Size = 11.5f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(4f, 6f, 4f, 6f) },
            ],
        };
    }

    static Element RailRow(ReleaseNotesIndexEntry e, string selected, string running, string lastSeen)
    {
        bool isSelected = string.Equals(e.Version, selected, StringComparison.Ordinal);
        var title = new List<Element>(3)
        {
            new TextEl(e.Version) { Size = 12.5f, Weight = 600, Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1 },
        };
        switch (RailMarkerFor(e.Version, selected, running, lastSeen))
        {
            case RailMarker.You: title.Add(RailPill(Loc.Get(Strings.WhatsNew.You), Tok.AccentDefault, Tok.TextOnAccentPrimary)); break;
            case RailMarker.Unread: title.Add(new BoxEl { Width = 6f, Height = 6f, Shrink = 0f, Corners = CornerRadius4.All(3f), Fill = Tok.AccentDefault }); break;
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
            OnClick = () => Shell.GoTo(Shell.Parse("whatsnew", version)),   // a NEW keep-alive slot, keyed by the arg
            Children =
            [
                new BoxEl
                {
                    Width = 9f, Height = 9f, Shrink = 0f, Corners = CornerRadius4.All(4.5f), Margin = new Edges4(2f, 4f, 0f, 0f),
                    BorderWidth = 1.5f, BorderColor = isSelected ? Tok.AccentDefault : Tok.TextTertiary,
                    Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,
                },
                new BoxEl
                {
                    Direction = 1, Gap = 1f, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = true, Children = title.ToArray() },
                        new TextEl(RailSubtitle(e.Name, ReleaseDate(e.Date))) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        }.Interactive(isSelected ? Interaction.ListRow : Interaction.Subtle);
    }

    static Element RailPill(string text, ColorF fill, ColorF ink) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(5f, 1f, 5f, 1f), Corners = CornerRadius4.All(6f), Fill = fill,
        Children = [new TextEl(text) { Size = 9.5f, Weight = 700, Color = ink }],
    };

    // ══ 6. THE HIGHLIGHT VIEWER ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The modal one-highlight-at-a-time viewer. Anchor-less, Modal chrome for the ContentDialog scale/fade and the
    /// focus trap, but ScrimVisual = false (the view paints its OWN veil so it never stacks a second smoke over the
    /// after-update plate) and LightDismiss (a veil click is how a viewer closes). <paramref name="closeHost"/> is the
    /// after-update plate's close, or null from the page.</summary>
    static OverlayHandle OpenViewer(IOverlayService overlay, IReadOnlyList<HighlightItem> items, int initial, Action? closeHost)
    {
        OverlayHandle? handle = null;
        handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new ViewerView(items, initial, closeHost, () => handle)),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Modal) { ScrimVisual = false });
        return handle;
    }

    sealed class ViewerView : Component
    {
        // Deliberate literals (ch 28 §4.6): rgba(0,0,0,.72) — Tok.MediaScrim (.55) reads thin stacked over the plate's own
        // smoke — the chrome's hover / dimmed MediaScrim @ .70 / .40, and the pips' white 82 / 140.
        static readonly ColorF Veil = ColorF.FromRgba(0, 0, 0, 184);
        static readonly ColorF ChromeDimmed = Tok.MediaScrim with { A = 0.40f };
        static readonly ColorF ChromeHover = Tok.MediaScrim with { A = 0.70f };
        static readonly ColorF PipOff = ColorF.FromRgba(255, 255, 255, 82), PipOffHover = ColorF.FromRgba(255, 255, 255, 140);

        const float PipDot = 6f, PipSelected = 14f, PipGap = 8f, PipMs = 120f;
        const float PagerRowHeight = 24f, PagerRowTop = 8f;   // dot centres 17 DIP from the band and from the title

        readonly IReadOnlyList<HighlightItem> _items;
        readonly Action? _closeHost;
        readonly Func<OverlayHandle?> _handle;
        readonly Signal<int> _index;
        readonly Signal<int> _pip = new(0);   // the pager's mirror of _index — written from an EFFECT, never during render
        // Motion-only: which way the NEXT slide travels. A plain FIELD — a motion-only value must never trigger a render of
        // its own; the render `_index.Value = …` triggers on the next line reads it (ch 28 §9.1 #3).
        HighlightSlideDirection _dir = HighlightSlideDirection.None;

        public ViewerView(IReadOnlyList<HighlightItem> items, int initial, Action? closeHost, Func<OverlayHandle?> handle)
        {
            _items = items;
            _closeHost = closeHost;
            _handle = handle;
            _index = new(Math.Clamp(initial, 0, Math.Max(0, items.Count - 1)));
        }

        public override Element Render()
        {
            var vp = UseContext(Viewport.Size);
            int count = _items.Count;
            int index = count == 0 ? 0 : Math.Clamp(_index.Value, 0, count - 1);
            UseEffect(() => { if (_pip.Peek() != index) _pip.Value = index; }, index);
            if (count == 0) return new BoxEl();

            var item = _items[index];
            var h = item.Highlight;
            bool store = HighlightVisibility.IsStore(h);
            string id = SlideId(item, index);
            float w = VL.PlateWidth(vp.Width, vp.Height);
            float imgH = VL.ImageHeight(w, item.Poster is { Length: > 0 });   // W·9/16 ALWAYS, poster or not (issue #89 L4)
            bool hasPager = count >= 2;

            var plateKids = new List<Element>(3) { Band(item, h, id, imgH, index, count, store, IsVideo(h)) };
            if (hasPager) plateKids.Add(PagerRow(count, index));
            plateKids.Add(TextStack(h, id, store, hasPager));

            return new BoxEl
            {
                // Explicit viewport size: the modal host hugs, so without it the veil would cover only the plate (#89 L1).
                Width = vp.Width, Height = vp.Height, Grow = 1f, ZStack = true,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Focusable = true, OnKeyDown = OnKeys,
                Children =
                [
                    // THE light-dismiss surface (the plate paints over it in z-order). Hover/pressed pinned to the resting
                    // fill: a dismiss surface must not tint under the cursor or flash on press like a giant button.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        Fill = Veil, HoverFill = Veil, PressedFill = Veil, OnClick = Close,
                    },
                    new BoxEl
                    {
                        Direction = 1, Width = w, MaxHeight = VL.PlateMaxHeight(vp.Height),
                        Fill = Tok.FillSolidBase, Corners = CornerRadius4.All(Radii.Overlay),
                        BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault, ClipToBounds = true,
                        Shadow = new ShadowSpec(Blur: 90f, OffsetY: 40f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x99)),
                        Children = plateKids.ToArray(),
                    },
                ],
            };
        }

        void Go(HighlightStep step)
        {
            if (step.Direction == HighlightSlideDirection.None) return;   // an end, or one item: nothing moves or re-renders
            _dir = step.Direction;
            _index.Value = step.Index;
        }

        /// <summary>Left/Right step (clamped, no wrap), Home/End jump, Escape is the overlay's. Every recognised key is
        /// HANDLED even as a no-op (a one-highlight viewer never leaks arrows to the page); an already-handled key came
        /// from a focused pip and is left alone.</summary>
        void OnKeys(KeyEventArgs e)
        {
            if (e.Handled) return;
            HighlightNavKey key;
            switch (e.KeyCode)
            {
                case Keys.Left: key = HighlightNavKey.Previous; break;
                case Keys.Right: key = HighlightNavKey.Next; break;
                case Keys.Home: key = HighlightNavKey.First; break;
                case Keys.End: key = HighlightNavKey.Last; break;
                default: return;
            }
            e.Handled = true;
            Go(VL.Step(_index.Peek(), _items.Count, key));
        }

        void Close() => _handle()?.Close();

        /// <summary>Navigate FIRST, then the host plate, then this viewer — so the shell has navigated before either plate
        /// starts its exit and focus lands somewhere (ch 28 §6.3).</summary>
        void TryIt(Shell.Route route)
        {
            Shell.GoTo(route);
            _closeHost?.Invoke();
            Close();
        }

        /// <summary>A KEYED poster layer under two STABLE chrome rows. The no-poster tint lives on this stable band, never on
        /// the keyed layer — there the outgoing and incoming translucent fills stacked and flashed ~8 levels brighter.</summary>
        Element Band(HighlightItem item, ReleaseHighlight h, string id, float imgH, int index, int count, bool store, bool video) => new BoxEl
        {
            Height = imgH, ZStack = true, ClipToBounds = true,
            Fill = store ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            BrushTransitionMs = Design.Motion.Standard,
            Children =
            [
                new BoxEl
                {
                    Key = "hv:" + id + ":img", Animate = HighlightViewerMotion.For(_dir),
                    ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Children = item.Poster is { Length: > 0 } poster
                        ? [new ImageEl
                          {
                              Source = poster, Fit = ImageFit.Cover, AspectRatio = VL.PosterAspect, DecodePx = PosterDecodePx,
                              Corners = new CornerRadius4(Radii.Overlay, Radii.Overlay, 0f, 0f), Placeholder = Tok.FillSubtleSecondary,
                              RevealTransition = ImageTransition.Fade(140f), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                          }]
                        : [],
                },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch,
                    Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Start,
                    Children =
                    [
                        new BoxEl { Margin = new Edges4(8f, 8f, 0f, 0f), Children = [KindPill(h.Kind, store)] },
                        ToolTip.Wrap(Circle(Icons.ChromeClose, 12f, enabled: true, Close, "hv:close") with { Margin = new Edges4(0f, 12f, 12f, 0f) },
                            Loc.Get(Strings.WhatsNew.Viewer.Close)),
                    ],
                },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Stretch,
                    Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(VL.ChromeInset, 0f, VL.ChromeInset, 0f),
                    Children = video
                        ? [Prev(index, count), WatchPill(item.Doc), Next(index, count)]
                        : [Prev(index, count), Next(index, count)],
                },
            ],
        };

        // The glyph-only circles' tooltip is the ONLY name each carries. A dimmed end button is not hit-testable, so it
        // simply never raises one.
        Element Prev(int index, int count) => ToolTip.Wrap(
            Circle(Icons.ChevronLeft, 16f, index > 0, () => Go(VL.Step(_index.Peek(), count, HighlightNavKey.Previous)), "hv:prev"),
            Loc.Get(Strings.WhatsNew.Viewer.Previous));

        Element Next(int index, int count) => ToolTip.Wrap(
            Circle(Icons.ChevronRight, 16f, index < count - 1, () => Go(VL.Step(_index.Peek(), count, HighlightNavKey.Next)), "hv:next"),
            Loc.Get(Strings.WhatsNew.Viewer.Next));

        /// <summary>The SAME node on every slide (a stable caller key): at an end it DIMS IN PLACE over an 83-ms ease and
        /// leaves the tab order, but never the layout — removing it read as the chrome jumping.</summary>
        static BoxEl Circle(string glyph, float glyphSize, bool enabled, Action onClick, string key) => new BoxEl
        {
            Key = key,
            Width = VL.ChromeCircle, Height = VL.ChromeCircle, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(VL.ChromeCircle / 2f),
            Fill = enabled ? Tok.MediaScrim : ChromeDimmed,
            HoverFill = enabled ? ChromeHover : default,
            BrushTransitionMs = Design.Motion.Faster,
            Opacity = enabled ? 1f : 0.3f,
            Transition = MotionTok.ControlFaster,
            HoverScale = Design.Motion.ScaleStandard.HoverIf(enabled),
            PressScale = Design.Motion.ScaleStandard.PressIf(enabled),
            HitTestVisible = enabled, Focusable = enabled, TabStop = enabled ? null : false,
            Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : default,
            OnClick = enabled ? onClick : null,
            Children = [Icon(glyph, glyphSize, Tok.OnMediaPrimary)],
        };

        /// <summary>The labelled on-media pill between the chevrons: opens the release page (where the mp4 lives) and leaves
        /// the viewer open. Its visible label IS its name. Focusable in 0.3 — 0.2.9 left it out of the trap (ch 28 §6.3).</summary>
        static Element WatchPill(ReleaseNotesDocument doc)
        {
            string release = doc.Links.Release is { Length: > 0 } r ? r : Links.ReleaseTagUrl(doc.Version);
            return new BoxEl
            {
                Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(14f, 8f, 16f, 8f), Corners = CornerRadius4.All(Radii.Pill),
                Fill = Tok.MediaScrim, HoverFill = ChromeHover, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Hyperlink, Cursor = CursorId.Hand, Focusable = true,
                HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
                OnClick = () => OpenUrl(release),
                Children =
                [
                    Icon(Icons.Play, 14f, Tok.OnMediaPrimary),
                    new TextEl(Loc.Get(Strings.WhatsNew.Viewer.Watch)) { Size = 12.5f, Weight = 600, Color = Tok.OnMediaPrimary },
                ],
            };
        }

        /// <summary>Hand-rolled, not PipsPager: 6-DIP dots and a 14-DIP selected capsule that GROWS on a Reflow width tween
        /// (Width only — a Position channel on top double-moved the pips) and brightens on the same 120-ms ramp. A full-width
        /// strip centres a hugging cluster; the tooltip wraps the cluster, so the dots never drift sideways.</summary>
        Element PagerRow(int count, int index)
        {
            var pips = new Element[count];
            for (int i = 0; i < count; i++)
            {
                int target = i;
                bool on = i == index;
                pips[i] = new BoxEl
                {
                    Key = "pip:" + target,
                    Animate = new LayoutTransition(TransitionChannels.Size, TransitionDynamics.Tween(PipMs, Easing.SmoothOut),
                        Size: SizeMode.Reflow, Axes: SizeAxes.Width),
                    Width = on ? PipSelected : PipDot, Height = PipDot, Shrink = 0f, Corners = CornerRadius4.All(Radii.Full),
                    Fill = on ? Tok.TextPrimary : PipOff, HoverFill = on ? Tok.TextPrimary : PipOffHover,
                    BrushTransitionMs = PipMs,
                    Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                    OnClick = () => Go(VL.StepTo(_index.Peek(), target, count)),
                };
            }
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, Height = PagerRowHeight, Margin = new Edges4(0f, PagerRowTop, 0f, 0f),
                Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Children =
                [
                    ToolTip.Wrap(new BoxEl { Height = PagerRowHeight, Direction = 0, AlignItems = FlexAlign.Center, Gap = PipGap, Children = pips },
                        Strings.WhatsNew.Viewer.Position(index + 1, count)),
                ],
            };
        }

        /// <summary>The text half with its box FINAL from frame one: a ZStack of one invisible, inert SIZER per highlight plus
        /// the live keyed slide on top. A flex column here folds the exit orphan's height into the plate for the whole exit
        /// and the centred plate bounces twice per step; MeasureZStack has no orphan fold (ch 28 §9.1 #1).</summary>
        Element TextStack(ReleaseHighlight h, string id, bool store, bool hasPager)
        {
            var layers = new Element[_items.Count + 1];
            for (int i = 0; i < _items.Count; i++)
            {
                var other = _items[i];
                layers[i] = new BoxEl
                {
                    Key = "hv:" + SlideId(other, i) + ":size",
                    Direction = 1, Padding = TextPadding(hasPager),
                    Opacity = 0f, HitTestVisible = false,   // inert: no hit-test, and its button is disabled (never trapped)
                    Children = [TextColumn(other.Highlight, HighlightVisibility.IsStore(other.Highlight), live: false)],
                };
            }
            layers[_items.Count] = new BoxEl   // painted last, over the sizers
            {
                Key = "hv:" + id + ":txt", Animate = HighlightViewerMotion.For(_dir),
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                Children =
                [
                    // ContentSized (issue #89 L2): the plate's auto height must sum band + pager + text, then SCROLL the text
                    // (never the image) once it would pass vpH − 64.
                    ScrollView(TextColumn(h, store, live: true)) with
                        { Grow = 1f, Shrink = 1f, MinHeight = 0f, ContentSized = true, Padding = TextPadding(hasPager) },
                ],
            };
            return new BoxEl { ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, Children = layers };
        }

        static Edges4 TextPadding(bool hasPager) => new(24f, hasPager ? 8f : 16f, 24f, 24f);

        /// <summary>Title, the WHOLE body, and — mutually exclusive — the store CTA or "Try it" for an in-app deep link.
        /// <paramref name="live"/> = false builds the sizer's twin: same geometry, no live link, selection or action.</summary>
        Element TextColumn(ReleaseHighlight h, bool store, bool live)
        {
            var content = new List<Element>(3)
            {
                new TextEl(h.Title) { Size = 20f, Weight = 600, LineHeight = 28f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                RichTextBlock.Paragraph(ToSpans(MarkdownLite.Tokenize(h.Body), live ? OpenLink : IgnoreLink), isTextSelectionEnabled: live)
                    with { Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Margin = new Edges4(0f, 8f, 0f, 0f) },
            };
            Element? action = null;
            if (store)
                action = Button.Accent(Loc.Get(Strings.WhatsNew.StoreCta), live ? (Action)OpenStoreListing : Inert, isEnabled: live);
            else if (h.DeepLink is { Length: > 0 } dl
                     && Shell.DeepLink(dl, Platform.Settings.Get(Platform.Keys.DeveloperMode)) is { Kind: Shell.DeepLinkKind.Open } verb)
            {
                var route = verb.Route;
                action = Button.Accent(Loc.Get(Strings.WhatsNew.TryIt), live ? () => TryIt(route) : Inert, isEnabled: live);
            }
            if (action is not null)
                content.Add(new BoxEl { Direction = 0, Gap = 8f, Margin = new Edges4(0f, 16f, 0f, 0f), Children = [action] });
            return new BoxEl { Direction = 1, MaxWidth = 720f, AlignSelf = FlexAlign.Start, Children = content.ToArray() };
        }
    }

    // ══ REGION — HighlightViewerMotion (a separate named region: ch 28 §8; its test drives it headless) ════════════════

    /// <summary>The viewer's slide: a directional ENTER (±24 DIP + fade, 250 ms SmoothOut — <c>Motion.EntranceOffsetPx</c> and
    /// <c>Expressive.Fast</c>) over a direction-FREE exit (a 120-ms fade in place). The exit is direction-free by necessity:
    /// the engine seeds an orphan's exit from the spec it MOUNTED with, so a directional exit would replay the PREVIOUS
    /// step's direction on the way out. Reduced motion needs no branch — the scheduler skips Enter/Exit tracks.</summary>
    public static class HighlightViewerMotion
    {
        /// <summary>== <c>Motion.EntranceOffsetPx</c>; a slideshow should visibly move (a page nudge is 8).</summary>
        public const float SlideDistance = 24f;
        public const float ExitMs = 120f;

        public static LayoutTransition SlideForward => new(
            TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
            Enter: new EnterExit(Dx: SlideDistance, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dx: 0f, Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(ExitMs, Easing.FluentAccelerate));

        public static LayoutTransition SlideBack => SlideForward with { Enter = new EnterExit(Dx: -SlideDistance, Opacity: 0f, Active: true) };

        /// <summary>The FIRST slide: no entrance (the Modal chrome already scaled the plate in), but the same exit.</summary>
        public static LayoutTransition ExitOnly => SlideForward with { Enter = default };

        public static LayoutTransition For(HighlightSlideDirection d) => d switch
        {
            HighlightSlideDirection.Forward => SlideForward,
            HighlightSlideDirection.Back => SlideBack,
            _ => ExitOnly,
        };
    }

    // ══ 7. THE AFTER-UPDATE PLATE ════════════════════════════════════════════════════════════════════════════════════

    // 0 idle · 1 a load is in flight, or the automatic plate already resolved this launch (opened, or found nothing).
    static int s_afterUpdateLatch;

    static AfterUpdateVerdict CurrentGate() => AfterUpdateGate.Decide(
        Platform.Settings.Get(Platform.Keys.ReleaseNotesPendingFrom),
        Platform.Settings.Get(Platform.Keys.ReleaseNotesAutoShow),
        Setup.Gating.IsPending(Platform.Settings) || Setup.WizardOpen.Peek(),
        CrashNoticeThisLaunch);

    /// <summary>Zero-size, INSIDE the overlay host (only there does <c>Overlay.Service</c> resolve the real service). The
    /// wizard deferral re-evaluates on every marker bump, so the plate still appears THIS launch once setup is done.</summary>
    sealed class AfterUpdateChromeView : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var post = UsePost();
            int wizardEpoch = Setup.WizardMarkerEpoch.Value;
            bool wizardOpen = Setup.WizardOpen.Value;

            UseEffect(() =>
            {
                if (CurrentGate() != AfterUpdateVerdict.Open) return;                 // every deferral leaves the key armed
                if (Interlocked.Exchange(ref s_afterUpdateLatch, 1) != 0) return;   // one load at a time, one plate per launch
                _ = LoadThenOpenAsync(overlay, post, Platform.Settings.Get(Platform.Keys.ReleaseNotesPendingFrom), automatic: true);
            }, DepKey.From(wizardEpoch, wizardOpen ? 1 : 0));

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Shrink = 0f };
        }
    }

    /// <summary>ch 28 §9.4 option (a): load the document FIRST (off the UI thread, posters resolved), then open the plate at
    /// its final height — no ~213 → 505 DIP pop. The automatic path re-checks the gates on the UI thread (the wizard may have
    /// opened meanwhile) and, when the load resolved to NOTHING, opens nothing and leaves pendingFrom ARMED.</summary>
    static async Task LoadThenOpenAsync(IOverlayService overlay, Action<Action> post, string fromQuad, bool automatic)
    {
        (ReleaseNotesDocument Doc, HighlightItem[] Cards)? loaded = null;
        try
        {
            var store = Notes;
            var me = Running();
            loaded = await Task.Run(() => LoadSummaryAsync(store, me, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Warn("whatsnew", "dialog notes load failed", ex); }

        post(() =>
        {
            if (automatic)
            {
                if (CurrentGate() != AfterUpdateVerdict.Open) { Interlocked.Exchange(ref s_afterUpdateLatch, 0); return; }   // retry on the next bump
                if (!AfterUpdateGate.Consume(Platform.Settings, opening: loaded is not null))
                {
                    Log.Info("whatsnew", "after-update notes unavailable; the plate stays armed for the next launch");
                    return;
                }
            }
            else AfterUpdateGate.Consume(Platform.Settings, opening: true);
            OpenPlate(overlay, fromQuad, loaded?.Doc, loaded?.Cards ?? []);
        });
    }

    /// <summary>A raw Modal overlay, not ContentDialog (which clamps its card): 720 wide, Modal dismiss + chrome, the chrome's
    /// own smoke. No ClosingAction veto, so Escape closes it.</summary>
    static void OpenPlate(IOverlayService overlay, string fromQuad, ReleaseNotesDocument? doc, HighlightItem[] cards)
    {
        OverlayHandle? handle = null;
        void Close() => handle?.Close();
        handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new PlateView(overlay, fromQuad, doc, cards, Close)),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal) { ScrimVisual = true });
    }

    /// <summary>The plate. Every input is final at mount (the load already landed), so the ctor args are legitimately frozen;
    /// the checkbox is the only live state. Geometry comes from <see cref="HighlightCardMetrics"/> — the same numbers the
    /// 620-DIP plate-cap test asserts.</summary>
    sealed class PlateView(IOverlayService overlay, string fromQuad, ReleaseNotesDocument? doc, HighlightItem[] cards, Action close) : Component
    {
        readonly Signal<bool> _dontShow = new(false);

        public override Element Render()
        {
            var body = new List<Element>(3)
            {
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S,
                    Padding = new Edges4(M.PlatePadX, M.HeroPadTop, M.PlatePadX, M.HeroPadBottom),
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true,
                            Children =
                            [
                                Pill(Loc.Get(Strings.WhatsNew.Dialog.Updated), accent: true),
                                // LEFT: the previous run's quad with its fourth part dropped; RIGHT: the running quad verbatim.
                                Pill(Notify.AppUpdateVersion.ReleaseTagVersion(fromQuad) + " → " + Platform.Version.Quad, mono: true),
                            ],
                        },
                        new TextEl(Strings.WhatsNew.Dialog.Welcome(VersionDisplayText())) { Size = 26f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new TextEl(doc?.Tagline ?? "") { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 560f },
                    ],
                },
            };

            if (cards.Length > 0)
            {
                var row = new Element[cards.Length];
                for (int i = 0; i < cards.Length; i++)
                {
                    int idx = i;
                    row[i] = CardFor(cards[i], () => OpenViewer(overlay, cards, idx, close), compact: true)
                        with { Key = "dlg-hl:" + (cards[i].Highlight.Id is { Length: > 0 } id ? id : idx.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
                }
                body.Add(new BoxEl
                {
                    Direction = 0, Gap = M.CardGap, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
                    Padding = new Edges4(M.PlatePadX, M.RowPadTop, M.PlatePadX, M.RowPadBottom),
                    Children = row,
                });
            }

            body.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Padding = new Edges4(M.PlatePadX, 14f, M.PlatePadX, 14f),
                Fill = Tok.FillLayerAlt, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
                Children =
                [
                    // Written IMMEDIATELY, not on close.
                    CheckBox.Create(Loc.Get(Strings.WhatsNew.Dialog.DontShow), _dontShow, static v => Platform.Settings.Set(Platform.Keys.ReleaseNotesAutoShow, !v)),
                    Spacer(),
                    // close THEN navigate — the reverse of the viewer's "Try it": this plate is the thing being replaced.
                    Button.Standard(Loc.Get(Strings.WhatsNew.Dialog.Full), () => { close(); Shell.GoTo(new Shell.Route(Shell.RouteKind.WhatsNew)); }),
                    Button.Accent(Loc.Get(Strings.WhatsNew.Dialog.GotIt), close),
                ],
            });

            return new BoxEl
            {
                Width = M.PlateWidth, Direction = 1, MaxHeight = M.PlateMaxHeight,
                Corners = CornerRadius4.All(Radii.Overlay), ClipToBounds = true, Fill = Tok.FillSolidBase,
                Children = body.ToArray(),
            };
        }
    }
}
