// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Body.Help.cs — Help & about's WinUI cards ──────────────────────────────────────────────────────────
//
// About Wavee: the update panel (the version hero + the W20 state matrix, the status card with its progress, "Open release page" on a failure, "Dismiss"
// after an update), the Beta channel link, "Wavee right now" (the receipts: the GPU line and the counters, a 5 s interval on a child; N12), What's new with its
// unread dot. Feedback: what a report holds (one expander of facts). Keyboard shortcuts: ONE collapsed expander of the chords the shell defines (owner
// decision 1), each a key chip, Ctrl+Tab where the structure cycles the workspace. Diagnostics: the log viewer card (live counts bound into its description,
// a 750 ms interval that writes only when the log or the minute moved), the two detail levels, the log-file rule, the realtime capture. Developer: the
// switch and the three items it unlocks (present and greyed while it is off). Links and licenses: the two license expanders over the generated notices.
//
// Everything with LIVE state is a child component; the state matrix (`Settings.AboutRules`), the verbs (the dials' writers) and the notices file are the core's.
// A Store install has no feed, channel, install-on-quit or metered gate — the Store owns all four — so its rows are drawn only where they apply.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using AboutRules = Wavee.Settings.AboutRules;
using PrivacyRules = Wavee.Settings.PrivacyRules;
using PvDev = Wavee.Strings.Settings.Privacy.Developer;
using PvLogs = Wavee.Strings.Settings.Privacy.Logs;
using PvTools = Wavee.Strings.Settings.Privacy.Tools;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    /// <summary>A row WinUI draws only where it applies: the Store's own card on a Store install, the feed's rows (channel, install on quit, metered) elsewhere,
    /// "Show summary again" where the after-update plate can open.</summary>
    static bool Draws(SettingsBodyRow row) => row.Id switch
    {
        "help.update.store" => Platform.Version.IsStore,
        "help.update.channel" or "help.update.installOnQuit" or "help.update.onMetered" => !Platform.Version.IsStore,
        "help.showSummaryAgain" => SettingsContext.Overlay is not null && ReleaseNotes.CanShowSummaryAgain,
        "account.plan" => false,   // it rides the account's row (the name over the plan)
        _ => true,
    };

    // ══ 1. ABOUT: THE UPDATE PANEL (W19 hero + cards, W20 matrix) ═════════════════════════════════════════════════

    /// <summary>The update hero and its status card: one component re-rendered by every published snapshot (it reads <see cref="Notify.Update"/>).</summary>
    static Element UpdatePanelCard(RowCtx x) => Embed.Comp(new UpdateProps(x), static () => new AboutUpdatePanel()) with { Key = "about:update" };

    sealed record UpdateProps(RowCtx X);

    sealed class AboutUpdatePanel : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var x = UseProps<UpdateProps>().X;
            var s = Notify.Update.Value;
            var me = Platform.Version;
            var kids = new List<Element>(2) { UpdateHero(x, me, s) };
            if (!me.IsStore) kids.Add(UpdateStatusCard(x, s));
            return new BoxEl { Direction = 1, Gap = WinUiSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
        }
    }

    /// <summary>The hero: a 64² accent-subtle tile, the 24/600 version line, a wrapping pill row (quad MONO · channel · state) + the commit/arch stamp, the
    /// provenance line; on the right the primary button (the hero dial's writer: check · update now · retry · nothing, chosen from the live state at the press).</summary>
    static Element UpdateHero(RowCtx x, WaveeVersionInfo me, AppUpdateSnapshot s)
    {
        // A dev build has nothing for the feed to be newer than — unless a developer-mode SIMULATION is walking, whose states are real enough to press.
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
        pills.Add(new TextEl((me.Commit.Length > 0 ? me.Commit + " · " : "") + Settings.ArchToken)
            { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" });

        var hero = x.Row.Dial;
        Action act = shape.Verb == AboutRules.HeroVerb.None ? static () => { } : () => Press(hero);
        Element button = Btn(Loc.Get(shape.ButtonKey), act,
            shape.Tone == AboutRules.HeroTone.Accent ? ButtonAppearance.Accent : ButtonAppearance.Standard, shape.Tone != AboutRules.HeroTone.Disabled);

        long lastChecked = s.LastCheckedMs > 0 ? s.LastCheckedMs : SettingsFacts.UpdateLastCheckedMs();
        string provenance = AboutRules.Provenance(me.BuildDate, lastChecked, static d => Strings.Update.About.Built(d),
            static w => Strings.Update.About.LastChecked(w), Loc.Get(Strings.Update.About.NeverChecked));

        // Where the tier stacks its controls (Compact) there is no room for the tile, the words and the button side by side: they stack, the button under the words.
        bool stack = s_layout.StacksControls;
        return new BoxEl
        {
            Direction = stack ? (byte)1 : (byte)0, Gap = WinUiSettingsRules.HeroGap, AlignItems = stack ? FlexAlign.Start : FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = WinUiSettingsRules.HeroPadding, Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Width = WinUiSettingsRules.HeroTile, Height = WinUiSettingsRules.HeroTile, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(WinUiSettingsRules.HeroTileRadius), Fill = Tok.AccentSubtle,
                    Children = [Icon(Icons.MusicNote, WinUiSettingsRules.HeroGlyph, Tok.AccentTextPrimary)],
                },
                new BoxEl
                {
                    Direction = 1, Gap = WinUiSettingsRules.HeroTextGap, Grow = 1f, Shrink = 1f, Basis = stack ? float.NaN : 0f, MinWidth = 0f, AlignSelf = stack ? FlexAlign.Stretch : FlexAlign.Auto,
                    Children =
                    [
                        new TextEl(ReleaseNotes.VersionDisplayText()) { Size = WinUiSettingsRules.HeroVersionSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = pills.ToArray() },
                        new TextEl(provenance) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    ],
                },
                new BoxEl { Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = stack ? FlexAlign.Start : FlexAlign.End, Children = [button] },
            ],
        };
    }

    /// <summary>The status card: the ONE state sentence (`Notify.AppUpdateToasts.StateSentence`), plus progress while downloading/installing, "Open release page" on
    /// a failure, "Dismiss" after an update (the hero's two child dials, hosted).</summary>
    static Element UpdateStatusCard(RowCtx x, AppUpdateSnapshot s)
    {
        Element? content = s.State switch
        {
            AppUpdateState.Downloading or AppUpdateState.Installing => new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    ProgressBar.Determinate(s.ProgressPercent / 100f, WinUiSettingsRules.UpdateProgressWidth),
                    new TextEl(s.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%")
                        { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
                ],
            },
            // The release that FAILED, not the bare listing (`Update.ReleasePageUrl` is the one owner of that rule).
            AppUpdateState.Failed => HostedItem(x, "help.update.openReleasePage",
                StdButton(Loc.Get(Strings.Update.Action.OpenReleasePage), static () => Press(Dial("help.update.openReleasePage")))),
            AppUpdateState.Completed => HostedItem(x, "help.update.dismiss",
                SubtleButton(Loc.Get(Strings.Update.Action.Dismiss), static () => Press(Dial("help.update.dismiss")))),
            _ => null,
        };
        return PlainCard(Loc.Get(Strings.Update.Status.Title), Notify.AppUpdateToasts.StateSentence(s), content) with { Key = "about:update:status" };
    }

    /// <summary>The Beta channel: a LINK, not a picker (Beta installs beside Stable; nothing in this app changes when it is clicked).</summary>
    static Element ChannelCard(RowCtx x) => DialCard(BaseCard(x, LinkButton(Loc.Get(Strings.Update.Channel.Beta), Settings.BetaFeedUrl)));

    /// <summary>What's new: a door, with the unread dot while the running release's notes have never been opened (the ONLY unread affordance outside the
    /// notification centre).</summary>
    static Element WhatsNewCard(RowCtx x)
    {
        var card = DoorCard(x, () => Press(x.Row.Dial));
        return DialCard(ReleaseNotes.RunningNotesUnread() ? card with { Control = InfoBadge.Dot() } : card);
    }

    // ══ 2. ABOUT: "WAVEE RIGHT NOW" (N12: a 5 000 ms interval on THIS child; Render never reads process/GPU/FPS) ═════════

    static Element ReceiptsCardOf(RowCtx x) => Embed.Comp(new ReceiptsProps(x), static () => new ReceiptsCard());

    sealed record ReceiptsProps(RowCtx X);

    sealed class ReceiptsCard : SettingsComponent
    {
        readonly Signal<string> _gpu = new(Settings.Receipts.Dash), _workingSet = new(Settings.Receipts.Dash), _managed = new(Settings.Receipts.Dash),
            _uptime = new(Settings.Receipts.Dash), _fps = new(Settings.Receipts.Dash), _zoom = new(Settings.Receipts.Dash), _gpuAssets = new(Settings.Receipts.Dash),
            _appExcl = new(Settings.Receipts.Dash), _detail = new(Settings.Receipts.Dash);

        public override Element Render()
        {
            UseLayout();
            UseEffect(Tick, DepKey.Empty);
            UseInterval(Tick, WinUiSettingsRules.ReceiptsTickMs);
            var lines = new BoxEl
            {
                Direction = 1, Gap = Spacing.XS, MinWidth = 0f,
                Children =
                [
                    Line(_gpu, "GPU"), Line(_workingSet, "Working set"), Line(_managed, "Managed heap"), Line(_uptime, "Uptime"),
                    Line(_fps, "FPS"), Line(_zoom, "Zoom"), Line(_gpuAssets, "GPU assets"),
                    Line(_appExcl, "App memory excl. GPU assets"),
                    new TextEl(_detail) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                ],
            };
            return DialCard(NewCard("", null, lines) with { Form = CardForm.Left });
        }

        static Element Line(Signal<string> value, string label) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(label) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary, Shrink = 0f },
                new TextEl(value)
                {
                    Size = R.Type.DenseTitle("").Size,
                    LineHeight = R.Type.DenseTitle("").LineHeight,
                    Weight = R.Type.DenseTitle("").ResolvedWeight,
                    Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap,
                },
            ],
        };

        void Tick()
        {
            var sample = Settings.SampleReceipts();
            _gpu.Value = sample.Gpu;
            _workingSet.Value = sample.WorkingSet;
            _managed.Value = sample.Managed;
            _uptime.Value = sample.Uptime;
            _fps.Value = sample.Fps;
            _zoom.Value = sample.Zoom;
            _gpuAssets.Value = sample.GpuAssets;
            _appExcl.Value = sample.AppExclusive;
            _detail.Value = sample.Detail;
        }
    }

    // ══ 3. FEEDBACK AND SHORTCUTS ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the report dialog sends: the four lines it states, then its own sentence on what is sent and when.</summary>
    static Element ReportHoldsCard(RowCtx x)
    {
        Element[] items =
        [
            PlainItem(Loc.Get(Strings.Settings.Feedback.HoldsWrite), null),
            PlainItem(Loc.Get(Strings.Settings.Feedback.HoldsVersions), null),
            PlainItem(Strings.Settings.Feedback.HoldsLogs(Feedback.ReportBundle.ManualLogLines), null),
            PlainItem(Loc.Get(Strings.Settings.Feedback.HoldsNever), null),
        ];
        return Expander(BaseCard(x, null) with { Description = Loc.Get(Strings.Report.Subtitle) }, items, x.Expanded) with { Key = "feedback.holds" };
    }

    sealed record ShortcutsProps(System.Collections.Immutable.ImmutableArray<SettingsBodyRow> Rows, SettingsBody Body, SettingsArrival Arrival, SettingsPageProps Page);

    /// <summary>The Keyboard shortcuts group as ONE collapsed expander (owner decision 1): the group's title and caption in its header, a row per chord inside (each
    /// its dial, hosted), Ctrl+Tab after New tab where the structure cycles the workspace. A link to one chord opens it.</summary>
    sealed class ShortcutsGroup : SettingsComponent
    {
        public override Element Render()
        {
            var layout = UseLayout();
            var p = UseProps<ShortcutsProps>();
            var expanded = UseSignal(false);
            var arrival = p.Arrival;
            UseLayoutEffect(() =>
            {
                if (arrival.IsNone || arrival.Seq <= s_arrivalPlayed.Value || expanded.Peek()) return;
                foreach (var row in p.Rows)
                    if (string.Equals(row.Id, arrival.DialId, StringComparison.Ordinal)) { expanded.Value = true; break; }
            }, DepKey.From(arrival.Seq));

            // Ctrl+Tab cycles the workspace where the structure asks for the chord or hides the strip (WinUI's tabs option: with the strip hidden it is the only way
            // to reach a background tab). The row sits after New tab, where the other tab chords are.
            bool cycles = ShellCaps.Tracked.TabCycleChord;
            var items = new List<Element>(p.Rows.Length + 1);
            foreach (var row in p.Rows)
            {
                items.Add(RowHost(row, p.Body, p.Page, item: true));
                if (cycles && row.Id == "help.shortcuts.newTab")
                    items.Add(PlainItem(Loc.Get(Strings.Theme.Winui.Screens.CycleTabs), Loc.Get(Strings.Theme.Winui.Screens.CycleTabsSub), KeyChip(CycleTabsChord))
                        with { Key = "settings:keyboard:cycleTabs" });
            }
            var group = GroupOf(p.Body, ShortcutsGroupId);
            var header = NewCard(group?.Title ?? "", group?.Subtitle) with { Glyph = null };
            return new BoxEl
            {
                Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Padding = new Edges4(0f, layout.SectionTop, 0f, 0f),
                Children = [Expander(header, items, expanded)],
            };
        }
    }

    /// <summary>The chord the Ctrl+Tab row names.</summary>
    const string CycleTabsChord = "Ctrl+Tab";

    /// <summary>One chord: its label and its key chip (a read-only reference of the chords the shell defines).</summary>
    static Element ShortcutItem(RowCtx x) => DialCard(BaseCard(x, KeyChip(x.Row.Value as string ?? "")));

    static Element KeyChip(string chord) => new BoxEl
    {
        Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WinUiSettingsRules.KeyChipPadX, WinUiSettingsRules.KeyChipPadY, WinUiSettingsRules.KeyChipPadX, WinUiSettingsRules.KeyChipPadY),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Corners = CornerRadius4.All(Spacing.XXS), HitTestVisible = false,
        Children = [new TextEl(Settings.Shortcuts.Words(chord)) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };

    // ══ 4. DIAGNOSTICS ════════════════════════════════════════════════════════════════════════════════════════════

    static Element LogViewerCardOf(RowCtx x) => Embed.Comp(new LogViewerProps(x), static () => new LogViewerCard()) with { Key = "privacy.log-viewer" };

    sealed record LogViewerProps(RowCtx X);

    /// <summary>The Log viewer card: opens the viewer page, and its description counts the live session. The counts and the uptime are bound into the
    /// description (the card's live line), fed by a 750 ms interval that writes only when the log or the minute moved — so ONE text node relays out, the card
    /// never re-renders, and nothing runs per frame (N12).</summary>
    sealed class LogViewerCard : SettingsComponent
    {
        readonly Signal<string> _line = new(Compose());
        long _seenVersion = -1, _seenMinute = -1;

        public override Element Render()
        {
            UseLayout();
            var x = UseProps<LogViewerProps>().X;
            UseInterval(() =>
            {
                long version = Log.Version, minute = Log.SinceStartMs / 60_000;
                if (version == _seenVersion && minute == _seenMinute) return;
                _seenVersion = version;
                _seenMinute = minute;
                _line.Value = Compose();
            }, WinUiSettingsRules.LogTickMs);
            var viewer = x.Row.Dial;
            return DialCard(DoorCard(x, () => Press(viewer)) with { LiveDescription = _line });
        }

        static string Compose()
        {
            Log.Tally(out int total, out int warnings, out int errors);
            return PvLogs.ViewerSub(Thousands(total), Thousands(warnings), Thousands(errors),
                Diagnostics.LogView.Uptime(TimeSpan.FromMilliseconds(Log.SinceStartMs)));
        }

        static string Thousands(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }

    static Element DetailLevelCard(RowCtx x) => Embed.Comp(new DetailLevelProps(x), static () => new DetailLevelGroup()) with { Key = "privacy.detail-level" };

    sealed record DetailLevelProps(RowCtx X);

    /// <summary>The two detail levels as one expander (the composite dial; its two child dials hosted): what the viewer captures and what reaches the log file.
    /// Both are persisted settings, so the group subscribes <see cref="Platform.SettingsChanged"/> — the viewer's Verbose toggle writes the same keys and the
    /// header tag follows. A combo's items and selection freeze at mount, so each is keyed by the level VALUE it shows.</summary>
    sealed class DetailLevelGroup : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var x = UseProps<DetailLevelProps>().X;
            _ = Platform.SettingsChanged.Value;   // subscribed
            var viewer = Log.MinLevel;
            var file = LogCapturePolicy.EffectiveFileLevel(viewer, Log.FileMinLevel);
            int viewerIndex = Math.Clamp((int)viewer, 0, Diagnostics.LogView.LevelNames.Length - 1);
            int fileIndex = Math.Clamp((int)file, 0, Diagnostics.LogView.LevelNames.Length - 1);
            var viewerDial = Dial("help.logs.viewerLevel");
            var fileDial = Dial("help.logs.fileLevel");
            var items = new List<Element>(2)
            {
                HostedItem(x, "help.logs.viewerLevel", PlainItem(Loc.Get(PvLogs.InViewer), Loc.Get(PvLogs.InViewerSub),
                    Combo(Diagnostics.LogView.LevelNames, new Signal<int>(viewerIndex), width: WinUiSettingsRules.LevelComboWidth,
                        onChange: i => viewerDial.Writer.Choose!(i)) with { Key = "privacy.level.viewer:" + viewerIndex.ToString(CultureInfo.InvariantCulture) })),
                HostedItem(x, "help.logs.fileLevel", PlainItem(Loc.Get(PvLogs.InFile), Loc.Get(PvLogs.InFileSub),
                    Combo(Diagnostics.LogView.LevelNames, new Signal<int>(fileIndex), width: WinUiSettingsRules.LevelComboWidth,
                        onChange: i => fileDial.Writer.Choose!(i)) with { Key = "privacy.level.file:" + fileIndex.ToString(CultureInfo.InvariantCulture) })),
            };
            var header = BaseCard(x, ValueTag(PvLogs.DetailLevelValue(Diagnostics.LogView.LevelNames[viewerIndex], Diagnostics.LogView.LevelNames[fileIndex])))
                with { Description = Loc.Get(PvLogs.DetailLevelSub) };
            return Expander(header, items, x.Expanded);
        }
    }

    /// <summary>The writer's rule in one sentence, fed by the writer's own numbers (never hard-coded here), and a door to the folder.</summary>
    static Element LogFilesCard(RowCtx x)
        => DialCard(BaseCard(x, StdButton(Loc.Get(PvLogs.OpenFolder), () => Press(x.Row.Dial)))
            with { Description = PvLogs.FilesSub(PrivacyRules.WholeMb(Log.MaxFileBytes), Log.RetentionDays.ToString(CultureInfo.InvariantCulture), PrivacyRules.WholeMb(Log.RetentionBytes)) });

    /// <summary>The realtime capture: the switch applies live; the two doors sit inside (its child dials, hosted).</summary>
    static Element CaptureCard(RowCtx x)
    {
        var items = new List<Element>(2);
        if (Child(x, "help.tools.captureViewer") is { } viewer)
            items.Add(ChildHost(x, viewer, PlainItem(Loc.Get(PvTools.OpenCaptureViewer), Loc.Get(PvTools.OpenCaptureViewerSub), onClick: () => Press(viewer.Dial))));
        if (Child(x, "help.tools.captureFolder") is { } folder)
            items.Add(ChildHost(x, folder, PlainItem(Loc.Get(PvTools.OpenCaptureFolder), Loc.Get(PvTools.OpenCaptureFolderSub),
                StdButton(Loc.Get(PvTools.OpenFolder), () => Press(folder.Dial)))));
        return Expander(BaseCard(x, DialToggle(x.Row.Dial, x.Row.Enabled)), items, x.Expanded) with { Key = "privacy.capture" };
    }

    // ══ 5. DEVELOPER ══════════════════════════════════════════════════════════════════════════════════════════════

    static Element DeveloperCard(RowCtx x) => Embed.Comp(new DeveloperProps(x), static () => new DeveloperGroup()) with { Key = "privacy.developer" };

    sealed record DeveloperProps(RowCtx X);

    /// <summary>Developer mode and the three items it unlocks. The items are PRESENT and greyed while the switch is off (D2). The switch and the FPS overlay go
    /// through the one persist-then-publish setter, so they apply at once; the group reads the same signals, so it follows a flip made anywhere.</summary>
    sealed class DeveloperGroup : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var x = UseProps<DeveloperProps>().X;
            var facts = SettingsFacts.Live;
            bool dev = facts.DeveloperMode;
            bool fps = Platform.Developer.FpsOverlay.Value;
            var mode = x.Row.Dial;
            var items = new List<Element>(3);
            if (Child(x, "help.developer.fpsOverlay") is { } fpsRow)
            {
                var fpsDial = fpsRow.Dial;
                bool on = fpsDial.Enabled(facts).Enabled;
                items.Add(ChildHost(x, fpsRow, PlainItem(Loc.Get(PvDev.FpsOverlay), Loc.Get(PvDev.FpsOverlaySub),
                    Toggle(new Signal<bool>(fps), onChange: v => fpsDial.Writer.Toggle!(v), isEnabled: on), isEnabled: on)));
            }
            if (Child(x, "help.developer.simulateUpdate") is { } simulate)
            {
                bool on = simulate.Dial.Enabled(facts).Enabled;
                items.Add(ChildHost(x, simulate, PlainItem(Loc.Get(PvDev.SimulateUpdate), Loc.Get(PvDev.SimulateUpdateSub),
                    StdButton(Loc.Get(PvDev.SimulateUpdateButton), () => Press(simulate.Dial), isEnabled: on), isEnabled: on)));
            }
            if (Child(x, "help.developer.sendTestCrashReport") is { } test)
            {
                bool on = test.Dial.Enabled(facts).Enabled;
                items.Add(ChildHost(x, test, PlainItem(Loc.Get(PvDev.SendTestCrashReport), test.Caption,
                    StdButton(Loc.Get(PvDev.SendTestCrashReportButton), () => Press(test.Dial), isEnabled: on), isEnabled: dev && facts.CrashServiceConfigured)));
            }
            var header = BaseCard(x, Toggle(new Signal<bool>(dev), onChange: on =>
            {
                // The Appearance / Notifications developer-only rows are page-composed, so the writer asks the page to re-read.
                if (mode.Writer.Toggle!(on)) Settings.Bump();
            }));
            return Expander(header, items, x.Expanded);
        }
    }

    // ══ 6. LINKS AND LICENSES ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The licenses: Wavee's (MIT) and ONE expander for everything third-party, read from the generated file rather than restated in code; then
    /// the build's own words (unofficial, the OS, the data folder).</summary>
    static Element LicensesCard(RowCtx x) => new BoxEl
    {
        Direction = 1, Gap = WinUiSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Children =
        [
            LicenseExpander("Wavee", "MIT", Settings.WaveeLicense),
            LicenseExpander(Loc.Get(Strings.Settings.About.ThirdPartyNotices), Settings.NoticesFileName, Settings.ReadNotices()),
            DialCard(NewCard("", null, new BoxEl
            {
                Direction = 1, Gap = Spacing.XS, MinWidth = 0f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Settings.About.Unofficial)) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    new TextEl(Settings.OsDescription) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    new TextEl(Platform.LocalFolder) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                ],
            }) with { Form = CardForm.Left }),
        ],
    };

    /// <summary>A license: a collapsed expander whose one item is the body in 12-DIP Cascadia tertiary, with a flat never-wrapping item card. The style is built
    /// per call: it reads theme tokens.</summary>
    static Element LicenseExpander(string name, string kind, string body)
    {
        var style = new SettingsExpander.Style
        {
            ItemCardStyle = SettingsCard.DefaultStyle with
            {
                Padding = WinUiSettingsRules.LicensePadding, MinHeight = 0f, CornerRadius = 0f, WrapThreshold = 0f, WrapNoIconThreshold = 0f,
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
                    new TextEl(body) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                    alignment: SettingsCard.ContentAlignment.Left, style: style),
            ],
        });
    }
}
