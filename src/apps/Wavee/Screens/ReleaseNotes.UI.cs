// ── Screens/ReleaseNotes.UI.cs ─────────────────────────────────────────────────────────────────────────────────────
// the release-notes CORE HOSTS and doors: the What's-new page host (`PageHost`, a `[CoreHost]`: the load, last-seen, the issue-state republish and the "only the
// latest" state), the highlight viewer's overlay (`OpenViewer`), the after-update plate's zero-size chrome, its gates, load and overlay (`OpenPlate`), and the doors
// other owners call (InstallUi, AfterUpdateChrome, CrashNoticeThisLaunch, ShowSummaryAgain, RunningNotesUnread, VersionDisplayText, Pill)
//
// THE PICTURES ARE CLASSIC'S (themes plan T11c): the page, the highlight strip and cards, the changelog sections / rows / issue chips / avatars, the release rail, the
// since-banner, loading and empty, the viewer and the after-update plate are the registered views `Pages.WhatsNew`, `ReleaseNotes.Viewer` and
// `ReleaseNotes.AfterUpdatePlate` (`Themes\Classic\Structure\Views\Screens\ClassicReleaseNotes*.cs`, numbers in `Rules\ClassicReleaseNotesRules.cs`).
//
// Role: UI
// Owner: R
// Wave: 6
// Spec: ch 28 §9.5
//
//   Shell.SetPage(WhatsNew) → PageFor(versionArg) → PageHost       signals: view · loaded · onlyLatest (ch 28 §1.2)
//   Overlay (ScrimVisual=false) → ViewKeys.ReleaseNotesViewer      the veil, the plate, the band with its chrome, the pager, the text
//   AfterUpdateChromeView (zero-size) → gates → loads FIRST → OpenPlate opens ViewKeys.ReleaseNotesAfterUpdatePlate at its final height (ch 28 §9.4 option a)
//
// Every decision a test can reach is in ReleaseNotes.Host.cs's PURE region; this file is wiring. The page is a DOCUMENT, not a virtualized list (ch 28 §9.2):
// posters are resolved in the loader, and nothing here touches the disk or the network.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

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
        _ = LoadThenOpenAsync(overlay, post, ReleaseNotesModel.SummaryFromQuad(), automatic: false);
    }

    /// <summary>"Open What's new" carries an unread dot while the running release's notes have never been opened.</summary>
    public static bool RunningNotesUnread() => ReleaseNotesModel.RunningNotesUnread();

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

    static RunningBuild Running() => ReleaseNotesModel.Running();

    static void OpenUrl(string url)
    {
        if (Actions.Services.OpenExternal is { } open) { open(url); return; }
        InputHooks.Current.Default.OpenUri?.Invoke(url);
    }

    static void OpenStoreListing() => OpenUrl(Update.StorePageUrl(Links.StoreIdOrFallback(Platform.Version.StoreId)));

    // The verbs the views carry: cached once (never a fresh lambda per render).
    static readonly Action<string> OpenLink = static url => OpenUrl(url);
    static readonly Action OpenStoreListingVerb = static () => OpenStoreListing();
    static readonly Action<string> OpenReleasePage = static version => Shell.GoTo(Shell.Parse("whatsnew", version));

    /// <summary>The hero / plate / About pill: MinHeight 22, 11.5/600, accent or subtle, optionally Cascadia Code. Settings › About and the setup wizard's
    /// terms page draw it too, so it stays a core door (Classic's hero calls it as well).</summary>
    public static Element Pill(string text, bool accent = false, bool mono = false) => new BoxEl
    {
        Shrink = 0f, MinHeight = 22f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(9f, 2f, 9f, 2f), Corners = CornerRadius4.All(Radii.Pill),
        Fill = accent ? Tok.AccentDefault : Tok.FillSubtleSecondary,
        Children =
        [
            R.Type.MicroMeta(text) with
            {
                Weight = 600, MaxLines = 1,
                Color = accent ? Tok.TextOnAccentPrimary : Tok.TextSecondary,
                FontFamily = mono ? "Cascadia Code" : null,
            },
        ],
    };

    // ══ 2. THE PAGE HOST ═════════════════════════════════════════════════════════════════════════════════════════════

    static Element PageFor(string versionArg) => Embed.Comp(() => new PageHost(versionArg)) with { Key = "whatsnew:" + versionArg };

    /// <summary>Reading the page is what marks the notes seen — and <c>lastSeen</c> is read BEFORE the load starts and
    /// carried into it (ch 28 §0.11): advancing first hands the range (me, me) and the banner + every dot die. The host owns
    /// the load and its state; <c>Pages.WhatsNew</c> draws what it is handed.</summary>
    [CoreHost]
    sealed class PageHost(string versionArg) : Component
    {
        readonly Signal<bool> _onlyLatest = new(false);
        readonly Signal<ReleaseNotesView?> _view = new(null);
        readonly Signal<bool> _loaded = new(false);      // "still loading" vs "nothing to show"
        Action? _showOnlyLatest;

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
                string lastSeen = ReleaseNotesModel.LastSeen();   // BEFORE the load
                var cts = new CancellationTokenSource();
                _ = LoadAsync(lastSeen, post, cts.Token);
                ReleaseNotesModel.MarkSeen();   // seen on OPEN
                return (Action?)(() => cts.Cancel());
            }, DepKey.Empty);

            var merged = view?.MergedHighlights ?? [];
            return ViewSlots.View(ViewKeys.WhatsNewPage, new WhatsNewPageProps
            {
                State = view is not null ? WhatsNewState.Loaded : loaded ? WhatsNewState.Empty : WhatsNewState.Loading,
                View = view,
                OnlyLatest = onlyLatest,
                RunningVersion = Platform.Version.Core,
                ShowOnlyLatest = _showOnlyLatest ??= () => _onlyLatest.Value = true,
                CopyLink = text =>
                {
                    hooks.Clipboard?.SetText(text);
                    Notify.Say(Loc.Get(Strings.WhatsNew.LinkCopied), InfoBarSeverity.Success);
                },
                OpenHighlight = i => OpenViewer(overlay, merged, i, closeHost: null),
                OpenUrl = OpenLink,
                OpenStoreListing = OpenStoreListingVerb,
                OpenRelease = OpenReleasePage,
            });
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
    }

    // ══ 3. THE HIGHLIGHT VIEWER'S OVERLAY ════════════════════════════════════════════════════════════════════════════

    /// <summary>The modal one-highlight-at-a-time viewer. Anchor-less, Modal chrome for the ContentDialog scale/fade and the
    /// focus trap, but ScrimVisual = false (the view paints its OWN veil so it never stacks a second smoke over the
    /// after-update plate) and LightDismiss (a veil click is how a viewer closes). <paramref name="closeHost"/> is the
    /// after-update plate's close, or null from the page.</summary>
    static OverlayHandle OpenViewer(IOverlayService overlay, IReadOnlyList<HighlightItem> items, int initial, Action? closeHost)
    {
        OverlayHandle? handle = null;
        var props = new HighlightViewerProps
        {
            Items = items,
            Initial = initial,
            Close = () => handle?.Close(),
            // Navigate FIRST, then the host plate, then this viewer — so the shell has navigated before either plate
            // starts its exit and focus lands somewhere (ch 28 §6.3).
            TryItFor = h => ReleaseNotesModel.OpenRouteFor(h.DeepLink) is { } route
                ? () => { Shell.GoTo(route); closeHost?.Invoke(); handle?.Close(); }
                : null,
            OpenUrl = OpenLink,
            OpenStoreListing = OpenStoreListingVerb,
        };
        handle = overlay.Open(
            static () => NodeHandle.Null,
            () => ViewSlots.View(ViewKeys.ReleaseNotesViewer, props),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Modal) { ScrimVisual = false });
        return handle;
    }

    // ══ 4. THE AFTER-UPDATE PLATE ════════════════════════════════════════════════════════════════════════════════════

    // The gate, the once-per-launch latch, the load and the resolve step are `ReleaseNotesModel` (C15).

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
                if (!ReleaseNotesModel.TryBeginAutomaticLoad()) return;   // every deferral leaves the key armed; one load at a time, one plate per launch
                _ = LoadThenOpenAsync(overlay, post, ReleaseNotesModel.PendingFrom(), automatic: true);
            }, DepKey.From(wizardEpoch, wizardOpen ? 1 : 0));

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Shrink = 0f };
        }
    }

    /// <summary>ch 28 §9.4 option (a): load the document FIRST (off the UI thread, posters resolved), then open the plate at
    /// its final height — no ~213 → 505 DIP pop. The automatic path re-checks the gates on the UI thread (the wizard may have
    /// opened meanwhile) and, when the load resolved to NOTHING, opens nothing and leaves pendingFrom ARMED.</summary>
    static async Task LoadThenOpenAsync(IOverlayService overlay, Action<Action> post, string fromQuad, bool automatic)
    {
        var loaded = await ReleaseNotesModel.LoadSummaryAsync().ConfigureAwait(false);

        post(() =>
        {
            if (!ReleaseNotesModel.Resolve(automatic, loaded is not null)) return;   // automatic: retry on the next bump / stays armed
            OpenPlate(overlay, fromQuad, loaded?.Doc, loaded?.Cards ?? []);
        });
    }

    /// <summary>A raw Modal overlay, not ContentDialog (which clamps its card): 720 wide, Modal dismiss + chrome, the chrome's
    /// own smoke. No ClosingAction veto, so Escape closes it.</summary>
    static void OpenPlate(IOverlayService overlay, string fromQuad, ReleaseNotesDocument? doc, HighlightItem[] cards)
    {
        OverlayHandle? handle = null;
        void Close() => handle?.Close();
        var props = new AfterUpdatePlateProps
        {
            // LEFT: the previous run's quad with its fourth part dropped; RIGHT: the running quad verbatim.
            VersionRange = Notify.AppUpdateVersion.ReleaseTagVersion(fromQuad) + " → " + Platform.Version.Quad,
            VersionText = VersionDisplayText(),
            Tagline = doc?.Tagline ?? "",
            Cards = cards,
            OpenCard = idx => OpenViewer(overlay, cards, idx, Close),
            SetDontShowAgain = static v => ReleaseNotesModel.SetDontShowAgain(v),
            // close THEN navigate — the reverse of the viewer's "Try it": this plate is the thing being replaced.
            ShowFull = () => { Close(); Shell.GoTo(new Shell.Route(Shell.RouteKind.WhatsNew)); },
            Close = Close,
            OpenStoreListing = OpenStoreListingVerb,
        };
        handle = overlay.Open(
            static () => NodeHandle.Null,
            () => ViewSlots.View(ViewKeys.ReleaseNotesAfterUpdatePlate, props),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal) { ScrimVisual = true });
    }
}
