// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.About.cs — the About tab (themes plan 6.1, T11b) ──────────────────────────────────────────
//
// From `Screens\Settings.UI.About.cs`. The update panel (the version hero + the W20 state matrix, the status card, channel, install-on-quit, metered download,
// what's-new auto-show — or the Store card), the links card, the "Wavee right now" receipts (the GPU line, a 5 s interval on a child), the two license expanders.
// The crash-reports card moved to Settings › Privacy & diagnostics (crash-diagnostics-implementation.md §D, §E).
//
// Everything with LIVE state is an embedded child component (the tab body is called behind the page's tab switch, so a hook in it would be conditional):
// `AboutUpdatePanel` reads `Notify.Update`, `ReceiptsCard` owns the interval and asks the core for one sample per tick (`Settings.SampleReceipts`: the process, the GPU,
// the frame counter; it also keeps the block's text for the diagnostics copy). The hero's state matrix (`Settings.AboutRules`), the verbs (`row.Writer`) and the
// notices file are the core's.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using AboutRules = Wavee.Settings.AboutRules;
using Tab = Wavee.Settings.Tab;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. THE TAB ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Order: update panel, links, the receipts, Licenses. Crash reports moved to Settings › General ›
    /// Privacy &amp; diagnostics and Settings › Logs › Reports (crash-diagnostics-implementation.md §D, §E).</summary>
    private static Element AboutTab() => TabStack(
        Embed.Comp(static () => new AboutUpdatePanel()) with { Key = "about:update" },
        AboutLinksCard(),
        SectionHeader(Loc.Get(Strings.Settings.About.RightNow), Icons.Info),
        Embed.Comp(static () => new ReceiptsCard()),
        SectionHeader(Loc.Get(Strings.Settings.About.Licenses), Icons.Document),
        LicenseExpander("Wavee", "MIT", Settings.WaveeLicense),
        // ONE expander for everything third-party, read from the generated file rather than restated in code.
        LicenseExpander(Loc.Get(Strings.Settings.About.ThirdPartyNotices), Settings.NoticesFileName, Settings.ReadNotices()));

    /// <summary>W19's links card: ContentAlignment.Left (no header, glyph or chevron at any width), the link column
    /// pulled flush to the card padding. "Open What's new" carries the unread dot while the running release's notes
    /// have never been opened — the ONLY unread affordance outside the notification centre.</summary>
    static Element AboutLinksCard()
    {
        Element whatsNew = LinkButton(Loc.Get(Strings.Update.About.OpenWhatsNew), static () => D(Tab.About, "whatsNew").Writer.Run!());
        if (ReleaseNotes.RunningNotesUnread())
            whatsNew = new BoxEl { Direction = 0, Gap = ClassicSettingsRules.AboutBadgeGap, AlignItems = FlexAlign.Center, Children = [whatsNew, InfoBadge.Dot()] };

        return SettingsCard.Create(new SettingsCard.Options
        {
            Alignment = SettingsCard.ContentAlignment.Left,
            Content = new BoxEl
            {
                Direction = 1, Gap = ClassicSettingsRules.CardSpacing, Margin = new Edges4(-12f, 0f, 0f, 0f),
                Children =
                [
                    whatsNew,
                    LinkButton(Loc.Get(Strings.Report.AboutReportProblem), static () => D(Tab.About, "reportProblem").Writer.Run!()),
                    LinkButton(Loc.Get(Strings.Report.AboutSuggestFeature), static () => D(Tab.About, "suggestFeature").Writer.Run!()),
                    LinkButton(Loc.Get(Strings.Report.AboutAllIssues), Settings.IssuesUrl),
                    LinkButton(Loc.Get(Strings.Settings.About.Website), Settings.WebsiteUrl),
                    LinkButton(Loc.Get(Strings.Settings.About.PrivacyPolicy), Settings.PrivacyUrl),
                    LinkButton(Loc.Get(Strings.Settings.About.ThirdPartyNotices), static () => D(Tab.About, "thirdPartyNotices").Writer.Run!()),
                    LinkButton(Loc.Get(Strings.Settings.About.CopyDiagnostics), static () => D(Tab.About, "copyDiagnostics").Writer.Run!()),
                    LinkButton(Loc.Get(Strings.Settings.About.OpenDataFolder), static () => D(Tab.About, "openDataFolder").Writer.Run!()),
                    new TextEl(Loc.Get(Strings.Settings.About.Unofficial)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    new TextEl(Settings.OsDescription) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    new TextEl(Platform.LocalFolder) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
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
                    new TextEl(body) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                    alignment: SettingsCard.ContentAlignment.Left, style: style),
            ],
        });
    }

    // ══ 2. THE UPDATE PANEL (W19 hero + cards, W20 matrix) ═════════════════════════════════════════════════════════

    /// <summary>Re-renders on every published snapshot (it reads <see cref="Notify.Update"/>). A Store install has no
    /// feed, channel, install-on-quit or metered gate — the Store owns all four — so one card replaces them.</summary>
    sealed class AboutUpdatePanel : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var s = Notify.Update.Value;
            var me = Platform.Version;
            var kids = new List<Element>(6) { UpdateHero(me, s) };
            if (me.IsStore)
            {
                kids.Add(DocRow(D(Tab.About, "storeOpen"),
                    StdButton(Loc.Get(Strings.Update.Store.Open), static () => D(Tab.About, "storeOpen").Writer.Run!()), icon: Icons.Refresh));
            }
            else
            {
                kids.Add(UpdateStatusCard(s));
                // A LINK, not a picker: Beta installs beside Stable, nothing in this app changes when it is clicked.
                kids.Add(DocRow(D(Tab.About, "channel"),
                    LinkButton(Loc.Get(Strings.Update.Channel.Beta), Settings.BetaFeedUrl), icon: Icons.Devices));
                kids.Add(DocRow(D(Tab.About, "installOnQuit"), RowToggle(D(Tab.About, "installOnQuit")), icon: Icons.Download));
                kids.Add(DocRow(D(Tab.About, "updateOnMetered"), RowToggle(D(Tab.About, "updateOnMetered")), icon: Icons.RadioTower));
            }
            kids.Add(DocRow(D(Tab.About, "autoShow"), RowToggle(D(Tab.About, "autoShow")), icon: Icons.RefineSparkle));
            return new BoxEl { Direction = 1, Gap = ClassicSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
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
        pills.Add(new TextEl((me.Commit.Length > 0 ? me.Commit + " · " : "") + Settings.ArchToken)
            { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" });

        // The verb is the row's writer, chosen from the live state at the press (check · update now · retry · nothing).
        Action act = shape.Verb == AboutRules.HeroVerb.None
            ? static () => { }
            : static () => D(Tab.About, "updateHero").Writer.Run!();
        var right = new List<Element>(3)
        {
            Btn(Loc.Get(shape.ButtonKey), act,
                shape.Tone == AboutRules.HeroTone.Accent ? ButtonAppearance.Accent : ButtonAppearance.Standard,
                shape.Tone != AboutRules.HeroTone.Disabled),
        };
        if (me.Codename.Length > 0)
            right.Add(LinkButton(Strings.Update.About.WhatsNewIn(me.Codename), static () => D(Tab.About, "whatsNew").Writer.Run!()));
        // The after-update plate on demand; a dev build has no update story to summarize.
        if (SettingsContext.Overlay is not null && ReleaseNotes.CanShowSummaryAgain)
            right.Add(LinkButton(Loc.Get(Strings.Update.About.ShowSummaryAgain), static () => D(Tab.About, "showSummaryAgain").Writer.Run!()));

        long lastChecked = s.LastCheckedMs > 0 ? s.LastCheckedMs : SettingsFacts.UpdateLastCheckedMs();
        string provenance = AboutRules.Provenance(me.BuildDate, lastChecked, static d => Strings.Update.About.Built(d),
            static w => Strings.Update.About.LastChecked(w), Loc.Get(Strings.Update.About.NeverChecked));

        // The hero's three parts sit in a row; where the tier stacks its controls (Compact) there is no room for the tile, the words and the buttons side by side:
        // they stack, the buttons under the words.
        bool stack = s_layout.StacksControls;
        return new BoxEl
        {
            Direction = stack ? (byte)1 : (byte)0, Gap = ClassicSettingsRules.HeroGap, AlignItems = stack ? FlexAlign.Start : FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = new Edges4(24f, 22f, 24f, 22f), Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Width = ClassicSettingsRules.HeroTile, Height = ClassicSettingsRules.HeroTile, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(16f), Fill = Tok.AccentSubtle,
                    Children = [Icon(Icons.MusicNote, 30f, Tok.AccentTextPrimary)],
                },
                new BoxEl
                {
                    Direction = 1, Gap = ClassicSettingsRules.HeroTextGap, Grow = 1f, Shrink = 1f, Basis = stack ? float.NaN : 0f, MinWidth = 0f, AlignSelf = stack ? FlexAlign.Stretch : FlexAlign.Auto,
                    Children =
                    [
                        new TextEl(ReleaseNotes.VersionDisplayText()) { Size = ClassicSettingsRules.HeroVersionSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                        new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = pills.ToArray() },
                        new TextEl(provenance) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    ],
                },
                new BoxEl { Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = stack ? FlexAlign.Start : FlexAlign.End, Children = right.ToArray() },
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
                    ProgressBar.Determinate(s.ProgressPercent / 100f, ClassicSettingsRules.UpdateProgressWidth),
                    new TextEl(s.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%")
                        { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
                ],
            },
            // The release that FAILED, not the bare listing (`Update.ReleasePageUrl` is the one owner of that rule).
            AppUpdateState.Failed => StdButton(Loc.Get(Strings.Update.Action.OpenReleasePage),
                static () => D(Tab.About, "updateOpenReleasePage").Writer.Run!()),
            AppUpdateState.Completed => SubtleButton(Loc.Get(Strings.Update.Action.Dismiss),
                static () => D(Tab.About, "updateDismiss").Writer.Run!()),
            _ => null,
        };
        return Row(Loc.Get(Strings.Update.Status.Title), Notify.AppUpdateToasts.StateSentence(s), content, Icons.Refresh);
    }

    // ══ 3. "WAVEE RIGHT NOW" (N12: a 5 000 ms interval on THIS child; Render never reads process/GPU/FPS) ═════════

    sealed class ReceiptsCard : SettingsComponent
    {
        readonly Signal<string> _gpu = new(Settings.Receipts.Dash), _workingSet = new(Settings.Receipts.Dash), _managed = new(Settings.Receipts.Dash),
            _uptime = new(Settings.Receipts.Dash), _fps = new(Settings.Receipts.Dash), _zoom = new(Settings.Receipts.Dash), _gpuAssets = new(Settings.Receipts.Dash),
            _appExcl = new(Settings.Receipts.Dash), _detail = new(Settings.Receipts.Dash);

        public override Element Render()
        {
            UseLayout();
            UseEffect(Tick, DepKey.Empty);
            UseInterval(Tick, ClassicSettingsRules.ReceiptsTickMs);
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
                        new TextEl(_detail) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    ],
                },
            });
        }

        static Element Line(Signal<string> value, string label) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(label) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, Shrink = 0f },
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
}
