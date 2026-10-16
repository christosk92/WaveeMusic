// ── Shell/Shell.Overlays.UI.cs ─────────────────────────────────────────────────────────────────────────────────────
// the CORE side of the overlays: the profile chip's host and the account menu's rows (what exists and what each does) + the Play ▸
// cascade, the logout confirm's popup, the play-a-link card's state, the playback-runtime banner's gate, the digital-signature
// dialog's opener, the in-app toast decision sites, and the mount of +Screens/Setup.UI.Runtime.cs (the setup card's body is not here
// — A18). THE PICTURES are the structure's since T11a (`Overlay.ProfileChip`, `.ProfileMenu`, `.RuntimeBanner`, `.Toast`,
// `Dialog.LogoutConfirm`, `.PlayLink`, `.Signature`; Classic's are `Themes\Classic\Structure\Views\Overlays` and `Views\Dialogs`).
//
// Role: UI
// Owner: I
// Wave: 4
// Budget: 1700 lines
// Spec: ch 19 §9.4 (1,700 of its Shell.UI.cs +2,000 share; the note under the table restated it 1,500 → 2,000 when the panel landed)
//
// THE TWO MOUNT POINTS. `Shell.Overlays()` is one full-window, hit-test-pass-through lane for the frame's ZStack (owner
// I1 places it above the page column and below the palette): the floating runtime banner, top-centred and clear of the
// 48-DIP chrome row, plus the zero-size watchers that must live INSIDE the overlay host — the update-lifecycle toast,
// the runtime-missing toast, the setup-request door, and the binder that fills `Actions.Services`' overlay-bound seams.
// `Shell.ProfileChip()` is the trailing island's account chip (owner I1 places it; it reads `ChromeLayout` itself).
//
// THE SEAMS THIS FILE LEAVES FOR LATER WAVES (each a null-safe static, so a build without the owner still runs):
//   · `PickAndPlayFile`       — owner O's local-file verb. Null ⇒ the whole `Play ▸` row is ABSENT (never disabled).
//   · `PlayLinkRouter.Modules` / `Match` — owner T's module host: the match-capable modules, and the router. Null router ⇒
//                                the dialog answers "Nothing installed can play this link." in place.
//   · `NotificationsLauncher` — owner I1's panel launcher, re-anchored to the chip when the ladder folds the bell.
//   · `ReportDialogOpener`    — owner R's report dialog (`wavee://open?route=report`).
// The update toast's action button routes through `Notify.UpdateCommand` (G-092) via `Notify.VerbFor` — NOT a second
// seam of its own: 0.2.9-era `UpdateVerb` and the panel row's `Notify.UpdateCommand` were two doors an update host
// would have had to fill, and with neither wired an update toast's button did nothing.
// The runtime banner reads `Setup.Runtime` (`+Screens/Setup.UI.Runtime.cs`), whose host the SHELL half installs.

using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. THE MOUNT POINTS ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The overlay lane: the runtime banner and the in-host watchers.</summary>
    // MOUNT POINT (stage B contract)
    public static Element Overlays() => Embed.Comp(() => new OverlaysLayer());

    /// <summary>The trailing island's account chip (and its menu). Not in the stage-B table: owner I1's merged chrome row
    /// mounts it where 0.2.9's `MergedChromeRow.Trailing` put `ProfileMenu`.</summary>
    // MOUNT POINT (stage B contract)
    public static Element ProfileChip() => Embed.Comp(() => new ProfileChipCore());

    // ══ 2. THE SEAMS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"Play ▸ File…" — pick a local file and play it (owner O). Null ⇒ no `Play ▸` row at all.</summary>
    public static Action? PickAndPlayFile;

    /// <summary>Owner I1's notification-panel launcher: (overlay, anchor thunk, the handle cell).</summary>
    public static Action<IOverlayService, Func<NodeHandle>, Ref<OverlayHandle?>>? NotificationsLauncher;

    /// <summary>Owner R's report dialog: (overlay, kind arg).</summary>
    public static Action<IOverlayService, string>? ReportDialogOpener;

    // ══ 3. THE OVERLAY LAYER ═══════════════════════════════════════════════════════════════════════════════════════

    sealed class OverlaysLayer : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();

            // The seams that need the REAL overlay service / hooks / post, filled once from inside the overlay host (the
            // 0.2.9 `ActionServicesOverlayBinder`). A confirm-required bound action refuses to run without them. The
            // wiring is the composition root's (`App.BindOverlayServices`); this is where the host's values exist.
            UseEffect(() => App.BindOverlayServices(overlay, hooks, post), DepKey.Empty);

            // The structure's per-band fit of the overlays the core composes (TC9): hand over the live viewport (a dialog opens from a handler, outside any render)
            // and dock the engine's toast lane above the bar. Reading the facts here is what re-docks it when a tier crosses.
            OverlayFit.Bind(UseContextSignal(Viewport.Size));
            OverlayFit.Toasts(Theming.ThemeHost.Facts.Value.Policies.PlayerBar.Height);

            return new BoxEl
            {
                Grow = 1f, HitTestPassThrough = true,
                Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center,
                Padding = new Edges4(0f, Setup.RuntimeRules.BannerTopPad, 0f, 0f),
                Children =
                [
                    Embed.Comp(() => new RuntimeBannerChrome()),
                    Embed.Comp(() => new UpdateToastWatcher()),
                    Embed.Comp(() => new RuntimeToastWatcher()),
                    Embed.Comp(() => new PlaybackFailureToastWatcher()),
                    Setup.SignInDoor(),                               // opens the sign-in surface on request or on a fresh SignInRequired (B5)
                    // Mount order is load-bearing (ch 28 §9.3.8; crash-diagnostics-implementation.md §D "Component
                    // tree"): the crash prompt (Crash.Chrome) sets ReleaseNotes.CrashNoticeThisLaunch before the
                    // after-update plate (ReleaseNotes.AfterUpdateChrome) reads it — so the wizard chrome, THEN the
                    // report chrome, THEN the crash chrome, THEN the after-update chrome.
                    Setup.WizardChrome(),
                    Feedback.Chrome(),
                    Crash.Chrome(),
                    ReleaseNotes.AfterUpdateChrome(),
                ],
            };
        }
    }

    // ══ 4. THE PLAYBACK-RUNTIME BANNER AND ITS GATE (ch 19 W22/W23, §6.8) ═════════════════════════════════════════

    /// <summary>The banner's host + the setup-request watcher. Floats; never reflows the page. No motion: gated off it is a bare
    /// zero-footprint box (`Overlay.RuntimeBanner` draws both).</summary>
    [CoreHost]
    sealed class RuntimeBannerChrome : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var post = UsePost();
            var facts = Setup.Runtime.Status.Value;
            _ = Setup.Runtime.BannerEpoch.Value;            // settings writes are not signals — the epoch is the subscription
            bool covering = Setup.Runtime.WizardCovering.Value;
            bool dismissed = Platform.Settings.Get(Platform.Keys.PlaybackRuntimeSetupDismissed);
            bool pending = Platform.Settings.Get(Platform.Keys.SetupPending);
            bool show = OverlayGates.ShowsRuntimeBanner(facts.Issue, dismissed, covering, pending);

            int request = Setup.Runtime.OpenRequest.Value;
            var lastRequest = UseRef(request);
            var modal = UseRef<OverlayHandle?>(null);

            void OpenSetup(string door)
            {
                if (modal.Value is { IsOpen: true }) return;   // one card at a time
                Log.Event(WaveeLogLevel.Info, "ui", "runtime.banner.open_setup", "Playback runtime setup requested", null, -1, null,
                    WaveeLogField.Of("issue", facts.Issue.ToString()), WaveeLogField.Of("dismissed", dismissed),
                    WaveeLogField.Of("door", door));
                var h = Setup.OpenRuntimeCard(overlay, post);
                modal.Value = h;
                h.ClosedAction = () => modal.Value = null;
            }

            UseEffect(() =>
            {
                if (request == lastRequest.Value) return;
                lastRequest.Value = request;
                Log.Event(WaveeLogLevel.Debug, "ui", "runtime.banner.request", "Playback runtime setup request signal changed",
                    null, -1, null, WaveeLogField.Of("request", request));
                OpenSetup("request");
            }, DepKey.From(request));

            return ViewSlots.View(ViewKeys.OverlayRuntimeBanner, new RuntimeBannerProps
            {
                Show = show,
                Message = show ? Loc.Get(Setup.RuntimeRules.BannerLocKey(facts.Issue)) : "",
                Dismiss = s_dismissBanner,
                SetUp = () => OpenSetup("banner"),
            });
        }

        static readonly Action s_dismissBanner = static () => Setup.Runtime.Dismiss("banner");
    }

    /// <summary>The runtime-missing toast (0.2.9 `LiveSessionHost:637`, the third door): when a track fails because the
    /// local runtime is missing and a host could fix it, one Warning card with "Set up". Suppressed while the wizard is
    /// asking the same question, and while the banner is already on screen saying it.</summary>
    sealed class RuntimeToastWatcher : Component
    {
        public override Element Render()
        {
            UseEffect(() =>
            {
                var fault = Playback.Error.Value;
                if (fault != Playback.Fault.RuntimeMissing || Setup.Runtime.Host is null) return;
                bool pending = Platform.Settings.Get(Platform.Keys.SetupPending);
                if (!OverlayGates.ShowsRuntimeMissingToast(fault, hostInstalled: true, Setup.Runtime.WizardCovering.Peek(), pending,
                        Setup.Runtime.Status.Peek().Issue, Platform.Settings.Get(Platform.Keys.PlaybackRuntimeSetupDismissed))) return;
                Notify.Say(Loc.Get(Strings.Playback.Runtime.Missing), InfoBarSeverity.Warning,
                    Loc.Get(Strings.Playback.Runtime.SetUp), Setup.Runtime.RequestOpen);
            });
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    /// <summary>"Couldn't take over playback" / "Couldn't transfer playback": one toast per BUMP of the model's two failure
    /// counters (<see cref="Playback.ClaimRejectedCount"/>, <see cref="Playback.TransferFailedCount"/>). The decision — did a
    /// counter move, and which story wins — is <see cref="PlaybackFailureToasts.Between"/>; this only reads and says it.
    /// The baseline is taken at mount, so a remount never replays an old failure.</summary>
    sealed class PlaybackFailureToastWatcher : Component
    {
        public override Element Render()
        {
            var seen = UseRef((Claim: Playback.ClaimRejectedCount.Peek(), Transfer: Playback.TransferFailedCount.Peek()));
            UseEffect(() =>
            {
                uint claim = Playback.ClaimRejectedCount.Value, transfer = Playback.TransferFailedCount.Value;
                var failure = PlaybackFailureToasts.Between(seen.Value.Claim, claim, seen.Value.Transfer, transfer);
                seen.Value = (claim, transfer);
                switch (failure)
                {
                    case PlaybackFailure.ClaimRejected:
                        Notify.Say(Loc.Get(Strings.Player.TakeOverFailed), InfoBarSeverity.Warning, dedupeKey: "playback.claim.rejected");
                        break;
                    case PlaybackFailure.TransferFailed:
                        Notify.Say(Loc.Get(Strings.Player.TransferFailed), InfoBarSeverity.Warning, dedupeKey: "playback.transfer.failed");
                        break;
                }
            });
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    // ══ 4b. THE DIGITAL-SIGNATURE DIALOG (ch 19 W33) ═══════════════════════════════════════════════════════════════

    /// <summary>The signature sheet: a SECOND, nested ContentDialog at rung 4 (548), because the setup dialog's width is
    /// set once at Show and cannot grow. A summary line under its verdict badge, a divider, nine label/value rows at
    /// 11/12, one Close. Opened from the setup card's Ready view and from Settings ▸ Playback (owner R).</summary>
    public static void OpenSignatureDialog(IOverlayService overlay, Setup.RuntimeFacts facts)
    {
        if (facts.Signature is not { } signature)
        {
            Notify.Say(Loc.Get(Strings.Playback.Runtime.ViewSignatureFailed), InfoBarSeverity.Warning);
            return;
        }
        Log.Event(WaveeLogLevel.Debug, "ui", "runtime.setup.signature_details", "Playback runtime signature details opened",
            null, -1, null, WaveeLogField.Of("subject", signature.Subject), WaveeLogField.Of("trust", signature.Trust.ToString()));
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(LocSignatureTitle);
            d.PrimaryText = "";
            d.SecondaryText = "";
            d.CloseText = Loc.Get(Strings.Common.Close);
            OverlayFit.Dialog(d, Setup.RuntimeRules.SignatureDialogWidth);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = ViewSlots.View(ViewKeys.DialogSignature, new SignatureSheetProps
            {
                Summary = Setup.RuntimeSignatureSummary(facts),
                Trusted = signature.Trust == Setup.RuntimeTrust.Trusted,
                Rows =
                [
                    new(Loc.Get(Strings.Runtime.Sig.Publisher), signature.Subject),
                    new(Loc.Get(Strings.Runtime.Sig.Issuer), signature.Issuer),
                    new(Loc.Get(Strings.Runtime.Sig.Trust), Loc.Get(Setup.RuntimeRules.TrustLocKey(signature.Trust))),
                    new(Loc.Get(Strings.Runtime.Sig.Reason), signature.Reason),
                    new(Loc.Get(Strings.Runtime.Sig.ValidFrom), Setup.RuntimeRules.SignatureDate(signature.ValidFrom)),
                    new(Loc.Get(Strings.Runtime.Sig.ValidTo), Setup.RuntimeRules.SignatureDate(signature.ValidTo)),
                    new(Loc.Get(Strings.Runtime.Sig.Thumbprint), signature.Thumbprint),
                    new(Loc.Get(Strings.Runtime.Sig.Pinned), Loc.Get(facts.PinnedFingerprint ? Strings.Runtime.Sig.Yes : Strings.Runtime.Sig.No)),
                    new(Loc.Get(Strings.Runtime.Sig.File), signature.FilePath),
                ],
            });
        });
    }

    /// <summary>New loc key (stageB-loc/I3.json): the sheet's title, "Digital signature" — hard-coded English in 0.2.9
    /// (ch 19 §6.10); the rest of the sheet's words already had `runtime.sig.*` keys.</summary>
    const string LocSignatureTitle = "runtime.sig.title";

    // ══ 5. THE UPDATE-LIFECYCLE TOAST (ch 19 §0 #11, W20; the decision is Notify.AppUpdateToasts) ═════════════════

    /// <summary>ONE card for the whole update lifecycle (dedupe key "update"): a planned toast only when the state MOVED;
    /// the download card mounted once with its bar BOUND to a float signal, so twenty progress ticks are twenty float
    /// writes, not twenty reconciles. The progress value is written BEFORE the plan early-out and before the dial check,
    /// so a card that appears mid-download starts at the right percentage.</summary>
    sealed class UpdateToastWatcher : Component
    {
        public override Element Render()
        {
            UseEffect(() =>
            {
                // The model remembers the previous snapshot, writes the bound progress first, plans the toast and applies the
                // dial (a silenced one kills it outright); this only shows what it answers.
                var next = Notify.Update.Value;
                if (UpdateToastModel.Shared.Advance(next, static () => Notify.Prefs.Level(NotifyTopic.AppUpdates)) is not { } plan) return;
                Show(plan, next);
            });
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        static void Show(ToastPlan plan, AppUpdateSnapshot snapshot)
        {
            bool hasAction = plan.Actions.Length > 0;
            var first = hasAction ? plan.Actions[0] : default;
            bool downloading = snapshot.State == AppUpdateState.Downloading;
            string title = plan.Title;
            // D29: through the ONE toast door (G-212) — a direct Toast.Show bypassed Notify.Say's announce rule.
            Notify.Say(plan.Body, plan.Severity,
                actionLabel: hasAction ? Notify.AppUpdateToasts.Label(first) : null,
                onAction: hasAction ? () => UpdateToastModel.RunAction(first, snapshot, OpenWhatsNew) : null,
                dedupeKey: UpdateToastModel.ToastKey,
                durationMs: plan.Sticky ? 0f : UpdateToastModel.AutoDismissMs,
                title: title.Length > 0 ? title : null,
                // The padded custom card exists only while Downloading; Installing/Failed are standard sticky InfoBars
                // on the same key, so they replace the card's body in place.
                customContent: downloading ? () => UpdateProgressCard(title) : null);
        }

        static Element UpdateProgressCard(string title)
            => ViewSlots.View(ViewKeys.OverlayToast, new ToastCardProps { Title = title, Progress = UpdateToastModel.Shared.Progress });

        /// <summary>What's new on the toast: the notes of <paramref name="version"/>.</summary>
        static void OpenWhatsNew(string version) => GoTo(Parse("whatsnew", version));
    }

    // ══ 6. THE PROFILE CHIP AND ITS MENU (ch 19 W4–W7, §6.2) ═════════════════════════════════════════════════════

    /// <summary>The chip's host: the menu's overlay handle (open, toggle, light dismiss, focus trap), the anchor capture, the chrome
    /// ladder's flags and the Store-shot presentation switch. The picture is `Overlay.ProfileChip`.</summary>
    [CoreHost]
    sealed class ProfileChipCore : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var notifyHandle = UseRef<OverlayHandle?>(null);     // a SECOND handle: the panel re-anchored here never fights the menu's

            var layout = ChromeLayout.Value;                      // subscribe: the name column + the fold rows follow the ladder
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = Entities.Current.Users.Changed.Value;             // subscribe: the account's identity lands
            var identity = ReadIdentity();
            // Presentation mode (the Store shot): a generic picture and no name — a listing image carries no account data.
            bool presenting = FrameFacts.Presenting.Value;
            if (presenting) identity = new AccountIdentity(true, "", null);
            float nameCap = Actions.ProfileRules.NameCap(Layout.ChromeProfileNameW);

            void Close() => handle.Value?.Close();

            void OpenMenu()
            {
                if (handle.Value is { IsOpen: true }) { Close(); return; }
                // One-shot at OPEN: the menu body is a thunk, so every live value is PEEKED here (the chip subscribes; the
                // menu body peeks — a subscription inside it would be a phantom dependency).
                var peekIdentity = ReadIdentity();
                bool actionsInMenu = ChromeLayout.Peek().ActionsInMenu;
                int unread = Notify.Unread.Peek();
                var tier = Spotify.Current.Tier;
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => ProfileMenuContent(peekIdentity, tier, unread, actionsInMenu, overlay, Close,
                        () => NotificationsLauncher?.Invoke(overlay, () => anchor.Value, notifyHandle)),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    // MENU chrome, not flyout-presenter chrome: the anchored 250 ms MenuPopupThemeTransition unfold over a
                    // windowed DWM transient-acrylic popup — like every other menu in the shell.
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Flyout)
                    {
                        ConstrainToRootBounds = false,
                    });
                OverlayService.Track(handle.Value);
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return ViewSlots.View(ViewKeys.OverlayProfileChip, new ProfileChipProps
            {
                Known = identity.Known, Name = identity.Name, AvatarUrl = identity.AvatarUrl,
                ShowName = layout.ShowName, Presenting = presenting, NameCap = nameCap,
                Open = OpenMenu, Realized = h => anchor.Value = h,
            });
        }
    }

    readonly record struct AccountIdentity(bool Known, string Name, string? AvatarUrl);

    /// <summary>The signed-in account's display identity, from its user row — known only once the row carries the
    /// Identity group (the readiness predicate).</summary>
    static AccountIdentity ReadIdentity()
    {
        var me = User.Me;
        if (!me.IsValid || !me.Knows(UserFields.Identity)) return new AccountIdentity(false, "", null);
        return new AccountIdentity(true, Entities.Strings.Resolve(me.NameId), Controls.ArtUrl(me.ImageId));
    }

    /// <summary>The account flyout: the rows in the row table's order (the body, the header over stock WinUI menu rows, is `Overlay.ProfileMenu`).</summary>
    static Element ProfileMenuContent(AccountIdentity identity, Spotify.Tier tier, int unread, bool actionsInMenu,
        IOverlayService overlay, Action close, Action openNotifications)
    {
        var table = Actions.ProfileRules.Rows(PickAndPlayFile is not null, actionsInMenu, NotificationsLauncher is not null,
            hasProfile: User.Me.IsValid);
        bool dark = Theme.Dark;
        var items = new List<MenuFlyoutItem>(table.Length);
        for (int i = 0; i < table.Length; i++)
        {
            // Every row closes the menu FIRST, then acts (a modal opened over a closing menu would lose its focus restore).
            switch (table[i])
            {
                case Actions.ProfileRow.Profile:
                    // Own profile (#161): `User.Me` through the one composer, so --fake's bare account row routes too.
                    items.Add(new MenuFlyoutItem(Loc.Get(Strings.Person.List.Menu.Profile), Icons.Contact,
                        Invoke: () => { close(); GoTo(ProfileRoute.For(User.Me)); }));
                    break;
                case Actions.ProfileRow.Settings:
                    items.Add(new MenuFlyoutItem(Loc.Get(Strings.Auth.Settings), Icons.Settings,
                        Invoke: () => { close(); GoTo(new Route(RouteKind.Settings)); }));
                    break;
                case Actions.ProfileRow.Play:
                    items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Play.Menu), PlayItems(overlay, close), Icons.MusicNote));
                    break;
                case Actions.ProfileRow.Separator:
                    items.Add(MenuFlyoutItem.Separator);
                    break;
                case Actions.ProfileRow.Notifications:
                    items.Add(new MenuFlyoutItem(
                        unread > 0 ? Strings.Notifications.OverflowTitle(unread) : Loc.Get(Strings.Notifications.Title),
                        Icons.Bell, Invoke: () => { close(); openNotifications(); }));
                    break;
                case Actions.ProfileRow.Friends:
                    items.Add(new MenuFlyoutItem(Loc.Get(Strings.Shell.Friends), Icons.Friends,
                        Invoke: () => { close(); Ui.Toggle(RailMode.Friends); }));
                    break;
                case Actions.ProfileRow.Theme:
                    bool light = Actions.ProfileRules.OffersLightTheme(dark);   // labelled with the TARGET theme
                    items.Add(new MenuFlyoutItem(Loc.Get(light ? Strings.Shell.LightTheme : Strings.Shell.DarkTheme),
                        light ? Icons.Sun : Icons.Moon, Invoke: () => { close(); Appearance.ToggleMode(); }));
                    break;
                case Actions.ProfileRow.LogOut:
                    items.Add(new MenuFlyoutItem(Loc.Get(Strings.Auth.LogOut), Icons.SignOut,
                        Invoke: () => { close(); ConfirmLogout(overlay); }));
                    break;
            }
        }

        return ViewSlots.View(ViewKeys.OverlayProfileMenu, new ProfileMenuProps
        {
            Known = identity.Known, Name = identity.Name, AvatarUrl = identity.AvatarUrl,
            ShowsTier = Actions.ProfileRules.ShowsTier(tier), Premium = tier == Spotify.Tier.Premium,
            Items = items, Close = close,
        });
    }

    /// <summary>"Play ▸" — File… and Link…, then (behind a separator, only when any qualify) one Globe row per installed
    /// match-capable module, labelled by its own manifest.</summary>
    static MenuFlyoutItem[] PlayItems(IOverlayService overlay, Action close)
    {
        string generic = Loc.Get(Strings.Play.Placeholder);
        var items = new List<MenuFlyoutItem>(6)
        {
            new(Loc.Get(Strings.Play.File), Icons.Document, Invoke: () => { close(); PickAndPlayFile?.Invoke(); }),
            new(Loc.Get(Strings.Play.Link), Icons.Link, Invoke: () => { close(); OpenPlayLink(overlay, null, generic); }),
        };
        if (PlayLinkRouter.Modules?.Invoke() is { Count: > 0 } modules)
        {
            items.Add(MenuFlyoutItem.Separator);
            for (int i = 0; i < modules.Count; i++)
            {
                var module = modules[i];   // captured by value per row
                items.Add(new MenuFlyoutItem(module.MenuLabel, Icons.Globe,
                    Invoke: () => { close(); OpenPlayLink(overlay, module.Id, module.Placeholder); }));
            }
        }
        return items.ToArray();
    }

    /// <summary>Open a web url in the user's browser — only an http(s) url with a host ever reaches the shell.</summary>
    static void OpenWeb(string url)
    {
        if (!Actions.PlayLinkRules.IsWebUrl(url)) return;
        if (Actions.Services.OpenExternal is { } open) { open(url); return; }
        InputHooks.Current.Default.OpenUri?.Invoke(url);
    }

    // ══ 7. THE LOGOUT CONFIRM (ch 19 W8 — off-ladder on purpose) ══════════════════════════════════════════════════

    /// <summary>A bare Modal card, NOT a ContentDialog: no title band, no separator, no command-row fill (`Dialog.LogoutConfirm`
    /// draws it). Cancel closes it; Log out closes it, then signs out.</summary>
    static void ConfirmLogout(IOverlayService overlay)
    {
        OverlayHandle? h = null;
        h = overlay.Open(
            () => NodeHandle.Null,
            () => ViewSlots.View(ViewKeys.DialogLogoutConfirm, new LogoutConfirmProps
            {
                Cancel = () => h?.Close(),
                Confirm = () =>
                {
                    h?.Close();
                    FrameVerbs.Shared.LogOut();
                },
            }),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
    }

    // ══ 8. THE PLAY-A-LINK DIALOG (ch 19 W9–W13, §6.7) ════════════════════════════════════════════════════════════

    /// <summary>Open the paste-a-link card. The clipboard is read HERE, once: it is an open-time fact, and a card that
    /// re-read it per render would fight the user's own edits. <paramref name="pinnedModuleId"/> non-null = the user
    /// picked that module's row, so only it is asked.</summary>
    public static void OpenPlayLink(IOverlayService? overlay, string? pinnedModuleId, string placeholder)
    {
        if (overlay is null) return;
        string seed = InputHooks.Current.Default.Clipboard is { } clip && clip.TryGetText(out string text)
            ? Actions.PlayLinkRules.PrefillFrom(text)
            : "";
        OverlayHandle? h = null;
        h = overlay.Open(
            () => NodeHandle.Null,
            () => Embed.Comp(() => new PlayLinkCardCore(pinnedModuleId, placeholder, seed, () => h?.Close())),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
    }

    /// <summary>The card. Every ctor value is an OPEN-TIME constant, so freezing them at mount is the contract; the only
    /// things that change while it is open are its own signals.</summary>
    [CoreHost]
    sealed class PlayLinkCardCore(string? pinnedModuleId, string placeholder, string seed, Action close) : Component
    {
        public override Element Render()
        {
            var text = UseSignal(seed);
            var status = UseSignal("");
            // The input that FAILED — a separate signal, so a post-failure edit cannot redirect the escape hatch.
            var failedInput = UseSignal("");
            var lookup = UseAsyncCommand(cancelOnUnmount: true);     // closing the card withdraws the question
            var post = UsePost();

            bool busy = lookup.IsRunning;
            string line = busy ? Loc.Get(Strings.Play.LookingUp) : status.Value;
            bool canPlay = !busy && Actions.PlayLinkRules.CanSubmit(text.Value);

            void Submit()
            {
                if (lookup.IsRunningNow) return;
                string input = Actions.PlayLinkRules.Normalize(text.Peek());
                if (input.Length == 0) return;
                if (PlayLinkRouter.Match is not { } match) { status.Value = Loc.Get(Strings.Play.NoOwner); return; }   // in place, never a toast
                status.Value = "";
                failedInput.Value = "";        // a new attempt retires the previous failure's escape hatch
                string? pinned = pinnedModuleId;
                lookup.Restart(async ct =>
                {
                    var found = await match(input, pinned, ct).ConfigureAwait(false);
                    post(() => Matched(found));
                }, Failed);
            }

            void Matched(LinkMatch? found)
            {
                if (found is null) { status.Value = Loc.Get(Strings.Play.NoOwner); return; }
                // The line the user sees settle is the fact the card acted on — set BEFORE the play.
                status.Value = Actions.PlayLinkRules.MatchStatus(found.ModuleName, found.Title, found.IsLive, Loc.Get(Strings.Play.Live));
                found.Play();
                close();
            }

            void Failed(Exception ex)
            {
                if (Actions.PlayLinkRules.IsCancelled(ex)) return;
                status.Value = "";
                failedInput.Value = Actions.PlayLinkRules.Normalize(text.Peek());
                // ONE failed play is ONE card: the shared key collapses this with the player's generic failure card.
                Notify.Say(Actions.PlayLinkRules.ErrorText(ex, Loc.Get(Strings.Play.Failed)), InfoBarSeverity.Error,
                    Loc.Get(Strings.Play.TryAgain), Submit, Actions.PlayLinkRules.FailureToastKey);
            }

            string escape = failedInput.Value;
            return ViewSlots.View(ViewKeys.DialogPlayLink, new PlayLinkProps
            {
                Placeholder = placeholder,
                Text = () => text,
                Status = line,
                CanPlay = canPlay,
                OffersBrowser = Actions.PlayLinkRules.IsWebUrl(escape),
                OpenInBrowser = () => OpenWeb(escape),
                Cancel = close,
                Submit = Submit,
            });
        }
    }
}
