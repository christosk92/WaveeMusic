// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.About.cs — the About tab (themes plan 6.3, Z8b) ───────────────────────────────────────────
//
// Board Z-Settings-Sections ("about": the wordmark and version as a numeral, the update line, the doors) and Z-Feedback ("feedback & help": report a bug, suggest a feature,
// all issues, the changelog). The update panel (the version hero and the state matrix, the status row, channel, install-on-quit, metered download, what's-new auto-show, or the
// Store row), the links, the "Wavee right now" receipts (a 5 s interval on a child) and the two licences.
//
// Everything with LIVE state is an embedded child component (the tab body is called behind the page's tab switch, so a hook in it would be conditional): the update panel
// reads `Notify.Update`, the receipts own their interval and ask the core for one sample per tick. The hero's state matrix (`Settings.AboutRules`), the verbs (`row.Writer`)
// and the notices file are the core's.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using static FluentGpu.Dsl.Ui;
using AboutRules = Wavee.Settings.AboutRules;
using Tab = Wavee.Settings.Tab;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    static Named[] AboutTab(ZuneKit k) =>
    [
        new Named("Update", Embed.Comp(static () => new UpdatePanel()) with { Key = "zs:about:update" }),
        new Named("Links", Group(k, "zs:section:about:links", Loc.Get(Strings.Settings.Tabs.About), null, LinksRow(k))),
        new Named("Right now", Group(k, "zs:section:about:right-now", Loc.Get(Strings.Settings.About.RightNow), null, Embed.Comp(static () => new ReceiptsRows()) with { Key = "zs:receipts" })),
        new Named("Licenses", Group(k, "zs:section:about:licenses", Loc.Get(Strings.Settings.About.Licenses), null,
            Embed.Comp(static () => new LicenseRow("Wavee", "MIT", Settings.WaveeLicense)) with { Key = "zs:license:wavee" },
            Embed.Comp(static () => new LicenseRow(Loc.Get(Strings.Settings.About.ThirdPartyNotices), Settings.NoticesFileName, Settings.ReadNotices())) with { Key = "zs:license:third-party" })),
    ];

    /// <summary>The doors: what's new (with the unread dot while the running release's notes have never been opened: the ONLY unread affordance outside the notification
    /// centre), all issues, the website, privacy, the notices, open the data folder; then the unofficial line, the OS and the data folder. (Report a problem, suggest a
    /// feature and copy diagnostics are Feedback &amp; help's.)</summary>
    static Element LinksRow(ZuneKit k)
    {
        Element whatsNew = k.Cmd(Loc.Get(Strings.Update.About.OpenWhatsNew), static () => D(Tab.About, "whatsNew").Writer.Run!());
        if (ReleaseNotes.RunningNotesUnread())
            whatsNew = new BoxEl { Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [whatsNew, InfoBadge.Dot()] };
        return new BoxEl
        {
            Key = "zs:about:links", Direction = 1, Gap = Z.LabelGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = Z.LinkGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        whatsNew,
                        k.Link(Loc.Get(Strings.Report.AboutAllIssues), Settings.IssuesUrl),
                        k.Link(Loc.Get(Strings.Settings.About.Website), Settings.WebsiteUrl),
                        k.Link(Loc.Get(Strings.Settings.About.PrivacyPolicy), Settings.PrivacyUrl),
                        k.Cmd(Loc.Get(Strings.Settings.About.ThirdPartyNotices), static () => D(Tab.About, "thirdPartyNotices").Writer.Run!()),
                        k.Cmd(Loc.Get(Strings.Settings.About.OpenDataFolder), static () => D(Tab.About, "openDataFolder").Writer.Run!()),
                    ],
                },
                ZuneText.Hint(Loc.Get(Strings.Settings.About.Unofficial)),
                ZuneText.Hint(Settings.OsDescription) with { Color = Tok.TextSecondary },
                ZuneText.Hint(Platform.LocalFolder) with { Color = Tok.TextSecondary, FontFamily = Z.Mono },
            ],
        };
    }

    // ══ the update panel ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Re-renders on every published snapshot (it reads `Notify.Update`). A Store install has no feed, channel, install-on-quit or metered gate (the Store owns all
    /// four), so one row replaces them.</summary>
    sealed class UpdatePanel : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var s = Notify.Update.Value;
            var me = Platform.Version;
            var rows = new List<Element>(6);
            if (me.IsStore)
            {
                var open = D(Tab.About, "storeOpen");
                rows.Add(k.DocRow(open, k.Go(Loc.Get(Strings.Update.Store.Open), () => open.Writer.Run!(), glyph: Icons.Refresh)));
            }
            else
            {
                rows.Add(StatusRow(k, s));
                // A LINK, not a picker: Beta installs beside Stable, nothing in this app changes when it is clicked.
                rows.Add(k.DocRow(D(Tab.About, "channel"), k.Link(Loc.Get(Strings.Update.Channel.Beta), Settings.BetaFeedUrl)));
                rows.Add(k.DocRow(D(Tab.About, "installOnQuit"), k.Toggle(D(Tab.About, "installOnQuit"))));
                rows.Add(k.DocRow(D(Tab.About, "updateOnMetered"), k.Toggle(D(Tab.About, "updateOnMetered"))));
            }
            rows.Add(k.DocRow(D(Tab.About, "autoShow"), k.Toggle(D(Tab.About, "autoShow"))));
            return new BoxEl
            {
                Key = "zs:about:panel", Direction = 1, Gap = Z.SectionGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children = [Hero(k, me, s), new BoxEl { Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = rows.ToArray() }],
            };
        }

        /// <summary>The version as a numeral, the line under it (the build quad in mono, the channel, the state, the commit and arch, the provenance) and the primary action as the
        /// round accent button with its word beside it, with up to two commands.</summary>
        static Element Hero(ZuneKit k, WaveeVersionInfo me, AppUpdateSnapshot s)
        {
            // A dev build has nothing for the feed to be newer than, unless a developer-mode SIMULATION is walking, whose states are real enough to press.
            var shape = AboutRules.Hero(s.State, me.IsDev && !Update.Host.IsSimulating, me.IsStore);
            string channel = Loc.Get(me.Channel switch
            {
                "beta" => Strings.Update.About.ChannelBeta,
                "stable" => Strings.Update.About.ChannelStable,
                _ => Strings.Update.About.ChannelDev,
            });
            var line = new List<string>(4);
            if (me.Quad.Length > 0) line.Add(me.Quad);
            line.Add(channel);
            line.Add(Loc.Get(shape.PillKey));
            line.Add((me.Commit.Length > 0 ? me.Commit + " · " : "") + Settings.ArchToken);

            Action act = shape.Verb == AboutRules.HeroVerb.None ? static () => { } : static () => D(Tab.About, "updateHero").Writer.Run!();
            long lastChecked = s.LastCheckedMs > 0 ? s.LastCheckedMs : SettingsFacts.UpdateLastCheckedMs();
            string provenance = AboutRules.Provenance(me.BuildDate, lastChecked, static d => Strings.Update.About.Built(d),
                static w => Strings.Update.About.LastChecked(w), Loc.Get(Strings.Update.About.NeverChecked));

            var doors = new List<Element>(3)
            {
                k.Go(Loc.Get(shape.ButtonKey), act, shape.Tone != AboutRules.HeroTone.Disabled,
                    shape.Tone == AboutRules.HeroTone.Accent ? Icons.Forward : Icons.Refresh),
            };
            if (me.Codename.Length > 0)
                doors.Add(k.Cmd(Strings.Update.About.WhatsNewIn(me.Codename), static () => D(Tab.About, "whatsNew").Writer.Run!()));
            // The after-update plate on demand; a dev build has no update story to summarize.
            if (SettingsContext.Overlay is not null && ReleaseNotes.CanShowSummaryAgain)
                doors.Add(k.Cmd(Loc.Get(Strings.Update.About.ShowSummaryAgain), static () => D(Tab.About, "showSummaryAgain").Writer.Run!()));

            return new BoxEl
            {
                Key = "zs:about:hero", Direction = 1, Gap = Z.LabelGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    ZuneText.Cased(R.Type.Hero(ReleaseNotes.VersionDisplayText()), LabelKind.Heading) with { MinWidth = 0f, Wrap = TextWrap.Wrap },
                    R.Type.DenseMeta(string.Join("  ·  ", line)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, FontFamily = Z.Mono },
                    ZuneText.Hint(provenance),
                    new BoxEl
                    {
                        Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Z.LinkGap, MinWidth = 0f, Margin = new Edges4(0f, Z.ControlGap, 0f, 0f),
                        Children = doors.ToArray(),
                    },
                ],
            };
        }

        /// <summary>The status row: the ONE state sentence (`Notify.AppUpdateToasts.StateSentence`), plus progress while downloading or installing, "open release page" on a
        /// failure, "dismiss" after an update.</summary>
        static Element StatusRow(ZuneKit k, AppUpdateSnapshot s)
        {
            Element? content = s.State switch
            {
                AppUpdateState.Downloading or AppUpdateState.Installing => new BoxEl
                {
                    Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        ZuneKit.Bar(s.ProgressPercent / 100f, Z.UpdateProgressWidth),
                        R.Type.DenseMeta(s.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%") with { Color = Tok.TextSecondary, FontFamily = Z.Mono },
                    ],
                },
                // The release that FAILED, not the bare listing (`Update.ReleasePageUrl` is the one owner of that rule).
                AppUpdateState.Failed => k.Cmd(Loc.Get(Strings.Update.Action.OpenReleasePage), static () => D(Tab.About, "updateOpenReleasePage").Writer.Run!()),
                AppUpdateState.Completed => k.Cmd(Loc.Get(Strings.Update.Action.Dismiss), static () => D(Tab.About, "updateDismiss").Writer.Run!()),
                _ => null,
            };
            return k.Row("zs:row:update-status", Loc.Get(Strings.Update.Status.Title), Notify.AppUpdateToasts.StateSentence(s), content);
        }
    }

    // ══ "WAVEE RIGHT NOW" ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A 5 000 ms interval on THIS child; Render never reads process, GPU or FPS.</summary>
    sealed class ReceiptsRows : ZuneScreenComponent
    {
        readonly Signal<string> _gpu = new(Settings.Receipts.Dash), _workingSet = new(Settings.Receipts.Dash), _managed = new(Settings.Receipts.Dash),
            _uptime = new(Settings.Receipts.Dash), _fps = new(Settings.Receipts.Dash), _zoom = new(Settings.Receipts.Dash), _gpuAssets = new(Settings.Receipts.Dash),
            _appExcl = new(Settings.Receipts.Dash), _detail = new(Settings.Receipts.Dash);

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            UseEffect(Tick, DepKey.Empty);
            UseInterval(Tick, Z.ReceiptsTickMs);
            return new BoxEl
            {
                Key = "zs:receipts:rows", Direction = 1, Gap = Z.LabelGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    Line(k, "GPU", _gpu), Line(k, "Working set", _workingSet), Line(k, "Managed heap", _managed), Line(k, "Uptime", _uptime),
                    Line(k, "FPS", _fps), Line(k, "Zoom", _zoom), Line(k, "GPU assets", _gpuAssets), Line(k, "App memory excl. GPU assets", _appExcl),
                    ZuneText.Hint(_detail.Value),
                ],
            };
        }

        static Element Line(ZuneKit k, string label, Signal<string> value) => k.Row("zs:receipt:" + label, label, null,
            R.Type.DenseMeta(value.Value) with { Color = Tok.TextPrimary, MinWidth = 0f, Wrap = TextWrap.Wrap, Grow = 1f });

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

    // ══ licences ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A licence: its name and kind, and a command that opens and closes the text (12-DIP mono, tertiary). Closed until asked for.</summary>
    sealed class LicenseRow(string name, string kind, string body) : ZuneScreenComponent
    {
        readonly Signal<bool> _open = new(false);

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            bool open = _open.Value;
            var kids = new List<Element>(2)
            {
                k.Row("zs:row:license:" + name, name, kind, k.Cmd(Loc.Get(open ? Strings.Common.Less : Strings.Common.More), () => _open.Value = !_open.Peek())),
            };
            if (open)
                kids.Add(R.Type.MicroMeta(body) with { Key = "zs:license:body:" + name, Color = Tok.TextTertiary, FontFamily = Z.Mono, Wrap = TextWrap.Wrap, MinWidth = 0f });
            return new BoxEl { Direction = 1, Gap = Z.LabelGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
        }
    }
}
