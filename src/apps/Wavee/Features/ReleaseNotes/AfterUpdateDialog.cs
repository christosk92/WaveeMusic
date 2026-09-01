using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The first-launch-after-an-update plate: what changed, in three cards, once.
///
/// <para>ONE SHOT, whatever happens next: <c>ReleaseNotesPendingFrom</c> is cleared at <see cref="Open"/> — before the
/// plate has even rendered — so a crash, a force-quit or a dismissal can never make it reappear on the next launch.
/// The same discipline the crash notice uses (<c>WaveeShell</c>): a notice about the LAST run must not survive into
/// the next one whether or not it was read.</para>
///
/// <para>A raw overlay with <see cref="PopupChrome.Modal"/>, not <c>ContentDialog</c>: the plate is 720 DIP wide and
/// ContentDialog hard-clamps its card (the <c>SetupDialog</c> precedent, for the same reason).</para></summary>
static class AfterUpdateDialog
{
    /// <summary>Set by <c>WaveeShell</c>'s crash-notice effect on every launch: did this run open with a "Wavee closed
    /// unexpectedly" toast? A launch that greets the user with a crash report must not ALSO greet them with a welcome
    /// plate — the update notice defers to the next launch instead (it is still pending; nothing is lost).</summary>
    public static bool CrashNoticeThisLaunch;

    /// <summary>Open the plate. <paramref name="fromQuad"/> is the version that ran BEFORE this launch (the raw
    /// <c>app.lastRunVersion</c> value). <paramref name="nav"/> navigates the shell — invoked for "Full release
    /// notes".</summary>
    public static OverlayHandle Open(IOverlayService overlay, IAppSettings? settings, string fromQuad,
                                     WaveeVersionInfo me, ReleaseNotesStore? store, Action<string> nav)
    {
        settings?.Set(WaveeSettings.ReleaseNotesPendingFrom, "");

        OverlayHandle? handle = null;
        void Close() => handle?.Close();

        handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new Plate(fromQuad, me, store, nav, Close)),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal)
                { ScrimVisual = true });
        return handle;
    }

    sealed class Plate(string fromQuad, WaveeVersionInfo me, ReleaseNotesStore? store, Action<string> nav, Action close)
        : Component
    {
        /// <summary>The whole plate's payload, published in ONE write when the load finishes: the document (for its
        /// tagline) and the highlight cards with their posters already resolved to disk paths.</summary>
        readonly Signal<(ReleaseNotesDocument Doc, HighlightItem[] Cards)?> _loaded = new(null);
        readonly Signal<bool> _dontShow = new(false);

        public override Element Render()
        {
            var settings = UseContext(Services.Slot)?.Settings;
            var post = UsePost();
            var loaded = _loaded.Value;                 // subscribe → the cards appear when the document lands

            UseEffect(() =>
            {
                var cts = new CancellationTokenSource();
                _ = LoadAsync(post, cts.Token);
                return () => cts.Cancel();
            }, DepKey.Empty);

            HighlightItem[] highlights = loaded?.Cards ?? [];
            var cards = new List<Element>(highlights.Length);
            for (int i = 0; i < highlights.Length; i++)
                cards.Add(HighlightCard.Compact(highlights[i])
                    with { Key = "dlg-hl:" + (highlights[i].Highlight.Id is { Length: > 0 } id ? id : i.ToString()) });

            var body = new List<Element>(3)
            {
                // Hero: the two pills say WHERE the user came from, which is the only number that makes "Updated" mean
                // anything, then the welcome line and the release's own tagline.
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Padding = new Edges4(26f, 22f, 26f, 18f),
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true,
                            Children =
                            [
                                ReleaseNotesHero.Pill(Loc.Get(Strings.WhatsNew.Dialog.Updated), accent: true),
                                ReleaseNotesHero.Pill(AppUpdateVersion.ReleaseTagVersion(fromQuad) + " → " + me.Quad, mono: true),
                            ],
                        },
                        new TextEl(Strings.WhatsNew.Dialog.Welcome(AppVersionDisplay.Of(me)))
                            { Size = 26f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new TextEl(loaded?.Doc.Tagline ?? "")
                            { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 560f },
                    ],
                },
            };

            if (cards.Count > 0)
                body.Add(new BoxEl
                {
                    Direction = 0, Gap = 10f, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
                    Padding = new Edges4(26f, 6f, 26f, 14f),
                    Children = cards.ToArray(),
                });

            body.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Padding = new Edges4(26f, 14f, 26f, 14f),
                Fill = Tok.FillLayerAlt, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
                Children =
                [
                    CheckBox.Create(Loc.Get(Strings.WhatsNew.Dialog.DontShow), _dontShow,
                        v => settings?.Set(WaveeSettings.ReleaseNotesAutoShow, !v)),
                    Spacer(),
                    Button.Standard(Loc.Get(Strings.WhatsNew.Dialog.Full), () => { close(); nav("whatsnew"); }),
                    Button.Accent(Loc.Get(Strings.WhatsNew.Dialog.GotIt), close),
                ],
            });

            return new BoxEl
            {
                Width = 720f, Direction = 1, MaxHeight = 620f,
                Corners = CornerRadius4.All(Radii.Overlay), ClipToBounds = true,
                Fill = Tok.FillSolidBase,
                Children = body.ToArray(),
            };
        }

        async System.Threading.Tasks.Task LoadAsync(Action<Action> post, CancellationToken ct)
        {
            if (store is null) return;
            try
            {
                var doc = await store.GetAsync(me.Core, ct).ConfigureAwait(false);
                if (doc is null) return;

                // Posters resolve HERE, off the UI thread, exactly like the page's own loader: the plate is opened at
                // launch, when the disk is already the busiest thing on the machine. SelectVisible applies the same
                // cap-and-channel rule as the page's strip: a Store install never sees the "get it from the Store"
                // announcement, and its slot goes to the next highlight.
                var highlights = doc.Highlights;   // never null: the store Normalize()s every document it hands out
                var visible = HighlightVisibility.SelectVisible(highlights, me.IsStore, HighlightStrip.Max);
                var cards = new HighlightItem[visible.Count];
                for (int i = 0; i < visible.Count; i++)
                    cards[i] = new HighlightItem(visible[i], doc, HighlightCard.ResolvePoster(visible[i], store, doc));

                post(() => _loaded.Value = (doc, cards));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { WaveeLog.Instance.Warn("whatsnew", "dialog notes load failed", ex); }
        }
    }
}

/// <summary>Zero-size chrome that decides, ONCE per launch, whether to raise <see cref="AfterUpdateDialog"/>.
///
/// <para>It is a component mounted INSIDE the <c>OverlayHost</c> subtree — the <c>SetupChrome</c> /
/// <c>SidebarOnboardingChrome</c> pattern — because <c>UseContext(Overlay.Service)</c> resolves the real service only
/// there; the shell's own render sits above the host and would get the null one.</para>
///
/// <para>Three deferrals, all of which leave <c>ReleaseNotesPendingFrom</c> ARMED: the setup wizard is pending OR a
/// wizard session is live (it is modal and mandatory — a re-auth or rerun wizard on a COMPLETED install is not
/// "pending", and the plate opening over it after sign-in threw the user out of setup), a crash notice opened this
/// launch, or the user turned "Show What's new after an update" off. The wizard deferral is re-evaluated on
/// <see cref="SetupSession.MarkerEpoch"/> — bumped at the end of every wizard close — so the plate still appears in
/// the SAME launch, once setup is out of the way; the other two wait for the next launch.</para></summary>
sealed class AfterUpdateChrome(IAppSettings? settings) : Component
{
    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var nav = UseContext(HistoryStore.NavCtx);
        var svc = UseContext(Services.Slot);
        // Subscribes this chrome to wizard open/close; the effect below re-runs on each bump and decides again.
        int wizardEpoch = SetupSession.MarkerEpoch.Value;

        UseEffect(() =>
        {
            if (settings is null) return;
            if (settings.Get(WaveeSettings.ReleaseNotesPendingFrom) is not { Length: > 0 } from) return;
            if (!settings.Get(WaveeSettings.ReleaseNotesAutoShow)) return;
            if (SetupGating.IsPending(settings) || SetupSession.Current is not null) return;
            if (AfterUpdateDialog.CrashNoticeThisLaunch) return;

            AfterUpdateDialog.Open(overlay, settings, from, AppVersion.Info, svc?.ReleaseNotes,
                key => nav?.Invoke(key, null));
        }, wizardEpoch);

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Shrink = 0f };
    }
}
