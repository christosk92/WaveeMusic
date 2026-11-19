// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.About.cs — Help & about's cards (settings rework 7.1) ─────────────────────────────────────
//
// From `Screens\Settings.UI.About.cs`. About Wavee: the update hero (the version, the W20 state matrix, its one button) over the status card, the Store card or
// the channel link (a Store install has no feed, channel, install-on-quit or metered gate: the Store owns all four), "Wavee right now" (the receipts, a 5 s
// interval on a child), What's new (the unread dot) and the update summary again. Feedback's "What a report holds" expander. Links and licenses: the links card
// (left-aligned links, each its own dial, then the unofficial line, the OS and the data folder) and the two license expanders. The keyboard shortcuts are one
// collapsed expander (owner decision 1).
//
// Everything with LIVE state is an embedded child component: `UpdateHeroCard` reads `Notify.Update`, `ReceiptsCard` owns the interval and asks the core for one
// sample per tick (`Settings.SampleReceipts`). The hero's state matrix (`Settings.AboutRules`), the verbs (the dials' writers) and the notices file are the core's.

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

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. ABOUT WAVEE ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A Help & about dial's card; false for one the generic grammar draws. A dial a Store install does not have (or only a Store install has) is
    /// handled with no card.</summary>
    static bool TryAboutCard(SettingsBodyRow row, SettingsBodyGroup? group, out Element? card)
    {
        bool store = Platform.Version.IsStore;
        card = null;
        switch (row.Id)
        {
            case "help.update.hero":
                card = Embed.Comp(new HeroProps(row), static () => new UpdateHeroCard()) with { Key = "about:update" };
                return true;
            case "help.update.store":
                if (store) card = CardRow(row, StdButton(Loc.Get(Strings.Update.Store.Open), static () => Dial("help.update.store").Writer.Run!()));
                return true;
            case "help.update.channel":
                // A LINK, not a picker: Beta installs beside Stable, nothing in this app changes when it is clicked.
                if (!store) card = CardRow(row, LinkButton(Loc.Get(Strings.Update.Channel.Beta), static () => Dial("help.update.channel").Writer.Run!()));
                return true;
            case "help.update.installOnQuit" or "help.update.onMetered":
                return store;   // a Store install: no card; otherwise the generic switch
            case "help.receipts":
                card = Embed.Comp(static () => new ReceiptsCard());
                return true;
            case "help.whatsNew":
                // The unread dot while the running release's notes were never opened: the ONLY unread affordance outside the notification centre.
                card = CardRow(row, ReleaseNotes.RunningNotesUnread() ? InfoBadge.Dot() : null, isClickEnabled: true,
                    onClick: static () => Dial("help.whatsNew").Writer.Run!());
                return true;
            case "help.showSummaryAgain":
                // The after-update plate on demand; a dev build has no update story to summarize.
                if (SettingsContext.Overlay is not null && ReleaseNotes.CanShowSummaryAgain)
                    card = CardRow(row, isClickEnabled: true, onClick: static () => Dial("help.showSummaryAgain").Writer.Run!());
                return true;
            case "help.feedback.reportHolds":
                card = ReportHoldsExpander(row);
                return true;
            case "help.links.allIssues":
                card = group is null ? null : LinksCard(group);
                return group is not null;
            case "help.links.website" or "help.links.thirdPartyNotices" or "help.links.openDataFolder":
                return group is not null;   // the links card draws them (a result draws its own clickable row)
            case "help.links.privacyPolicy":
                return true;   // a duplicate of Account & privacy › Privacy policy: it left with the old About page (one home per verb)
            case "help.links.licenses":
                card = new BoxEl
                {
                    Direction = 1, Gap = ClassicSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        LicenseExpander("Wavee", "MIT", Settings.WaveeLicense),
                        // ONE expander for everything third-party, read from the generated file rather than restated in code.
                        LicenseExpander(Loc.Get(Strings.Settings.About.ThirdPartyNotices), Settings.NoticesFileName, Settings.ReadNotices()),
                    ],
                };
                return true;
        }
        return false;
    }

    sealed record HeroProps(SettingsBodyRow Row);

    /// <summary>The update hero over its status card (W19, W20). Re-renders on every published snapshot (it reads <see cref="Notify.Update"/>). Its two
    /// child dials (Open release page after a failure, Dismiss after an update) are the status card's buttons, keyed as their rows.</summary>
    sealed class UpdateHeroCard : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var p = UseProps<HeroProps>();
            var s = Notify.Update.Value;
            var me = Platform.Version;
            return new BoxEl
            {
                Direction = 1, Gap = ClassicSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch,
                Children = me.IsStore ? [UpdateHero(me, s)] : [UpdateHero(me, s), UpdateStatusCard(p.Row, s)],
            };
        }
    }

    /// <summary>The hero: a 64² accent-subtle tile, the 24/600 version line, a wrapping pill row (quad MONO · channel · state) + the commit/arch stamp, the
    /// provenance line; right column = the primary button. What's new and the update summary are rows of their own under it (one home per verb).</summary>
    static Element UpdateHero(WaveeVersionInfo me, AppUpdateSnapshot s)
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
            { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, FontFamily = "Cascadia Code" });

        // The verb is the dial's writer, chosen from the live state at the press (check · update now · retry · nothing).
        Action act = shape.Verb == AboutRules.HeroVerb.None
            ? static () => { }
            : static () => Dial("help.update.hero").Writer.Run!();
        Element button = Btn(Loc.Get(shape.ButtonKey), act,
            shape.Tone == AboutRules.HeroTone.Accent ? ButtonAppearance.Accent : ButtonAppearance.Standard,
            shape.Tone != AboutRules.HeroTone.Disabled);

        long lastChecked = s.LastCheckedMs > 0 ? s.LastCheckedMs : SettingsFacts.UpdateLastCheckedMs();
        string provenance = AboutRules.Provenance(me.BuildDate, lastChecked, static d => Strings.Update.About.Built(d),
            static w => Strings.Update.About.LastChecked(w), Loc.Get(Strings.Update.About.NeverChecked));

        // The hero's three parts sit in a row; where the tier stacks its controls (Compact) there is no room for the tile, the words and the button side by
        // side: they stack, the button under the words.
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
                new BoxEl { Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignItems = stack ? FlexAlign.Start : FlexAlign.End, Children = [button] },
            ],
        };
    }

    /// <summary>The status card: the ONE state sentence (`Notify.AppUpdateToasts.StateSentence`), plus progress while downloading/installing, "Open release
    /// page" on a failure, "Dismiss" after an update (each its own child dial).</summary>
    static Element UpdateStatusCard(SettingsBodyRow hero, AppUpdateSnapshot s)
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
            AppUpdateState.Failed => ChildFrame(hero, "help.update.openReleasePage", StdButton(Loc.Get(Strings.Update.Action.OpenReleasePage),
                static () => Dial("help.update.openReleasePage").Writer.Run!())),
            AppUpdateState.Completed => ChildFrame(hero, "help.update.dismiss", SubtleButton(Loc.Get(Strings.Update.Action.Dismiss),
                static () => Dial("help.update.dismiss").Writer.Run!())),
            _ => null,
        };
        return Row(Loc.Get(Strings.Update.Status.Title), Notify.AppUpdateToasts.StateSentence(s), content, Icons.Refresh);
    }

    // ══ 2. "WAVEE RIGHT NOW" (N12: a 5 000 ms interval on THIS child; Render never reads process/GPU/FPS) ═════════

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
                        BodyStrong(Loc.Get(Strings.Settings.About.RightNow)),
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

    // ══ 3. FEEDBACK, SHORTCUTS, LINKS AND LICENSES ═════════════════════════════════════════════════════════════════

    /// <summary>What the report dialog sends: the four lines it states, then its own sentence on what is sent and when.</summary>
    static Element ReportHoldsExpander(SettingsBodyRow row) => SettingsExpander.Create(new SettingsExpander.Options
    {
        Header = row.Label,
        Description = Loc.Get(Strings.Report.Subtitle),
        HeaderIcon = Icons.Shield,
        IsExpanded = ExpandCell(row.Id, false),
        Items =
        [
            Item(Loc.Get(Strings.Settings.Feedback.HoldsWrite), null, icon: Icons.Accept),
            Item(Loc.Get(Strings.Settings.Feedback.HoldsVersions), null, icon: Icons.Accept),
            Item(Strings.Settings.Feedback.HoldsLogs(Feedback.ReportBundle.ManualLogLines), null, icon: Icons.Accept),
            Item(Loc.Get(Strings.Settings.Feedback.HoldsNever), null, icon: Icons.Cancel),
        ],
    }) with { Key = "feedback.holds" };

    /// <summary>The keyboard shortcuts (a READ-ONLY reference of the chords the shell defines) as ONE collapsed expander (owner decision 1): its title is the
    /// group's, each chord an item with its key chip, keyed as its row.</summary>
    static Element ShortcutsBlock(SettingsBodyGroup group, string slug)
    {
        var items = new List<Element>(group.Rows.Length);
        foreach (SettingsBodyRow row in group.Rows)
            items.Add(Framed(row, Item(row.Label, row.Description, KeyChip(row.Value as string ?? ""))));
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = group.Title,
            Description = group.Subtitle,
            HeaderIcon = Icons.Font,
            IsExpanded = ExpandCell(ShortcutsCell, false),
            Items = items,
        }) with { Key = "settings:shortcuts:" + slug };
    }

    static Element KeyChip(string chord) => new BoxEl
    {
        Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(ClassicSettingsRules.KeyChipPadX, ClassicSettingsRules.KeyChipPadY, ClassicSettingsRules.KeyChipPadX, ClassicSettingsRules.KeyChipPadY),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Corners = CornerRadius4.All(Spacing.XXS), HitTestVisible = false,
        Children = [new TextEl(Settings.Shortcuts.Words(chord)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };

    /// <summary>W19's links card: ContentAlignment.Left (no header, glyph or chevron at any width), the link column pulled flush to the card padding, each link
    /// its own dial (keyed as its row), then the unofficial line, the OS and the data folder.</summary>
    static Element LinksCard(SettingsBodyGroup group)
    {
        var kids = new List<Element>(group.Rows.Length + 3);
        foreach (SettingsBodyRow row in group.Rows)
        {
            if (row.Kind != SettingsRowKind.Action || string.Equals(row.Id, "help.links.privacyPolicy", StringComparison.Ordinal)) continue;
            SettingsDial dial = row.Dial;
            kids.Add(Framed(row, LinkButton(row.Label, () => dial.Writer.Run!())));
        }
        kids.Add(new TextEl(Loc.Get(Strings.Settings.About.Unofficial)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
        kids.Add(new TextEl(Settings.OsDescription) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        kids.Add(new TextEl(Platform.LocalFolder) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap });
        return SettingsCard.Create(new SettingsCard.Options
        {
            Alignment = SettingsCard.ContentAlignment.Left,
            Content = new BoxEl
            {
                Direction = 1, Gap = ClassicSettingsRules.CardSpacing, Margin = new Edges4(-12f, 0f, 0f, 0f), AlignItems = FlexAlign.Start,
                Children = kids.ToArray(),
            },
        });
    }

    /// <summary>A license: a collapsed expander whose one item is the body in 12-DIP Cascadia tertiary, with a flat never-wrapping item card (Pad 16,12,16,16 ·
    /// MinH 0 · r 0). The style is built per call: it reads theme tokens.</summary>
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
}
