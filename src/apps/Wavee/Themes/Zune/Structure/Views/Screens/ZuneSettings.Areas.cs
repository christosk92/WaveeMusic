// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Areas.cs — the four sections the boards add, and the area composer (themes plan 6.3) ──────────
//
// Boards: Z-Settings-Sections (account, devices & connect, keyboard), Z-Feedback and its tiers. The twelve areas are `Settings.Area` (`Settings.Areas.cs`): each a list of
// document sections. `AreaGroups` draws the open area by asking each tab for its groups once and keeping the ones the area names; the groups are the same nodes whichever
// area shows them. The four sections here have no old tab: Account (the name and the plan, the profile, Spotify's account page, Log out), Devices (this PC's Connect name, the
// Connect diagnostics page), Keyboard (a READ-ONLY reference of the chords the shell defines) and Feedback (report a problem, suggest something, ask a question, what a
// report holds, copy diagnostics). Every control writes through its document row's writer.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Tab = Wavee.Settings.Tab;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    // ══ the area composer ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The groups of <paramref name="area"/>, in the area's order: each tab's groups are built once, and every part the area names picks its group(s) out.</summary>
    static Element[] AreaGroups(ZuneKit k, Settings.Area area, SettingsPageProps props)
    {
        var parts = Settings.PartsOf(area);
        var built = new Dictionary<Tab, Named[]>(4);
        var groups = new List<Element>(parts.Count + 1);
        foreach (var part in parts)
        {
            if (!built.TryGetValue(part.Tab, out var tab)) built[part.Tab] = tab = TabGroups(k, part.Tab, props);
            foreach (var named in tab)
                if (string.Equals(named.Section, part.Section, StringComparison.Ordinal)) groups.Add(named.El);
        }
        return groups.ToArray();
    }

    static Named[] TabGroups(ZuneKit k, Tab tab, SettingsPageProps props) => tab switch
    {
        Tab.General => GeneralTab(k),
        Tab.Appearance => AppearanceTab(k),
        Tab.Playback => PlaybackTab(k),
        Tab.Notifications => NotificationsTab(k),
        Tab.Storage => StorageTab(k, props.Storage),
        Tab.PrivacyDiagnostics => PrivacyTab(k),
        Tab.About => AboutTab(k),
        Tab.Account => AccountTab(k),
        Tab.Devices => DevicesTab(k),
        Tab.Keyboard => KeyboardTab(k),
        _ => FeedbackTab(k),
    };

    /// <summary>A document section drawn without a heading of its own: its caption leads it as a sentence (the page's heading already names it).</summary>
    static Named Bare(ZuneKit k, Tab tab, string section, params Element?[] rows)
    {
        var doc = SettingsDoc.Section(tab, section);
        var kids = new List<Element>(rows.Length);
        foreach (var row in rows)
            if (row is not null) kids.Add(row);
        return new Named(section, k.Block("zs:section:" + (int)tab + ":" + section, null, doc.SubtitleKey is { } sub ? Loc.Get(sub) : null, kids));
    }

    // ══ account ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Named[] AccountTab(ZuneKit k)
    {
        string name = D(Tab.Account, "identity").Current as string ?? "";
        int tier = D(Tab.Account, "plan").Current is int t ? t : 0;
        Element picture = name.Length > 0
            ? PersonPicture.Create("", Z.AccountAvatar, displayName: name, imageSourcePath: SettingsFacts.AccountAvatarUrl())
            : ZuneOverlayParts.SkeletonCircle(Z.AccountAvatar);
        var lines = new List<Element>(2)
        {
            name.Length > 0
                ? R.Type.SheetTitle(name) with { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                : ZuneOverlayParts.Skeleton(ZuneOverlayRules.HeaderNameBarWidth, ZuneOverlayRules.ChipNameBarHeight),
        };
        if (tier != 0)
            lines.Add(ZuneText.Cased(R.Type.DenseMeta(Loc.Get(tier == 2 ? Strings.Auth.PremiumBadge : Strings.Auth.FreeBadge)), LabelKind.Label)
                with { Color = tier == 2 ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1, MinWidth = 0f });
        Element card = new BoxEl
        {
            Key = "zs:account:card", Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.AccountGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = Z.AccountAvatar,
            Children = [picture, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = lines.ToArray() }],
        };

        var manage = D(Tab.Account, "manageAccount");
        var profile = D(Tab.Account, "profile");
        var signOut = D(Tab.Account, "signOut");
        Element commands = new BoxEl
        {
            Key = "zs:account:commands", Direction = 0, Wrap = true, Gap = Z.LinkGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                k.Cmd(Loc.Get(manage.TitleKey), () => manage.Writer.Run!()),
                k.Cmd(Loc.Get(profile.TitleKey), () => profile.Writer.Run!()),
                k.Cmd(Loc.Get(signOut.TitleKey), () => signOut.Writer.Run!()),
            ],
        };
        return [Bare(k, Tab.Account, "Account", card, commands)];
    }

    // ══ devices & connect ════════════════════════════════════════════════════════════════════════════════════════════════

    static Named[] DevicesTab(ZuneKit k)
    {
        var thisPc = D(Tab.Devices, "thisPc");
        var diagnostics = D(Tab.Devices, "connectDiagnostics");
        return
        [
            Bare(k, Tab.Devices, "Connect",
                k.DocRow(thisPc, R.Type.TrackTitle(thisPc.Current as string ?? "") with { Color = Tok.TextPrimary, MinWidth = 0f, Wrap = TextWrap.Wrap, Grow = 1f }),
                k.DocRow(diagnostics, k.Cmd(Loc.Get(diagnostics.TitleKey), () => diagnostics.Writer.Run!()))),
        ];
    }

    // ══ keyboard ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Named[] KeyboardTab(ZuneKit k)
    {
        var rows = new List<Element?>(Settings.Shortcuts.All.Length);
        foreach (var row in SettingsDoc.Section(Tab.Keyboard, "Shortcuts").Rows)
            if (Shown(row)) rows.Add(k.DocRow(row, KeyChip(row.Current as string ?? "")));
        return [Bare(k, Tab.Keyboard, "Shortcuts", rows.ToArray())];
    }

    static Element KeyChip(string chord) => new BoxEl
    {
        Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = new Edges4(Z.KeyChipPadX, Z.KeyChipPadY, Z.KeyChipPadX, Z.KeyChipPadY),
        BorderWidth = Z.KeyChipStroke, BorderColor = Tok.StrokeCardDefault, HitTestVisible = false,
        Children = [ZuneText.Cased(R.Type.MicroMeta(Settings.Shortcuts.Words(chord)), LabelKind.Label) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };

    // ══ feedback & help ══════════════════════════════════════════════════════════════════════════════════════════════════

    static Named[] FeedbackTab(ZuneKit k)
    {
        var report = D(Tab.Feedback, "reportProblem");
        var suggest = D(Tab.Feedback, "suggestFeature");
        var ask = D(Tab.Feedback, "askQuestion");
        var copy = D(Tab.Feedback, "copyDiagnostics");
        // Where the tier has an aside the contents list sits beside the column (Z-Feedback); where it has none it follows the actions.
        Element? holds = k.Lay.ShowsAside ? null : HoldsCard(k);
        Element? help = k.Lay.ShowsAside ? null : HelpLinks(k);
        return
        [
            Bare(k, Tab.Feedback, "Help", ActionRow(report, "!", accent: true), ActionRow(suggest, "+", accent: false), ActionRow(ask, "?", accent: false), holds, help),
            Group(k, Tab.Feedback, "Diagnostics", k.DocRow(copy, k.Cmd(Loc.Get(Strings.Settings.Feedback.CopyAction), () => copy.Writer.Run!()))),
        ];
    }

    /// <summary>One of the three ways to tell the developer: a disc with a mark, the words, a chevron. A press opens the report dialog (the row's writer).</summary>
    static Element ActionRow(SettingsRow row, string mark, bool accent)
    {
        string? sub = row.DescriptionKey(SettingsFacts.Live) is { } key ? Loc.Get(key) : null;
        var kids = new List<Element>(2) { ZuneText.Label(Loc.Get(row.TitleKey)) };
        if (sub is { Length: > 0 }) kids.Add(ZuneText.Hint(sub));
        Element disc = new BoxEl
        {
            Width = Z.DiscSize, Height = Z.DiscSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Corners = CornerRadius4.All(ZuneControls.Round), Fill = accent ? Tok.AccentDefault : default,
            BorderWidth = accent ? 0f : Z.KeyChipStroke * 2f, BorderColor = Tok.TextPrimary,
            Children = [R.Type.TrackTitle(mark) with { Color = accent ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
        };
        return new BoxEl
        {
            Key = "zs:action:" + row.Id, Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ActionGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = Z.ActionMinH,
            Padding = new Edges4(Z.ActionPadX, Z.ActionPadY, Z.ActionPadX, Z.ActionPadY),
            // The row's hover plate bleeds past the column by its padding, so the disc stands on the column's edge (Z-Feedback).
            Margin = new Edges4(-Z.ActionPadX, 0f, -Z.ActionPadX, 0f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = () => { if (row.Writer.Run!()) Settings.Bump(); },
            Children =
            [
                disc,
                new BoxEl { Direction = 1, Gap = Z.LabelGap, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = kids.ToArray() },
                new BoxEl { Shrink = 0f, Children = [Icon(Icons.ChevronRight, Z.SectionChevron, Tok.TextTertiary)] },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>What a report holds: the four lines the report dialog states (what the reporter writes, the versions, the last log lines if ticked, never secrets), then the dialog's
    /// own sentence on what is sent and when.</summary>
    static Element HoldsCard(ZuneKit k)
    {
        Element Line(string key, bool yes, string text) => new BoxEl
        {
            Key = key, Direction = 0, Gap = Z.HoldsGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Children =
            [
                new BoxEl { Shrink = 0f, Children = [Icon(yes ? Icons.Accept : Icons.Cancel, Z.HoldsMark, yes ? Tok.SystemFillSuccess : Tok.SystemFillCritical)] },
                ZuneText.Label(text) with { Grow = 1f, Basis = 0f },
            ],
        };
        return k.Block("zs:feedback:holds", Loc.Get(Strings.Settings.Feedback.HoldsTitle), null,
        [
            Line("zs:holds:write", true, Loc.Get(Strings.Settings.Feedback.HoldsWrite)),
            Line("zs:holds:versions", true, Loc.Get(Strings.Settings.Feedback.HoldsVersions)),
            Line("zs:holds:logs", true, Strings.Settings.Feedback.HoldsLogs(Feedback.ReportBundle.ManualLogLines)),
            Line("zs:holds:never", false, Loc.Get(Strings.Settings.Feedback.HoldsNever)),
            ZuneText.Hint(Loc.Get(Strings.Report.Subtitle)),
        ]);
    }

    /// <summary>The help links under the contents list (Z-Feedback's "help"): what's new, the issues on GitHub, how Wavee handles your data. They are About's rows, drawn here too.</summary>
    static Element HelpLinks(ZuneKit k)
    {
        var whatsNew = D(Tab.About, "whatsNew");
        return k.Block("zs:feedback:help", Loc.Get(Strings.Settings.Feedback.HelpTitle), null,
        [
            new BoxEl
            {
                Key = "zs:feedback:help:links", Direction = 1, Gap = Z.LabelGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children =
                [
                    k.Cmd(Loc.Get(whatsNew.TitleKey), () => whatsNew.Writer.Run!()),
                    k.Link(Loc.Get(Strings.Report.AboutAllIssues), Settings.IssuesUrl),
                    k.Link(Loc.Get(Strings.Settings.About.PrivacyPolicy), Settings.PrivacyUrl),
                ],
            },
        ]);
    }

    /// <summary>The contents list and the help links as the aside beside the column (a width with room for it).</summary>
    static Element HoldsAside(ZuneKit k) => new BoxEl
    {
        Key = "zs:aside:holds", Direction = 1, Gap = Z.SectionGap, Basis = Z.AsideBasis, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxWidth = Z.HoldsAsideMax,
        Padding = new Edges4(0f, Z.HoldsAsideTop, 0f, 0f), Animate = ZuneMotion.PivotSlide,
        Children = [HoldsCard(k), HelpLinks(k)],
    };
}
