// ── Shell/Shell.Overlays.UI.cs ─────────────────────────────────────────────────────────────────────────────────────
// profile chip + menu + Play ▸ cascade, logout confirm, play-a-link dialog, the playback-runtime banner and its
// gate, the digital-signature dialog, teaching tips, the in-app toast decision sites, and the mount of
// +Screens/Setup.UI.Runtime.cs (the setup card's body is not here — A18)
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
//   · `LinkModules` / `MatchLink` — owner T's module host: the match-capable modules, and the router. Null router ⇒
//                                the dialog answers "Nothing installed can play this link." in place.
//   · `NotificationsLauncher` — owner I1's panel launcher, re-anchored to the chip when the ladder folds the bell.
//   · `UpdateVerb`            — the updater's verbs (Update now / Later / Retry / Open release page / Dismiss).
//   · `ReportDialogOpener`    — owner R's report dialog (`wavee://open?route=report`).
// The runtime banner reads `Setup.Runtime` (`+Screens/Setup.UI.Runtime.cs`), whose host the SHELL half installs.

using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

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

    /// <summary>One installed module that declares the `match` capability, as its `Play ▸` row shows it (labels already
    /// folded through <see cref="Actions.PlayLinkRules.MenuLabel"/> / <see cref="Actions.PlayLinkRules.PlaceholderFor"/>).</summary>
    public readonly record struct LinkModule(string Id, string MenuLabel, string Placeholder);

    /// <summary>The match-capable installed modules, in install order (owner T).</summary>
    public static Func<IReadOnlyList<LinkModule>>? LinkModules;

    /// <summary>A link some module claimed: its name, the resolved title (null when it matched on shape alone), live-ness,
    /// and the one play verb (it lights the video surface first when the module says the playable is video).</summary>
    public sealed record LinkMatch(string ModuleName, string? Title, bool IsLive, Action Play);

    /// <summary>The router: (text, pinned module id or null, token) → the match, or null when nobody owns it. A module's
    /// own failure THROWS with its own words (owner T).</summary>
    public static Func<string, string?, CancellationToken, Task<LinkMatch?>>? MatchLink;

    /// <summary>Owner I1's notification-panel launcher: (overlay, anchor thunk, the handle cell).</summary>
    public static Action<IOverlayService, Func<NodeHandle>, Ref<OverlayHandle?>>? NotificationsLauncher;

    /// <summary>The updater's verbs, raised from the update toast's single action button (What's new is handled here).</summary>
    public static Action<ToastActionKind>? UpdateVerb;

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
            // 0.2.9 `ActionServicesOverlayBinder`). A confirm-required bound action refuses to run without them.
            UseEffect(() =>
            {
                var s = Actions.Services;
                s.CanConfirm = static () => true;
                s.Confirm = request => Controls.Confirm(overlay, Loc.Get(request.TitleLocKey), Loc.Get(request.BodyLocKey),
                    Loc.Get(request.PrimaryLocKey), request.OnConfirm);
                s.Clipboard ??= text => hooks.Clipboard?.SetText(text);
                s.OpenExternal ??= url => hooks.OpenUri?.Invoke(url);
                s.Post ??= post;
                OnPlayLink ??= PlayLinkDirect;
                OnReportRequested ??= arg => ReportDialogOpener?.Invoke(overlay, arg);
                return (Action?)(() =>
                {
                    s.CanConfirm = null;
                    s.Confirm = null;
                });
            }, DepKey.Empty);

            return new BoxEl
            {
                Grow = 1f, HitTestPassThrough = true,
                Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center,
                Padding = new Edges4(0f, Setup.RuntimeRules.BannerTopPad, 0f, 0f),
                Children =
                [
                    new BoxEl { MaxWidth = Setup.RuntimeRules.BannerMaxWidth, Children = [Embed.Comp(() => new RuntimeBannerChrome())] },
                    Embed.Comp(() => new UpdateToastWatcher()),
                    Embed.Comp(() => new RuntimeToastWatcher()),
                ],
            };
        }
    }

    // ══ 4. THE PLAYBACK-RUNTIME BANNER AND ITS GATE (ch 19 W22/W23, §6.8) ═════════════════════════════════════════

    /// <summary>The banner + the setup-request watcher. Floats; never reflows the page. No motion: gated off it is a bare
    /// zero-footprint box.</summary>
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
            bool show = Setup.RuntimeRules.ShowsBanner(facts.Issue, dismissed, covering, pending);

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

            if (!show) return new BoxEl { Shrink = 0f };

            return new BoxEl
            {
                // An opaque base under the InfoBar's translucent caution tint — the banner floats over artwork, not
                // chrome — plus the overlay elevation InfoBar does not carry itself.
                Shrink = 0f, Fill = Tok.FillSolidBase, Corners = CornerRadius4.All(Radii.Control),
                Shadow = Elevation.Flyout, ClipToBounds = true,
                Children =
                [
                    // NO availableWidth: only the 60-character rule decides the orientation (noPack is the one vertical).
                    InfoBar.Create(
                        InfoBarSeverity.Warning,
                        Loc.Get(Strings.Playback.Runtime.Title),
                        Loc.Get(Setup.RuntimeRules.BannerLocKey(facts.Issue)),
                        onClose: () => Setup.Runtime.Dismiss("banner"),
                        isClosable: true,
                        actionButton: Button.Accent(Loc.Get(Strings.Playback.Runtime.SetUp), () => OpenSetup("banner"))),
                ],
            };
        }
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
                if (Setup.Runtime.WizardCovering.Peek() || pending) return;
                if (Setup.RuntimeRules.ShowsBanner(Setup.Runtime.Status.Peek().Issue,
                        Platform.Settings.Get(Platform.Keys.PlaybackRuntimeSetupDismissed), false, false)) return;
                Notify.Say(Loc.Get(Strings.Playback.Runtime.Missing), InfoBarSeverity.Warning,
                    Loc.Get(Strings.Playback.Runtime.SetUp), Setup.Runtime.RequestOpen);
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
            d.DialogWidth = Setup.RuntimeRules.SignatureDialogWidth;
            d.PrimaryText = "";
            d.SecondaryText = "";
            d.CloseText = Loc.Get(Strings.Common.Close);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = new BoxEl
            {
                Direction = 1, Gap = Spacing.M,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            InfoBadge.Icon(signature.Trust == Setup.RuntimeTrust.Trusted ? InfoBadgeSeverity.Success : InfoBadgeSeverity.Caution),
                            new TextEl(Setup.RuntimeSignatureSummary(facts))
                                { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Grow = 1f, MinWidth = 0f },
                        ],
                    },
                    new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault },
                    SignatureRow(Strings.Runtime.Sig.Publisher, signature.Subject),
                    SignatureRow(Strings.Runtime.Sig.Issuer, signature.Issuer),
                    SignatureRow(Strings.Runtime.Sig.Trust, Loc.Get(Setup.RuntimeRules.TrustLocKey(signature.Trust))),
                    SignatureRow(Strings.Runtime.Sig.Reason, signature.Reason),
                    SignatureRow(Strings.Runtime.Sig.ValidFrom, Setup.RuntimeRules.SignatureDate(signature.ValidFrom)),
                    SignatureRow(Strings.Runtime.Sig.ValidTo, Setup.RuntimeRules.SignatureDate(signature.ValidTo)),
                    SignatureRow(Strings.Runtime.Sig.Thumbprint, signature.Thumbprint),
                    SignatureRow(Strings.Runtime.Sig.Pinned, Loc.Get(facts.PinnedFingerprint ? Strings.Runtime.Sig.Yes : Strings.Runtime.Sig.No)),
                    SignatureRow(Strings.Runtime.Sig.File, signature.FilePath),
                ],
            };
        });
    }

    /// <summary>New loc key (stageB-loc/I3.json): the sheet's title, "Digital signature" — hard-coded English in 0.2.9
    /// (ch 19 §6.10); the rest of the sheet's words already had `runtime.sig.*` keys.</summary>
    const string LocSignatureTitle = "runtime.sig.title";

    /// <summary>A stacked label (11 TextSecondary) over its value (12 TextPrimary, wrapping; "-" when blank).</summary>
    static Element SignatureRow(string labelKey, string? value) => new BoxEl
    {
        Direction = 1, Gap = 2f,
        Children =
        [
            new TextEl(Loc.Get(labelKey)) { Size = 11f, LineHeight = 14f, Color = Tok.TextSecondary },
            new TextEl(string.IsNullOrWhiteSpace(value) ? "-" : value)
                { Size = 12f, LineHeight = 16f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
        ],
    };

    // ══ 5. THE UPDATE-LIFECYCLE TOAST (ch 19 §0 #11, W20; the decision is Notify.AppUpdateToasts) ═════════════════

    /// <summary>ONE card for the whole update lifecycle (dedupe key "update"): a planned toast only when the state MOVED;
    /// the download card mounted once with its bar BOUND to a float signal, so twenty progress ticks are twenty float
    /// writes, not twenty reconciles. The progress value is written BEFORE the plan early-out and before the dial check,
    /// so a card that appears mid-download starts at the right percentage.</summary>
    sealed class UpdateToastWatcher : Component
    {
        const string UpdateToastKey = "update";
        const float UpdateCardBarWidth = 240f;

        static readonly FloatSignal s_progress = new(0f);
        static AppUpdateSnapshot s_previous = AppUpdateSnapshot.Idle;

        public override Element Render()
        {
            UseEffect(() =>
            {
                var next = Notify.Update.Value;
                var previous = s_previous;
                s_previous = next;
                s_progress.Value = Math.Clamp(next.ProgressPercent / 100f, 0f, 1f);
                if (Notify.AppUpdateToasts.Plan(previous, next) is not { } plan) return;
                if (Notify.Prefs.Level(NotifyTopic.AppUpdates) == NotifyLevel.Off) return;   // a silenced dial kills it outright
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
            Toast.Show(plan.Body, new ToastOptions
            {
                Severity = plan.Severity,
                Title = title.Length > 0 ? title : null,
                DurationMs = plan.Sticky ? 0f : 5000f,
                DedupeKey = UpdateToastKey,
                // A toast shows ONE action — the first planned; the notification row renders all of them.
                ActionLabel = hasAction ? Notify.AppUpdateToasts.Label(first) : null,
                OnAction = hasAction ? () => RunUpdateAction(first, snapshot) : null,
                // The padded custom card exists only while Downloading; Installing/Failed are standard sticky InfoBars on
                // the same key, so they replace the card's body in place.
                CustomContent = downloading ? () => UpdateProgressCard(title) : null,
            });
        }

        static Element UpdateProgressCard(string title) => new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Padding = new Edges4(16f, 14f, 16f, 14f),
            Children =
            [
                new TextEl(title) { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary },
                ProgressBar.Create(s_progress, UpdateCardBarWidth),
            ],
        };
    }

    /// <summary>An update action from a toast or the panel: What's new navigates; everything else is the updater's.</summary>
    public static void RunUpdateAction(ToastActionKind kind, AppUpdateSnapshot snapshot)
    {
        if (kind != ToastActionKind.WhatsNew) { UpdateVerb?.Invoke(kind); return; }
        string version = snapshot.TargetSemVer is { Length: > 0 } semver
            ? semver
            : Notify.AppUpdateVersion.ReleaseTagVersion(snapshot.TargetQuad);
        GoTo(Parse("whatsnew", version));
    }

    // ══ 6. THE PROFILE CHIP AND ITS MENU (ch 19 W4–W7, §6.2) ═════════════════════════════════════════════════════

    /// <summary>The premium tier ink — the ONE hand-authored colour in this surface, applied to the star and the badge.
    /// A property (never a field) so a live theme flip re-resolves it.</summary>
    static ColorF PremiumInk => Theme.Dark ? ColorF.FromRgba(0xE6, 0xC2, 0x6C) : ColorF.FromRgba(0x8A, 0x63, 0x12);

    const float ProfileMenuWidth = 304f, ChipAvatar = 24f;

    sealed class ProfileChipCore : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var requestTheme = UseContext(ThemeControl.Request);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var notifyHandle = UseRef<OverlayHandle?>(null);     // a SECOND handle: the panel re-anchored here never fights the menu's

            var layout = ChromeLayout.Value;                      // subscribe: the name column + the fold rows follow the ladder
            _ = Entities.Current.Users.Changed.Value;             // subscribe: the account's identity lands
            var identity = ReadIdentity();
            float nameCap = Actions.ProfileRules.NameCap(Layout.ChromeProfileNameW);

            // The skeleton chip until the account's identity is KNOWN (0.2.9 printed "—"; 0.3 must not).
            Element picture = identity.Known
                ? PersonPicture.Create("", ChipAvatar, displayName: identity.Name, imageSourcePath: identity.AvatarUrl)
                : new BoxEl { Width = ChipAvatar, Height = ChipAvatar, Shrink = 0f, Corners = CornerRadius4.All(ChipAvatar / 2f), Fill = Tok.FillSubtleSecondary };

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
                    () => ProfileMenuContent(peekIdentity, tier, unread, actionsInMenu, overlay, requestTheme, Close,
                        () => NotificationsLauncher?.Invoke(overlay, () => anchor.Value, notifyHandle)),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    // MENU chrome, not flyout-presenter chrome: the anchored 250 ms MenuPopupThemeTransition unfold over a
                    // windowed DWM transient-acrylic popup — like every other menu in the shell.
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Flyout)
                    {
                        ConstrainToRootBounds = false,
                    });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            Element[] children = !layout.ShowName
                ? [picture]
                : identity.Known
                    ? [picture, Caption(identity.Name).Primary() with { MaxWidth = nameCap, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                    : [picture, new BoxEl { Width = nameCap, Height = 12f, Corners = CornerRadius4.All(2f), Fill = Tok.FillSubtleSecondary }];

            return new BoxEl
            {
                Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Height = 32f, Shrink = 0f,
                // The named form carries 6 extra DIP of right padding — part of the budgeted ChromeProfileNameW.
                Padding = new Edges4(4f, 0f, layout.ShowName ? 10f : 4f, 0f), Corners = CornerRadius4.All(Radii.Control),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = OpenMenu, OnRealized = h => anchor.Value = h,
                Children = children,
            }.Interactive(Interaction.Subtle);
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

    /// <summary>The account flyout: the header over stock WinUI menu rows, in the row table's order.</summary>
    static Element ProfileMenuContent(AccountIdentity identity, Spotify.Tier tier, int unread, bool actionsInMenu,
        IOverlayService overlay, Action<float>? requestTheme, Action close, Action openNotifications)
    {
        var table = Actions.ProfileRules.Rows(PickAndPlayFile is not null, actionsInMenu, NotificationsLauncher is not null);
        bool dark = Theme.Dark;
        var items = new List<MenuFlyoutItem>(table.Length);
        for (int i = 0; i < table.Length; i++)
        {
            // Every row closes the menu FIRST, then acts (a modal opened over a closing menu would lose its focus restore).
            switch (table[i])
            {
                case Actions.ProfileRow.Account:
                    items.Add(new MenuFlyoutItem(Loc.Get(Strings.Auth.Account), Icons.Contact,
                        Invoke: () => { close(); OpenWeb(AccountUrl); }));
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
                        light ? Icons.Sun : Icons.Moon, Invoke: () => { close(); ToggleThemeFromMenu(requestTheme); }));
                    break;
                case Actions.ProfileRow.LogOut:
                    items.Add(new MenuFlyoutItem(Loc.Get(Strings.Auth.LogOut), Icons.SignOut,
                        Invoke: () => { close(); ConfirmLogout(overlay); }));
                    break;
            }
        }

        return new BoxEl
        {
            Direction = 1, MinWidth = ProfileMenuWidth, MaxWidth = ProfileMenuWidth,
            // 6 + the menu presenter's own (0,2,0,2) = the 8-DIP inset this card has always had.
            Padding = new Edges4(0f, 6f, 0f, 6f),
            Children =
            [
                AccountHeader(identity, tier),
                new BoxEl { Height = 1f, Margin = new Edges4(8f, 4f, 8f, 4f), Fill = Tok.StrokeDividerDefault },
                MenuFlyout.Create(items, close, ProfileMenuWidth),
            ],
        };
    }

    const string AccountUrl = "https://www.spotify.com/account";

    /// <summary>A 40-DIP avatar beside name (14/600) · tier line · (email — 0.3 carries no email column, so the line is
    /// omitted, which is also what 0.2.9 did when it had none). Unknown identity keeps the header's height with quiet
    /// bars; an unknown tier hides its row rather than printing "Spotify Free" speculatively.</summary>
    static Element AccountHeader(AccountIdentity identity, Spotify.Tier tier)
    {
        var lines = new List<Element>(2)
        {
            identity.Known
                ? new TextEl(identity.Name) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                : new BoxEl { Width = 140f, Height = 14f, Margin = new Edges4(0f, 3f, 0f, 3f), Corners = CornerRadius4.All(2f), Fill = Tok.FillSubtleSecondary },
        };
        if (Actions.ProfileRules.ShowsTier(tier)) lines.Add(TierLine(tier == Spotify.Tier.Premium));

        return new BoxEl
        {
            Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Padding = new Edges4(14f, 10f, 14f, 10f),
            Children =
            [
                identity.Known
                    ? PersonPicture.Create("", 40f, displayName: identity.Name, imageSourcePath: identity.AvatarUrl)
                    : new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), Fill = Tok.FillSubtleSecondary },
                new BoxEl { Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true, Children = lines.ToArray() },
            ],
        };
    }

    /// <summary>A 10-DIP star (or an invisible 10-DIP spacer for Free, so both badges share one baseline) + the badge at 12.</summary>
    static Element TierLine(bool premium)
    {
        ColorF ink = premium ? PremiumInk : Tok.TextSecondary;
        return new BoxEl
        {
            Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center,
            Children =
            [
                premium ? Icon(Icons.FavoriteStar, 10f, ink) : new BoxEl { Width = 10f, Shrink = 0f },
                new TextEl(Loc.Get(premium ? Strings.Auth.PremiumBadge : Strings.Auth.FreeBadge)) { Size = 12f, LineHeight = 16f, Color = ink },
            ],
        };
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
        if (LinkModules?.Invoke() is { Count: > 0 } modules)
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

    /// <summary>The menu's theme row: flip to the other palette, persist the explicit choice, and ask the host for the
    /// animated in-place re-theme.</summary>
    static void ToggleThemeFromMenu(Action<float>? requestTheme)
    {
        var next = Theme.Dark ? ThemeKind.Light : ThemeKind.Dark;
        if (SeedPalette is { } seed) seed(next); else Tok.Use(next);
        Platform.Settings.Set(Platform.Keys.ThemeMode, next == ThemeKind.Dark ? 2 : 1);
        requestTheme?.Invoke(Design.Motion.Standard);
    }

    /// <summary>Open a web url in the user's browser — only an http(s) url with a host ever reaches the shell.</summary>
    static void OpenWeb(string url)
    {
        if (!Actions.PlayLinkRules.IsWebUrl(url)) return;
        if (Actions.Services.OpenExternal is { } open) { open(url); return; }
        InputHooks.Current.Default.OpenUri?.Invoke(url);
    }

    // ══ 7. THE LOGOUT CONFIRM (ch 19 W8 — off-ladder on purpose) ══════════════════════════════════════════════════

    /// <summary>A bare Modal card, NOT a ContentDialog: no title band, no separator, no command-row fill — 380 wide
    /// (320..420), padded 24, Cancel / Log out both 96 wide, right-aligned.</summary>
    static void ConfirmLogout(IOverlayService overlay)
    {
        OverlayHandle? h = null;
        h = overlay.Open(
            () => NodeHandle.Null,
            () => ModalCard(380f, 320f, 420f,
            [
                new TextEl(Loc.Get(Strings.Auth.LogoutConfirmTitle)) { Size = 20f, LineHeight = 28f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                new TextEl(Loc.Get(Strings.Auth.LogoutConfirmBody)) { Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Margin = new Edges4(0f, Spacing.M, 0f, 0f),
                    Children =
                    [
                        Button.Standard(Loc.Get(Strings.Auth.Cancel), () => h?.Close()) with { MinWidth = 96f },
                        Button.Accent(Loc.Get(Strings.Auth.LogOut), () =>
                        {
                            h?.Close();
                            Log.Info("ui", "profile.logout.confirmed");
                            Spotify.Logout();
                        }) with { MinWidth = 96f },
                    ],
                },
            ]),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
    }

    /// <summary>The off-ladder modal plate both this chapter's hand-rolled cards share: FillSolidBase, 1px surface
    /// stroke, the dialog elevation, r8, padded 24 with a 12 gap.</summary>
    static Element ModalCard(float width, float min, float max, Element[] children) => new BoxEl
    {
        Direction = 1, Width = width, MinWidth = min, MaxWidth = max,
        Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
        Shadow = Elevation.Dialog, Padding = Edges4.All(24f), Gap = Spacing.M,
        Children = children,
    };

    // ══ 8. THE PLAY-A-LINK DIALOG (ch 19 W9–W13, §6.7) ════════════════════════════════════════════════════════════

    const float PlayLinkCardWidth = 420f;
    const float PlayLinkStatusHeight = 18f;

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

    /// <summary>`wavee://play?link=` — the same router, play verb and failure sentences as the card, with no status line:
    /// a miss is an Informational toast, a failure the shared-key Error toast with "Try again".</summary>
    public static void PlayLinkDirect(string link)
    {
        string input = Actions.PlayLinkRules.Normalize(link);
        if (!Actions.PlayLinkRules.CanSubmit(input)) return;
        if (MatchLink is not { } match)
        {
            Notify.Say(Loc.Get(Strings.Play.NoOwner), InfoBarSeverity.Informational);
            return;
        }
        Action<Action> post = Actions.Services.Post ?? (static a => a());
        _ = Task.Run(async () =>
        {
            try
            {
                var found = await match(input, null, CancellationToken.None).ConfigureAwait(false);
                post(() =>
                {
                    if (found is null) { Notify.Say(Loc.Get(Strings.Play.NoOwner), InfoBarSeverity.Informational); return; }
                    found.Play();
                });
            }
            catch (Exception ex)
            {
                if (Actions.PlayLinkRules.IsCancelled(ex)) return;
                post(() => Notify.Say(Actions.PlayLinkRules.ErrorText(ex, Loc.Get(Strings.Play.Failed)), InfoBarSeverity.Error,
                    Loc.Get(Strings.Play.TryAgain), () => PlayLinkDirect(link), Actions.PlayLinkRules.FailureToastKey));
            }
        });
    }

    /// <summary>The card. Every ctor value is an OPEN-TIME constant, so freezing them at mount is the contract; the only
    /// things that change while it is open are its own signals.</summary>
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
                if (MatchLink is not { } match) { status.Value = Loc.Get(Strings.Play.NoOwner); return; }   // in place, never a toast
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
            var buttons = new List<Element>(3);
            if (Actions.PlayLinkRules.IsWebUrl(escape))
                buttons.Add(Button.Standard(Loc.Get(Strings.Play.OpenInBrowser), () => OpenWeb(escape)));
            buttons.Add(Button.Standard(Loc.Get(Strings.Auth.Cancel), close) with { MinWidth = 96f });
            buttons.Add(Button.Accent(Loc.Get(Strings.Play.Start), Submit, isEnabled: canPlay) with { MinWidth = 96f });

            return ModalCard(PlayLinkCardWidth, 360f, 480f,
            [
                new TextEl(Loc.Get(Strings.Play.Title)) { Size = 20f, LineHeight = 28f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                // Enter commits: the single-line field raises OnCommit, so the card needs no key handler of its own.
                TextBox.Create(text, null, new TextBox.TextBoxOptions
                {
                    Placeholder = placeholder, Width = PlayLinkCardWidth - 48f, OnCommit = _ => Submit(),
                }),
                // 18 DIP reserved even when empty, so the button row never jumps when a status appears.
                new BoxEl
                {
                    MinHeight = PlayLinkStatusHeight, Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [new TextEl(line) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                },
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Margin = new Edges4(0f, Spacing.S, 0f, 0f),
                    Children = buttons.ToArray(),
                },
            ]);
        }
    }

    // ══ 9. TEACHING TIPS (A10 — the presenter; the rules are TipsCore, the persisted set is Tips) ═══════════════════

    static OverlayHandle? s_tipHandle;

    /// <summary>Arm tip <paramref name="tipId"/> against <paramref name="anchor"/>, scheduled for after the next PAINTED
    /// frame. Returns true when the gate passed. Safe to call every render: the per-launch latch makes repeats no-ops.
    /// <para>ONE tip at a time, once per launch, never again once acknowledged; it never scrims and never steals focus;
    /// a double post lets the page paint first; the anchor must still be live AND not parked when it opens; an inert
    /// handle (a host-less mount) frees the slot at once. The ✕ IS "don't show again".</para></summary>
    public static bool TryShowTip(IOverlayService? overlay, Action<Action>? post, string tipId, Func<NodeHandle> anchor,
        Func<SceneStore?> scene, string titleKey, string bodyKey,
        TeachingTip.PlacementMode placement = TeachingTip.PlacementMode.Bottom)
    {
        // A tip whose acknowledgement cannot be persisted or that has nowhere to draw is never shown (TipsCore: canPresent).
        if (overlay is not { } host || post is not { } schedule) return false;
        if (!Tips.Arm(tipId, canPresent: true)) return false;

        schedule(() => schedule(() =>
        {
            if (!string.Equals(Tips.Active.Peek(), tipId, StringComparison.Ordinal) || s_tipHandle is not null) return;
            var node = anchor();
            var sc = scene();
            if (sc is null || node.IsNull || !sc.IsLive(node) || (sc.Flags(node) & NodeFlags.Parked) != 0)
            {
                Tips.Dismiss(tipId);
                return;
            }
            var handle = TeachingTip.Show(host, anchor, tip =>
            {
                tip.Title = Loc.Get(titleKey);
                tip.Subtitle = Loc.Get(bodyKey);
                tip.PreferredPlacement = placement;
                tip.IsLightDismissEnabled = false;
                tip.CloseButtonClick = () => Tips.Acknowledge(tipId);
            });
            if (!handle.IsOpen) { Tips.Dismiss(tipId); return; }
            s_tipHandle = handle;
            // Every close path — the ✕, CloseTip, the host's orphaned-anchor prune — releases the one slot.
            handle.ClosedAction = () =>
            {
                if (ReferenceEquals(s_tipHandle, handle)) s_tipHandle = null;
                Tips.Dismiss(tipId);
            };
        }));
        return true;
    }

    /// <summary>Take the tip down WITHOUT acknowledging it (navigation away, the anchor evicted).</summary>
    public static void CloseTip(string tipId)
    {
        if (!string.Equals(Tips.Active.Peek(), tipId, StringComparison.Ordinal)) return;
        var handle = s_tipHandle;
        s_tipHandle = null;
        Tips.Dismiss(tipId);
        handle?.Close();
    }

    /// <summary>The user used the taught affordance: acknowledge (durable) and take the tip down.</summary>
    public static void AcknowledgeTip(string tipId)
    {
        if (!string.Equals(Tips.Active.Peek(), tipId, StringComparison.Ordinal)) return;
        var handle = s_tipHandle;
        s_tipHandle = null;
        Tips.Acknowledge(tipId);
        handle?.Close();
    }
}
