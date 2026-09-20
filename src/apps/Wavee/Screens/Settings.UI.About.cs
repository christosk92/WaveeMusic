// ── Screens/Settings.UI.About.cs ───────────────────────────────────────────────────────────────────────────────────
// the About tab: the update panel (the version hero + the W20 state matrix, the status card, channel, install-on-quit,
// metered download, what's-new auto-show — or the Store card), the links card, the crash-reports card mount, the "Wavee
// right now" receipts (the GPU line, a 5 s interval on a child), the two license expanders
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 350 lines
// Spec: ch 27 §0 N12, W19, W20, §3 (About rows), §5 (receipts tick, update progress), §7 (+ G8-G10, G12), §8, §10 parity
// 50-56; ch 28 §1.4 (CrashReportsCard) and §6.4 (the report entry points)
//
// Everything with LIVE state is an embedded child component (AboutTab is called behind the page's tab switch, so a hook
// in it would be conditional): `AboutUpdatePanel` reads `Notify.Update`, `ReceiptsCard` owns the interval.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. THE TAB ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Order: update panel, links, crash reports (beside "Report a problem"), the receipts, Licenses.</summary>
    private static partial Element AboutTab() => TabStack(
        Embed.Comp(static () => new AboutUpdatePanel()) with { Key = "about:update" },
        AboutLinksCard(),
        Feedback.CrashReportsCard(),
        SectionHeader(Loc.Get(Strings.Settings.About.RightNow), Icons.Info),
        Embed.Comp(static () => new ReceiptsCard()),
        SectionHeader(Loc.Get(Strings.Settings.About.Licenses), Icons.Document),
        LicenseExpander("Wavee", "MIT", WaveeLicense),
        // ONE expander for everything third-party, read from the generated file rather than restated in code.
        LicenseExpander(Loc.Get(Strings.Settings.About.ThirdPartyNotices), NoticesFileName, ReadNotices()));

    /// <summary>W19's links card: ContentAlignment.Left (no header, glyph or chevron at any width), the link column
    /// pulled flush to the card padding. "Open What's new" carries the unread dot while the running release's notes
    /// have never been opened — the ONLY unread affordance outside the notification centre.</summary>
    static Element AboutLinksCard()
    {
        Element whatsNew = HyperlinkButton.Create(Loc.Get(Strings.Update.About.OpenWhatsNew), static () => OpenWhatsNew());
        if (ReleaseNotes.RunningNotesUnread())
            whatsNew = new BoxEl { Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Children = [whatsNew, InfoBadge.Dot()] };

        return SettingsCard.Create(new SettingsCard.Options
        {
            Alignment = SettingsCard.ContentAlignment.Left,
            Content = new BoxEl
            {
                Direction = 1, Gap = 4f, Margin = new Edges4(-12f, 0f, 0f, 0f),
                Children =
                [
                    whatsNew,
                    HyperlinkButton.Create(Loc.Get(Strings.Report.AboutReportProblem), static () => Feedback.Open(Feedback.ReportKind.Bug)),
                    HyperlinkButton.Create(Loc.Get(Strings.Report.AboutSuggestFeature), static () => Feedback.Open(Feedback.ReportKind.Feature)),
                    HyperlinkButton.Create(Loc.Get(Strings.Report.AboutAllIssues), IssuesUrl),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.Website), WebsiteUrl),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.PrivacyPolicy), PrivacyUrl),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.ThirdPartyNotices), static () => OpenNotices()),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.CopyDiagnostics), static () => CopyDiagnostics()),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.OpenDataFolder), static () => OpenFolder(Platform.LocalFolder)),
                    new TextEl(Loc.Get(Strings.Settings.About.Unofficial)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    new TextEl(OsDescription) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    new TextEl(Platform.LocalFolder) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                ],
            },
        });
    }

    /// <summary>A license: a collapsed expander whose one item is the body in 12-DIP Cascadia tertiary, with a flat
    /// never-wrapping item card (Pad 16,12,16,16 · MinH 0 · r 0). The style is built per call: it reads theme tokens.</summary>
    static Element LicenseExpander(string name, string kind, string body)
    {
        var style = new SettingsExpander.Style
        {
            ItemCardStyle = SettingsCard.DefaultStyle with
            {
                Padding = new Edges4(16f, 12f, 16f, 16f), MinHeight = 0f, CornerRadius = 0f, WrapThreshold = 0f, WrapNoIconThreshold = 0f,
            },
        };
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = name,
            Description = kind,
            Style = style,
            Items =
            [
                SettingsExpander.Item("", null,
                    new TextEl(body) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                    alignment: SettingsCard.ContentAlignment.Left, style: style),
            ],
        });
    }

    // ══ 2. THE UPDATE PANEL (W19 hero + cards, W20 matrix) ═════════════════════════════════════════════════════════

    /// <summary>Re-renders on every published snapshot (it reads <see cref="Notify.Update"/>). A Store install has no
    /// feed, channel, install-on-quit or metered gate — the Store owns all four — so one card replaces them.</summary>
    sealed class AboutUpdatePanel : Component
    {
        public override Element Render()
        {
            var s = Notify.Update.Value;
            var me = Platform.Version;
            var kids = new List<Element>(6) { UpdateHero(me, s) };
            if (me.IsStore)
            {
                kids.Add(Row(Loc.Get(Strings.Update.Store.Title), Loc.Get(Strings.Update.Store.Hint),
                    Button.Create(Loc.Get(Strings.Update.Store.Open), static () => RunUpdate(UpdateRowAction.UpdateNow),
                        ButtonAppearance.Standard, ControlSize.Small), Icons.Refresh));
            }
            else
            {
                kids.Add(UpdateStatusCard(s));
                // A LINK, not a picker: Beta installs beside Stable, nothing in this app changes when it is clicked.
                kids.Add(Row(Loc.Get(Strings.Update.Channel.Title), Loc.Get(Strings.Update.Channel.Hint),
                    HyperlinkButton.Create(Loc.Get(Strings.Update.Channel.Beta), BetaFeedUrl), Icons.Devices));
                kids.Add(Row(Loc.Get(Strings.Update.InstallOnQuit.Title), Loc.Get(Strings.Update.InstallOnQuit.Hint),
                    Toggle(Platform.Keys.UpdateInstallOnQuit), Icons.Download));
                kids.Add(Row(Loc.Get(Strings.Update.Metered.Title), Loc.Get(Strings.Update.Metered.Hint),
                    Toggle(Platform.Keys.UpdateOnMetered), Icons.RadioTower));
            }
            kids.Add(Row(Loc.Get(Strings.Update.AutoShow.Title), Loc.Get(Strings.Update.AutoShow.Hint),
                Toggle(Platform.Keys.ReleaseNotesAutoShow), Icons.RefineSparkle));
            return new BoxEl { Direction = 1, Gap = 4f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
        }
    }

    /// <summary>The hero: a 64² accent-subtle tile, the 24/600 version line, a wrapping pill row (quad MONO · channel ·
    /// state) + the commit/arch stamp, the provenance line; right column = the primary button + up to two links.</summary>
    static Element UpdateHero(WaveeVersionInfo me, AppUpdateSnapshot s)
    {
        // A dev build has nothing for the feed to be newer than — unless a developer-mode SIMULATION is walking, whose
        // states are real enough to press.
        var shape = AboutRules.Hero(s.State, me.IsDev && !Update.Host.IsSimulating, me.IsStore);
        var pills = new List<Element>(4);
        if (me.Quad.Length > 0) pills.Add(ReleaseNotes.Pill(me.Quad, mono: true));
        pills.Add(ReleaseNotes.Pill(Loc.Get(me.Channel switch
        {
            "beta" => Strings.Update.About.ChannelBeta,
            "stable" => Strings.Update.About.ChannelStable,
            _ => Strings.Update.About.ChannelDev,
        })));
        pills.Add(ReleaseNotes.Pill(Loc.Get(shape.PillKey), accent: shape.PillAccent));
        pills.Add(new TextEl((me.Commit.Length > 0 ? me.Commit + " · " : "") + ArchToken)
            { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" });

        Action act = shape.Verb switch
        {
            AboutRules.HeroVerb.Check => static () => Update.Host.CheckNow(),
            AboutRules.HeroVerb.UpdateNow => static () => RunUpdate(UpdateRowAction.UpdateNow),
            AboutRules.HeroVerb.Retry => static () => RunUpdate(UpdateRowAction.Retry),
            _ => static () => { },
        };
        var right = new List<Element>(3)
        {
            Button.Create(Loc.Get(shape.ButtonKey), act,
                shape.Tone == AboutRules.HeroTone.Accent ? ButtonAppearance.Accent : ButtonAppearance.Standard,
                ControlSize.Medium, isEnabled: shape.Tone != AboutRules.HeroTone.Disabled),
        };
        if (me.Codename.Length > 0)
            right.Add(HyperlinkButton.Create(Strings.Update.About.WhatsNewIn(me.Codename), static () => OpenWhatsNew()));
        // The after-update plate on demand; a dev build has no update story to summarize.
        if (s_overlay is { } overlay && ReleaseNotes.CanShowSummaryAgain)
            right.Add(HyperlinkButton.Create(Loc.Get(Strings.Update.About.ShowSummaryAgain), () => ReleaseNotes.ShowSummaryAgain(overlay, s_post)));

        long lastChecked = s.LastCheckedMs > 0 ? s.LastCheckedMs : Platform.Settings.Get(Platform.Keys.UpdateLastCheckedMs);
        string provenance = AboutRules.Provenance(me.BuildDate, lastChecked, static d => Strings.Update.About.Built(d),
            static w => Strings.Update.About.LastChecked(w), Loc.Get(Strings.Update.About.NeverChecked));

        return new BoxEl
        {
            Direction = 0, Gap = 18f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = new Edges4(24f, 22f, 24f, 22f), Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Width = 64f, Height = 64f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(16f), Fill = Tok.AccentSubtle,
                    Children = [Icon(Icons.MusicNote, 30f, Tok.AccentTextPrimary)],
                },
                new BoxEl
                {
                    Direction = 1, Gap = 6f, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(ReleaseNotes.VersionDisplayText()) { Size = 24f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = pills.ToArray() },
                        new TextEl(provenance) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    ],
                },
                new BoxEl { Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.End, Children = right.ToArray() },
            ],
        };
    }

    /// <summary>The status card: the ONE state sentence (`Notify.AppUpdateToasts.StateSentence`), plus progress while
    /// downloading/installing, "Open release page" on a failure, "Dismiss" after an update.</summary>
    static Element UpdateStatusCard(AppUpdateSnapshot s)
    {
        Element? content = s.State switch
        {
            AppUpdateState.Downloading or AppUpdateState.Installing => new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    ProgressBar.Determinate(s.ProgressPercent / 100f, 180f),
                    new TextEl(s.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%")
                        { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
                ],
            },
            // The release that FAILED, not the bare listing (`Update.ReleasePageUrl` is the one owner of that rule).
            AppUpdateState.Failed => Button.Create(Loc.Get(Strings.Update.Action.OpenReleasePage),
                static () => RunUpdate(UpdateRowAction.OpenReleasePage), ButtonAppearance.Standard, ControlSize.Small),
            AppUpdateState.Completed => Button.Create(Loc.Get(Strings.Update.Action.Dismiss),
                static () => RunUpdate(UpdateRowAction.Dismiss), ButtonAppearance.Subtle, ControlSize.Small),
            _ => null,
        };
        return Row(Loc.Get(Strings.Update.Status.Title), Notify.AppUpdateToasts.StateSentence(s), content, Icons.Refresh);
    }

    // ══ 3. "WAVEE RIGHT NOW" (N12: a 5 000 ms interval on THIS child; Render never reads process/GPU/FPS) ═════════

    sealed class ReceiptsCard : Component
    {
        readonly Signal<string> _gpu = new(Receipts.Dash), _workingSet = new(Receipts.Dash), _managed = new(Receipts.Dash),
            _uptime = new(Receipts.Dash), _fps = new(Receipts.Dash), _zoom = new(Receipts.Dash), _gpuAssets = new(Receipts.Dash),
            _appExcl = new(Receipts.Dash), _detail = new(Receipts.Dash);

        public override Element Render()
        {
            UseEffect(Tick, DepKey.Empty);
            UseInterval(Tick, 5000f);
            return SettingsCard.Create(new SettingsCard.Options
            {
                Alignment = SettingsCard.ContentAlignment.Left,
                Content = new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS,
                    Children =
                    [
                        Line(_gpu, "GPU"), Line(_workingSet, "Working set"), Line(_managed, "Managed heap"), Line(_uptime, "Uptime"),
                        Line(_fps, "FPS"), Line(_zoom, "Zoom"), Line(_gpuAssets, "GPU assets"),
                        Line(_appExcl, "App memory excl. GPU assets"),
                        new TextEl(_detail) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    ],
                },
            });
        }

        static Element Line(Signal<string> value, string label) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Shrink = 0f },
                new TextEl(value)
                {
                    Size = global::Wavee.Design.Type.DenseTitle("").Size,
                    LineHeight = global::Wavee.Design.Type.DenseTitle("").LineHeight,
                    Weight = global::Wavee.Design.Type.DenseTitle("").ResolvedWeight,
                    Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap,
                },
            ],
        };

        void Tick()
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            long ws = proc.WorkingSet64;
            var tier = ReceiptsTier(GpuProfile.Tier);
            var snap = D3D12Device.LastVideoMemory;

            _gpu.Value = ReceiptsGpuLine();
            _workingSet.Value = Receipts.Mb(ws);
            _managed.Value = Receipts.Mb(GC.GetTotalMemory(forceFullCollection: false));
            _uptime.Value = Receipts.Uptime(DateTime.Now - proc.StartTime);
            _fps.Value = Receipts.Fps(Diagnostics.Probe.Host?.LastStats.Fps ?? 0);
            // The tick is plenty for a receipt (a fresh Ctrl+= shows within 5 s) — no zoom subscription to unhook.
            _zoom.Value = ZoomLadder.Percent(FluentApp.Zoom).ToString(CultureInfo.InvariantCulture) + "%";

            if (!snap.Valid)
            {
                _gpuAssets.Value = "— (no Present yet)";
                _appExcl.Value = Receipts.Dash;
                _detail.Value = "GPU video-memory snapshot publishes on the render thread after the first Present.";
            }
            else
            {
                bool shared = Receipts.IsSharedIgpu(tier, snap.LocalCurrentUsage, snap.NonLocalCurrentUsage);
                _gpuAssets.Value = Receipts.Mb((long)snap.LocalCurrentUsage) + " local  ·  " + Receipts.Mb((long)snap.NonLocalCurrentUsage) + " non-local";
                _appExcl.Value = Receipts.Mb(Receipts.AppExclusive(ws, shared, snap.LocalCurrentUsage, snap.NonLocalCurrentUsage))
                    + "  (" + (shared ? "shared / iGPU" : "discrete") + ")";
                var c = CultureInfo.InvariantCulture;
                _detail.Value = "Local budget " + Receipts.Mb((long)snap.LocalBudget) + "  ·  non-local budget " + Receipts.Mb((long)snap.NonLocalBudget)
                    + "  ·  tracked D3D12 " + Receipts.Mb(snap.TrackedResourceBytes) + " (" + snap.TrackedResourceCount.ToString(c) + ")"
                    + "  ·  atlas " + snap.AtlasImages.ToString(c) + "/" + snap.AtlasPages.ToString(c)
                    + "  ·  glyphs " + snap.CachedGlyphs.ToString(c) + ". App excl. GPU ≈ working set − "
                    + (shared ? "LOCAL (UMA/shared)" : "NON_LOCAL (system-memory overlap)") + ".";
            }

            ReceiptsText = "Working set: " + _workingSet.Peek() + "\nManaged heap: " + _managed.Peek() + "\nUptime: " + _uptime.Peek()
                + "\nFPS: " + _fps.Peek() + "\nZoom: " + _zoom.Peek() + "\nGPU assets: " + _gpuAssets.Peek()
                + "\nApp memory excl. GPU assets: " + _appExcl.Peek() + "\n" + _detail.Peek();
        }
    }
}
