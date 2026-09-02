using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu;   // FluentApp (the live app-zoom read for the receipts)
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
    const string FeedbackUrl = "https://github.com/christosk92/WaveeMusic/issues";
    const string WebsiteUrl = "https://github.com/christosk92/WaveeMusic";
    const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";

    /// <summary>The generated notices file, staged next to Wavee.exe by ops/build/generate-third-party-notices.ps1
    /// (called from both publish-wavee-aot.ps1 and pack-wavee-msix.ps1). A plain `dotnet run` has no such file, which
    /// is exactly what <c>Strings.Settings.About.NoticesMissing</c> says.</summary>
    const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    static string NoticesPath => Path.Combine(AppContext.BaseDirectory, NoticesFileName);

    /// <summary>The machine's architecture as the feed names it ("arm64" / "x64") — the same token the pack script
    /// stamps into the .appinstaller file names.</summary>
    internal static string ArchToken => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        var other => other.ToString().ToLowerInvariant(),
    };

    internal static string OsDescription => RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")";

    /// <summary>Wavee's OWN license. Everything else — every package, every vendored component — is enumerated by the
    /// generated notices file rather than by a hand-maintained list here: a list in code drifts the moment a
    /// PackageReference changes, and the drift is invisible until someone audits it.</summary>
    static readonly (string Name, string Kind, string Body)[] s_licenses =
    [
        ("Wavee", "MIT",
            "Copyright (c) 2026 Christos Karapasias\n\n" +
            "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated " +
            "documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation " +
            "the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and " +
            "to permit persons to whom the Software is furnished to do so, subject to the following conditions:\n\n" +
            "The above copyright notice and this permission notice shall be included in all copies or substantial portions of " +
            "the Software.\n\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO " +
            "THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE " +
            "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF " +
            "CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER " +
            "DEALINGS IN THE SOFTWARE."),
    ];

    static SettingsExpander.Style LicenseExpanderStyle => new()
    {
        ItemCardStyle = SettingsCard.DefaultStyle with
        {
            Padding = new Edges4(16f, 12f, 16f, 16f),
            MinHeight = 0f,
            CornerRadius = 0f,
            WrapThreshold = 0f,
            WrapNoIconThreshold = 0f,
        },
    };

    /// <summary>The "Copy diagnostics" text — hoisted to <c>internal static</c> (was a local closure of
    /// <see cref="AboutTab"/>) so <c>ReportComposer.Compose</c> can build the SAME diagnostics block for a report,
    /// off the UI thread, without a component in scope. Callable with any <paramref name="svc"/> including null
    /// (the pre-login / no-services shell state), matching every other read in here.</summary>
    internal static string DiagInfoText(Services? svc)
    {
        var me = AppVersion.Info;
        string os = OsDescription;
        string dotnet = ".NET " + Environment.Version;
        return $"{me.OneLine(os, ArchToken)}\nOS: {os}\nEngine: FluentGpu · {dotnet}\nGPU: {WaveeNowReceipts.GpuSummary()}\nData folder: {SettingsShared.AppDataRoot}\n" +
            $"Feed: {(svc?.AppUpdate.FeedUrl is { Length: > 0 } f ? f : "—")}\n" +
            $"Playback runtime: {(svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable).Outcome}\n" +
            WaveeNowReceipts.LastCopyText;
    }

    /// <summary>The About tab. Everything that has LIVE state (the update snapshot, the three persisted switches) is
    /// inside <see cref="AboutUpdatePanel"/>, an embedded component — never a hook in this method, which the tab switch
    /// calls conditionally.
    ///
    /// <para>Order: version hero + update panel, links, Reports (crash reports — moved here from the old Diagnostics
    /// tab; it sits next to "Report a problem"), the "Wavee right now" receipts, Licenses. Graphics moved OUT of this
    /// tab to General (Workstream B, "Settings regroup + removals") — a render-adapter picker is not "about" the app.
    /// </para></summary>
    Element AboutTab(Services? svc, InputHooks hooks)
    {
        string os = OsDescription;
        string DiagInfo() => DiagInfoText(svc);

        var kids = new List<Element>
        {
            Embed.Comp(() => new AboutUpdatePanel()) with { Key = "about:update" },
            AboutLinksCard(svc, hooks, DiagInfo, os),
            Embed.Comp(() => new CrashReportsCard()),
            SettingsSectionHeader(Loc.Get(Strings.Settings.About.RightNow), Icons.Info),
            Embed.Comp(() => new WaveeNowReceipts()),
            SettingsSectionHeader(Loc.Get(Strings.Settings.About.Licenses), Icons.Document),
        };
        kids.AddRange(LicenseExpanders());
        return SettingsTabStack(kids.ToArray());
    }

    Element AboutLinksCard(Services? svc, InputHooks hooks, Func<string> diagInfo, string os)
    {
        var me = AppVersion.Info;
        bool unread = !string.Equals(svc?.Settings.Get(WaveeSettings.ReleaseNotesLastSeen) ?? "", me.Core, StringComparison.Ordinal);
        var nav = _nav;

        // "Open What's new" carries an InfoBadge dot while the running release's notes have never been opened. The dot
        // is the ONLY unread affordance outside the notification centre, and it clears the moment the page mounts.
        Element whatsNew = unread
            ? new BoxEl
            {
                Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
                Children =
                [
                    HyperlinkButton.Create(Loc.Get(Strings.Update.About.OpenWhatsNew), () => nav?.Invoke("whatsnew", null)),
                    InfoBadge.Dot(),
                ],
            }
            : HyperlinkButton.Create(Loc.Get(Strings.Update.About.OpenWhatsNew), () => nav?.Invoke("whatsnew", null));

        return SettingsCard.Create(new SettingsCard.Options
        {
            Alignment = SettingsCard.ContentAlignment.Left,
            Content = new BoxEl
            {
                Direction = 1, Gap = 4f, Margin = new Edges4(-12f, 0f, 0f, 0f),
                Children =
                [
                    whatsNew,
                    HyperlinkButton.Create(Loc.Get(Strings.Report.AboutReportProblem), () => ReportRequests.Open(ReportKind.Bug)),
                    HyperlinkButton.Create(Loc.Get(Strings.Report.AboutSuggestFeature), () => ReportRequests.Open(ReportKind.Feature)),
                    HyperlinkButton.Create(Loc.Get(Strings.Report.AboutAllIssues), FeedbackUrl),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.Website), WebsiteUrl),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.PrivacyPolicy), PrivacyUrl),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.ThirdPartyNotices), OpenThirdPartyNotices),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.CopyDiagnostics), () =>
                    {
                        hooks.Clipboard?.SetText(diagInfo());
                        Toast.Show(Loc.Get(Strings.Settings.About.DiagnosticsCopied), new ToastOptions { Severity = InfoBarSeverity.Success });
                    }),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.About.OpenDataFolder),
                        () => SettingsShared.OpenFolder(SettingsShared.AppDataRoot)),
                    new TextEl(Loc.Get(Strings.Settings.About.Unofficial)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    new TextEl(os) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    new TextEl(SettingsShared.AppDataRoot) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                ],
            },
        });
    }

    /// <summary>Open the shipped notices file with the shell. Absent in a dev run (it is generated at publish/pack
    /// time), which the toast says plainly rather than opening nothing.</summary>
    static void OpenThirdPartyNotices()
    {
        string path = NoticesPath;
        if (!File.Exists(path))
        {
            Toast.Show(Loc.Get(Strings.Settings.About.NoticesMissing), new ToastOptions { Severity = InfoBarSeverity.Informational });
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch
        {
            Toast.Show(Loc.Get(Strings.Settings.About.NoticesMissing), new ToastOptions { Severity = InfoBarSeverity.Warning });
        }
    }

    // Read once per process: the file is stamped at publish time and cannot change under a running build, and the
    // About tab re-renders on every theme/tab tick — a File.ReadAllText per render would be disk I/O on the UI thread.
    static string? s_notices;

    static string ReadThirdPartyNotices()
    {
        if (s_notices is not null) return s_notices;
        string text;
        try
        {
            string path = NoticesPath;
            text = File.Exists(path) ? File.ReadAllText(path) : Loc.Get(Strings.Settings.About.NoticesMissing);
        }
        catch { text = Loc.Get(Strings.Settings.About.NoticesMissing); }   // unreadable reads the same as absent
        s_notices = text;
        return text;
    }

    static Element[] LicenseExpanders()
    {
        var style = LicenseExpanderStyle;
        var items = new List<Element>();
        foreach (var lic in s_licenses)
        {
            items.Add(SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = lic.Name,
                Description = lic.Kind,
                InitiallyExpanded = false,
                Style = style,
                Items =
                [
                    SettingsExpander.Item("", null,
                        new TextEl(lic.Body) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                        alignment: SettingsCard.ContentAlignment.Left,
                        style: style),
                ],
            }));
        }
        // ONE expander for everything third-party, read from the generated file rather than restated in code.
        items.Add(SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.About.ThirdPartyNotices),
            Description = NoticesFileName,
            InitiallyExpanded = false,
            Style = style,
            Items =
            [
                SettingsExpander.Item("", null,
                    new TextEl(ReadThirdPartyNotices()) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                    alignment: SettingsCard.ContentAlignment.Left,
                    style: style),
            ],
        }));
        return items.ToArray();
    }

    /// <summary>
    /// Settings → About "Wavee right now" receipts. A 5s <see cref="Component.UseInterval"/> composes the strings;
    /// Render never reads process/GPU/FPS itself (no per-frame <see cref="FluentGpu.Hosting.FrameDiagnostics"/> subscribe).
    /// Mounted via Embed.Comp so the interval lives on this child, not behind SettingsPage's tab switch.
    /// </summary>
    sealed class WaveeNowReceipts : Component
    {
        internal static string LastCopyText { get; private set; } = "";

        const float TickMs = 5000f;
        readonly Signal<string> _gpu = new("—");
        readonly Signal<string> _workingSet = new("—");
        readonly Signal<string> _managed = new("—");
        readonly Signal<string> _uptime = new("—");
        readonly Signal<string> _fps = new("—");
        readonly Signal<string> _zoom = new("—");
        readonly Signal<string> _gpuAssets = new("—");
        readonly Signal<string> _appExcl = new("—");
        readonly Signal<string> _detail = new("—");

        /// <summary>The render adapter identity + power class (+ software note), a static read — shared by the receipt
        /// line here and by <c>DiagInfo()</c> so "Copy diagnostics" names the GPU. Empty adapter name reads as "—".</summary>
        internal static string GpuSummary()
        {
            string name = GpuProfile.AdapterName is { Length: > 0 } n ? n : "—";
            string tier = GpuProfile.Tier switch
            {
                GpuPowerTier.Weak => "Weak",
                GpuPowerTier.Strong => "Strong",
                _ => "Unknown",
            };
            return GpuProfile.IsSoftwareAdapter ? name + "  (" + tier + " · software)" : name + "  (" + tier + ")";
        }

        public override Element Render()
        {
            UseEffect(Tick, DepKey.Empty);
            UseInterval(Tick, TickMs);
            return SettingsCard.Create(new SettingsCard.Options
            {
                Alignment = SettingsCard.ContentAlignment.Left,
                Content = new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS,
                    Children =
                    [
                        ReceiptLine(_gpu, "GPU"),
                        ReceiptLine(_workingSet, "Working set"),
                        ReceiptLine(_managed, "Managed heap"),
                        ReceiptLine(_uptime, "Uptime"),
                        ReceiptLine(_fps, "FPS"),
                        ReceiptLine(_zoom, "Zoom"),
                        ReceiptLine(_gpuAssets, "GPU assets"),
                        ReceiptLine(_appExcl, "App memory excl. GPU assets"),
                        new TextEl(_detail) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    ],
                },
            });
        }

        void Tick()
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            long ws = proc.WorkingSet64;
            long managed = GC.GetTotalMemory(forceFullCollection: false);
            TimeSpan up = DateTime.Now - proc.StartTime;
            double fps = WaveeStartupBench.Host?.LastStats.Fps ?? 0;
            var snap = D3D12Device.LastVideoMemory;

            _gpu.Value = GpuSummary();
            _workingSet.Value = FormatBytes(ws);
            _managed.Value = FormatBytes(managed);
            _uptime.Value = FormatUptime(up);
            _fps.Value = fps > 0 ? fps.ToString("0.0", CultureInfo.InvariantCulture) : "—";
            // The 5s tick is plenty for a receipt (a fresh Ctrl+= shows within a tick) — deliberately no ZoomChanged
            // subscription here, so this component's lifecycle stays "one interval", nothing to unhook.
            _zoom.Value = ZoomLadder.Percent(FluentApp.Zoom) + "%";

            if (!snap.Valid)
            {
                _gpuAssets.Value = "— (no Present yet)";
                _appExcl.Value = "—";
                _detail.Value = "GPU video-memory snapshot publishes on the render thread after the first Present.";
            }
            else
            {
                bool sharedIgpu = ClassifySharedIgpu(in snap);
                ulong sharedSeg = sharedIgpu ? snap.LocalCurrentUsage : snap.NonLocalCurrentUsage;
                long excl = ws - (long)sharedSeg;
                if (excl < 0) excl = 0;
                string kind = sharedIgpu ? "shared / iGPU" : "discrete";
                _gpuAssets.Value = FormatBytes((long)snap.LocalCurrentUsage)
                    + " local  ·  " + FormatBytes((long)snap.NonLocalCurrentUsage) + " non-local";
                _appExcl.Value = FormatBytes(excl) + "  (" + kind + ")";
                _detail.Value =
                    "Local budget " + FormatBytes((long)snap.LocalBudget)
                    + "  ·  non-local budget " + FormatBytes((long)snap.NonLocalBudget)
                    + "  ·  tracked D3D12 " + FormatBytes(snap.TrackedResourceBytes)
                    + " (" + snap.TrackedResourceCount.ToString(CultureInfo.InvariantCulture) + ")"
                    + "  ·  atlas " + snap.AtlasImages.ToString(CultureInfo.InvariantCulture)
                    + "/" + snap.AtlasPages.ToString(CultureInfo.InvariantCulture)
                    + "  ·  glyphs " + snap.CachedGlyphs.ToString(CultureInfo.InvariantCulture)
                    + ". App excl. GPU ≈ working set − "
                    + (sharedIgpu ? "LOCAL (UMA/shared)" : "NON_LOCAL (system-memory overlap)")
                    + ".";
            }

            LastCopyText =
                "Working set: " + _workingSet.Peek()
                + "\nManaged heap: " + _managed.Peek()
                + "\nUptime: " + _uptime.Peek()
                + "\nFPS: " + _fps.Peek()
                + "\nZoom: " + _zoom.Peek()
                + "\nGPU assets: " + _gpuAssets.Peek()
                + "\nApp memory excl. GPU assets: " + _appExcl.Peek()
                + "\n" + _detail.Peek();
        }

        static bool ClassifySharedIgpu(in GpuVideoMemorySnapshot snap)
        {
            if (GpuProfile.IsWeak) return true;
            if (GpuProfile.Tier == GpuPowerTier.Strong) return false;
            // Unknown: task heuristic — NON_LOCAL bulk of the DXGI usage ⇒ shared/iGPU; LOCAL dominates ⇒ discrete.
            return snap.NonLocalCurrentUsage >= snap.LocalCurrentUsage && snap.NonLocalCurrentUsage > 0;
        }

        static Element ReceiptLine(Signal<string> value, string label) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Shrink = 0f },
                new TextEl(value) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
            ],
        };

        static string FormatBytes(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return ((int)t.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + t.Hours.ToString(CultureInfo.InvariantCulture) + "h";
            if (t.TotalHours >= 1) return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " + t.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            return Math.Max(0, (int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        }
    }

    // GpuPickerCard moved to SettingsPage.General.cs (Settings › General › Graphics) — a render-adapter picker is not
    // "about" the app.
}

/// <summary>Settings › About — the version hero and everything the updater owns.
///
/// <para>Its own <see cref="Component"/>, mounted through <c>Embed.Comp</c>, for the same reason as
/// <c>WaveeNowReceipts</c>: <c>SettingsPage.Render</c> builds the About body only while the About tab is selected, so a
/// hook called from that body would be a conditional hook. Here the subscription (<c>AppUpdate.Changed</c>) and the
/// three persisted switches live on a component whose whole lifetime IS the tab.</para></summary>
sealed class AboutUpdatePanel : Component
{
    static readonly NullAppUpdateService s_noUpdater = new();

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var nav = UseContext(HistoryStore.NavCtx);
        // UNCONDITIONAL, at the top with the other hooks — the same rule AfterUpdateChrome documents: this panel is
        // mounted inside the OverlayHost subtree, which is the only place UseContext(Overlay.Service) resolves the real
        // service, and a hook may never sit behind a branch.
        var overlay = UseContext(Overlay.Service);
        // The SIMULATOR when a developer-mode walk is running, else the live updater (else the null one). Same owner
        // as the notification row and the toast actions, so all three agree about which service a press reaches.
        var upd = AppUpdateSurface.Resolve(svc) ?? s_noUpdater;

        _ = this.UseObservable(upd.Changed);            // re-render on every published snapshot
        var installOnQuit = this.UseSettingSignal(svc?.Settings, WaveeSettings.UpdateInstallOnQuit);
        var metered = this.UseSettingSignal(svc?.Settings, WaveeSettings.UpdateOnMetered);
        var autoShow = this.UseSettingSignal(svc?.Settings, WaveeSettings.ReleaseNotesAutoShow);

        var me = AppVersion.Info;
        var s = upd.Current;

        // "Show the update summary again" — the after-update plate on demand, for the user who dismissed it (or never
        // saw it because the wizard/crash notice deferred it). It reads the DURABLE previousVersion rather than the
        // one-shot pendingFrom, which the plate itself clears; on an install that has never updated there is no
        // from-quad at all, so the plate opens against the running build and simply shows this release's own notes.
        // A dev build has no update story to summarize and gets no link.
        Action? showSummary = null;
        if (overlay is not null && !me.IsDev && svc is { } services)
        {
            // The two collaborators are pulled out HERE, not inside the closure: the null test above does not travel
            // into a lambda body, and a captured `svc?.` would make the plate silently un-openable instead.
            var settings = services.Settings;
            var notes = services.ReleaseNotes;
            showSummary = () =>
            {
                string from = settings.Get(WaveeSettings.ReleaseNotesPreviousVersion);
                if (from.Length == 0) from = me.Quad;
                AfterUpdateDialog.Open(overlay, settings, from, me, notes, key => nav?.Invoke(key, null));
            };
        }

        return new BoxEl
        {
            Direction = 1, Gap = 4f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                Hero(me, s, upd, nav, showSummary),
                // A Store install has no feed, no channel choice, no install-on-quit and no metered gate: the Store
                // owns all four. One card says where updates come from and opens the listing.
                ..(me.IsStore
                    ? new Element[] { StoreCard(upd) }
                    : new Element[]
                    {
                        StatusCard(s, upd),
                        ChannelCard(),
                        InstallOnQuitCard(svc, installOnQuit),
                        SettingsCard.Create(new SettingsCard.Options
                        {
                            Header = Loc.Get(Strings.Update.Metered.Title),
                            Description = Loc.Get(Strings.Update.Metered.Hint),
                            HeaderIcon = Icons.RadioTower,
                            Content = ToggleSwitch.Create(metered, v => svc?.Settings.Set(WaveeSettings.UpdateOnMetered, v),
                                style: SettingsCard.CompactToggleStyle()),
                        }),
                    }),
                SettingsCard.Create(new SettingsCard.Options
                {
                    Header = Loc.Get(Strings.Update.AutoShow.Title),
                    Description = Loc.Get(Strings.Update.AutoShow.Hint),
                    HeaderIcon = Icons.RefineSparkle,
                    Content = ToggleSwitch.Create(autoShow, v => svc?.Settings.Set(WaveeSettings.ReleaseNotesAutoShow, v),
                        style: SettingsCard.CompactToggleStyle()),
                }),
            ],
        };
    }

    // ── hero ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="showSummary">Re-opens the after-update plate, or null when this build has no summary to show
    /// (a dev build, or no services). Built in <see cref="Render"/> because only a component may read the overlay
    /// context.</param>
    static Element Hero(WaveeVersionInfo me, AppUpdateSnapshot s, IAppUpdateService upd, Action<string, string?>? nav,
                        Action? showSummary)
    {
        // A dev build has nothing for the feed to be newer than, so its primary action and its pill are inert — unless
        // a developer-mode SIMULATION is walking, in which case the states on screen are real enough to press.
        bool inert = me.IsDev && !AppUpdateSurface.IsSimulating;
        var pills = new List<Element>(4);
        if (me.Quad is { Length: > 0 } quad) pills.Add(ReleaseNotesHero.Pill(quad, mono: true));
        pills.Add(ReleaseNotesHero.Pill(ChannelLabel(me.Channel)));
        pills.Add(StatePill(inert, me.IsStore, s));

        string stamp = (me.Commit is { Length: > 0 } c ? c + " · " : "") + SettingsPage.ArchToken;
        pills.Add(new TextEl(stamp) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" });

        var right = new List<Element>(3) { PrimaryButton(inert, me.IsStore, s, upd) };
        if (nav is not null && me.Codename is { Length: > 0 } name)
            right.Add(HyperlinkButton.Create(Strings.Update.About.WhatsNewIn(name), () => nav("whatsnew", null)));
        if (showSummary is not null)
            right.Add(HyperlinkButton.Create(Loc.Get(Strings.Update.About.ShowSummaryAgain), showSummary));

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
                    Children = [ Icon(Icons.MusicNote, 30f, Tok.AccentTextPrimary) ],
                },
                new BoxEl
                {
                    Direction = 1, Gap = 6f, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(AppVersionDisplay.Of(me)) { Size = 24f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = pills.ToArray() },
                        new TextEl(Provenance(me, s)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    ],
                },
                new BoxEl { Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.End, Children = right.ToArray() },
            ],
        };
    }

    /// <summary>"Built 2026-08-29 · last checked 29/08/2026 14:02" — the two dates that answer "is this thing current?".
    /// A build with no stamp says so instead of printing an empty separator.</summary>
    static string Provenance(WaveeVersionInfo me, AppUpdateSnapshot s)
    {
        var parts = new List<string>(2);
        if (me.BuildDate is { Length: > 0 } built) parts.Add(Strings.Update.About.Built(built));
        parts.Add(s.LastCheckedMs > 0
            ? Strings.Update.About.LastChecked(Stamp(s.LastCheckedMs))
            : Loc.Get(Strings.Update.About.NeverChecked));
        return string.Join("  ·  ", parts);
    }

    /// <summary>"Last checked" as a short local timestamp. INVARIANT culture explicitly: Wavee publishes with
    /// <c>InvariantGlobalization=true</c>, so <c>CurrentCulture</c> already IS the invariant culture at runtime and
    /// naming it only hid that from the reader of this code.</summary>
    static string Stamp(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("g", CultureInfo.InvariantCulture);

    static string ChannelLabel(string? channel) => Loc.Get(channel switch
    {
        "beta" => Strings.Update.About.ChannelBeta,
        "stable" => Strings.Update.About.ChannelStable,
        _ => Strings.Update.About.ChannelDev,
    });

    /// <summary>The hero's state pill. A Store build's <paramref name="isStore"/> branch comes BEFORE the
    /// <see cref="AppUpdateSnapshot.State"/> switch below: <c>StoreUpdateService.Current</c> is permanently
    /// <see cref="AppUpdateState.None"/> (the Store owns checking, this process never learns the answer), so falling
    /// through to that switch's default arm would print "Up to date" — a claim this build cannot back up and one
    /// that directly contradicts <see cref="Provenance"/>'s honest "not checked yet" on the very same card. Reusing
    /// that same "not checked yet" copy here keeps the two lines from disagreeing with each other.</summary>
    static Element StatePill(bool inert, bool isStore, AppUpdateSnapshot s)
    {
        if (inert) return ReleaseNotesHero.Pill(Loc.Get(Strings.Settings.About.DevBuild));
        if (isStore) return ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.NeverChecked));
        return s.State switch
        {
            AppUpdateState.Checking => ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.PillChecking)),
            AppUpdateState.Available => ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.PillAvailable), accent: true),
            AppUpdateState.Snoozed => ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.PillSnoozed)),
            AppUpdateState.Downloading => ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.PillDownloading), accent: true),
            AppUpdateState.Installing => ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.PillInstalling), accent: true),
            AppUpdateState.Completed => ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.PillUpdated)),
            AppUpdateState.Failed => ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.PillFailed)),
            _ => ReleaseNotesHero.Pill(Loc.Get(Strings.Update.About.PillUpToDate)),
        };
    }

    /// <summary>The ONE primary action, in the hero. A dev build gets a disabled "Check for updates": the button has to
    /// stay put (the row is the same shape in every build) but there is nothing for the feed to be newer than.
    ///
    /// <para>A Store build gets the SAME "stay put, change the verb" treatment: this process never polls a feed (the
    /// Store does), so the button opens the Store listing (<see cref="IAppUpdateService.ApplyAsync"/>, which for
    /// <c>StoreUpdateService</c> is exactly <c>_openUrl(FeedUrl)</c> → the <c>ms-windows-store://pdp/…</c> deep
    /// link) instead of calling the no-op <c>CheckAsync</c> — that no-op, wired here before this fix, was the whole
    /// of "Check for updates does nothing" on a Store build. <see cref="StoreCard"/> a little further down the page
    /// offers the identical action again with the explanatory copy ("this copy of Wavee ... updates arrive through
    /// the Store") — a deliberate, small duplication rather than leaving the hero's one always-present CTA either
    /// dead or silently missing on this one build shape, which would make the row's shape depend on the install
    /// type in a way nothing else in this file does.</para></summary>
    static Element PrimaryButton(bool inert, bool isStore, AppUpdateSnapshot s, IAppUpdateService upd)
    {
        if (inert)
            return Button.Create(Loc.Get(Strings.Update.Action.Check), static () => { },
                ButtonAppearance.Standard, ControlSize.Medium, isEnabled: false);
        if (isStore)
            return Button.Standard(Loc.Get(Strings.Update.Store.Open), () => _ = upd.ApplyAsync(CancellationToken.None));

        return s.State switch
        {
            AppUpdateState.Available or AppUpdateState.Snoozed or AppUpdateState.Failed =>
                Button.Accent(Loc.Get(s.State == AppUpdateState.Failed ? Strings.Update.Action.Retry : Strings.Update.Action.UpdateNow),
                    () => _ = upd.ApplyAsync(CancellationToken.None)),
            AppUpdateState.Checking =>
                Button.Create(Loc.Get(Strings.Update.State.Checking), static () => { },
                    ButtonAppearance.Standard, ControlSize.Medium, isEnabled: false),
            AppUpdateState.Downloading or AppUpdateState.Installing =>
                Button.Create(Loc.Get(Strings.Update.State.Installing), static () => { },
                    ButtonAppearance.Standard, ControlSize.Medium, isEnabled: false),
            _ => Button.Standard(Loc.Get(Strings.Update.Action.Check), () => _ = upd.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None)),
        };
    }

    // ── cards ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Store build's one update card: the Store applies updates, so the button opens the listing.</summary>
    static Element StoreCard(IAppUpdateService upd) => SettingsCard.Create(new SettingsCard.Options
    {
        Header = Loc.Get(Strings.Update.Store.Title),
        HeaderIcon = Icons.Refresh,
        Description = Loc.Get(Strings.Update.Store.Hint),
        Content = Button.Create(Loc.Get(Strings.Update.Store.Open),
            () => _ = upd.ApplyAsync(CancellationToken.None), ButtonAppearance.Standard, ControlSize.Small),
    });

    static Element StatusCard(AppUpdateSnapshot s, IAppUpdateService upd)
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
            // The TAG of the version that failed, not the bare listing: a user chasing a specific failed update must
            // land on the release that failed. ReleaseNotesText.ReleasePageUrl is the one owner of that rule.
            AppUpdateState.Failed => Button.Create(Loc.Get(Strings.Update.Action.OpenReleasePage),
                () => LoginView.OpenUrl(ReleaseNotesText.ReleasePageUrl(s)), ButtonAppearance.Standard, ControlSize.Small),
            AppUpdateState.Completed => Button.Create(Loc.Get(Strings.Update.Action.Dismiss),
                upd.Acknowledge, ButtonAppearance.Subtle, ControlSize.Small),
            _ => null,
        };

        return SettingsCard.Create(new SettingsCard.Options
        {
            Header = Loc.Get(Strings.Update.Status.Title),
            HeaderIcon = Icons.Refresh,
            Description = StateSentence(s),
            Content = content,
        });
    }

    internal static string StateSentence(AppUpdateSnapshot s) => s.State switch
    {
        AppUpdateState.Checking => Loc.Get(Strings.Update.State.Checking),
        // ONE naming rule for all four: AppUpdateToasts.ReleaseName (codename -> semver -> quad, never empty). The
        // "available" line used to interpolate the semver AND the codename separately, so an unknown codename printed
        // "Wavee 0.3.0  is available" with a double space; "just updated" read AppVersion.Info.Codename raw and said
        // "Updated to  just now" on a build with no codename stamp.
        AppUpdateState.Available => Strings.Update.State.Available(AppUpdateToasts.ReleaseName(s)),
        AppUpdateState.Snoozed => Strings.Update.State.Snoozed(s.TargetSemVer ?? s.TargetQuad ?? ""),
        AppUpdateState.Downloading => Strings.Update.State.Downloading(AppUpdateToasts.ReleaseName(s)),
        AppUpdateState.Installing => Loc.Get(Strings.Update.State.Installing),
        AppUpdateState.Completed => Strings.Update.State.JustUpdated(AppUpdateToasts.ReleaseName(s)),
        AppUpdateState.Failed => FailureText(s.Failure),
        _ => Loc.Get(Strings.Update.State.UpToDate),
    };

    /// <summary>One owner for the failure copy: the pure, unit-tested <see cref="AppUpdateToasts.FailureText"/>.</summary>
    internal static string FailureText(AppUpdateFailure? f) => AppUpdateToasts.FailureText(f);

    /// <summary>The beta channel is a SIDE-BY-SIDE package, not a switch: the link hands the user to the OS App
    /// Installer for <c>Wavee.Beta.&lt;arch&gt;.appinstaller</c>, which installs beside Stable. That is why this is a
    /// link and not a picker — nothing in the running app changes when it is clicked.</summary>
    static Element ChannelCard() => SettingsCard.Create(new SettingsCard.Options
    {
        Header = Loc.Get(Strings.Update.Channel.Title),
        Description = Loc.Get(Strings.Update.Channel.Hint),
        HeaderIcon = Icons.Devices,
        Content = HyperlinkButton.Create(Loc.Get(Strings.Update.Channel.Beta), BetaFeedUrl),
    });

    internal static string BetaFeedUrl =>
        ReleaseNotesText.RepoUrl + "/releases/download/wavee-beta/Wavee.Beta." + SettingsPage.ArchToken + ".appinstaller";

    /// <summary>The one update-timing choice Wavee actually implements.
    ///
    /// <para>This replaced a three-way "how updates install" picker (background / on quit / notify only) that NOTHING
    /// read: the setting was written and never consulted, so all three options behaved identically and the copy
    /// promised behaviour the app did not have. Updates apply on the NEXT LAUNCH — that is what the packaged
    /// deployment path does and it is not configurable — and the single real choice on top of it is whether Wavee
    /// also spends the moments after you close it staging an update the feed has already offered. That switch is read
    /// by <c>Program.Main</c>'s orderly-shutdown path through
    /// <see cref="Wavee.Core.ShutdownUpdatePolicy.ShouldApply"/>.</para></summary>
    static Element InstallOnQuitCard(Services? svc, Signal<bool> installOnQuit) => SettingsCard.Create(new SettingsCard.Options
    {
        Header = Loc.Get(Strings.Update.InstallOnQuit.Title),
        Description = Loc.Get(Strings.Update.InstallOnQuit.Hint),
        HeaderIcon = Icons.Download,
        Content = ToggleSwitch.Create(installOnQuit, v => svc?.Settings.Set(WaveeSettings.UpdateInstallOnQuit, v),
            style: SettingsCard.CompactToggleStyle()),
    });
}
