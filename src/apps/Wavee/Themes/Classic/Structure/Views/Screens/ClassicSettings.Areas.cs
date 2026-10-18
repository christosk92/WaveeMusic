// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Areas.cs — the area composer and the four sections the document gained (themes plan 6.1) ──
//
// The page shows the twelve AREAS of `Settings.Areas.cs` (account · appearance · playback · audio · lyrics · devices & connect · storage · notifications · keyboard ·
// privacy · feedback & help · about); each is a list of document sections, so Classic and Zune draw the same sections in their own style. A tab's body (`GeneralTab`,
// `PlaybackTab`, ...) is a flat run of cards in which every section starts at a `Mark(section)`; `AreaBody` asks each tab once, keeps the runs the area names, and hands
// them to `TabStack` (which balances them into its one or two columns). The four sections below have no old tab: Account, Devices, Keyboard (a READ-ONLY reference of
// the chords the shell defines) and Feedback & help (report a problem, suggest something, ask a question, what a report holds, copy diagnostics).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Tab = Wavee.Settings.Tab;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    /// <summary>The key a section's marker carries: a zero-size, inert node that starts the section's run inside a tab's body (the composer drops it).</summary>
    const string MarkPrefix = "settings:mark:";

    static Element Mark(string section) => new BoxEl { Key = MarkPrefix + section, Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

    /// <summary>The body of <paramref name="area"/>: every part's run from its tab, in the area's order.</summary>
    static Element AreaBody(Settings.Area area, SettingsPageProps props)
    {
        var parts = Settings.PartsOf(area);
        var built = new Dictionary<Tab, Element[]>(4);
        var cards = new List<Element>(32);
        foreach (var part in parts)
        {
            if (!built.TryGetValue(part.Tab, out var tab)) built[part.Tab] = tab = TabElements(part.Tab, props);
            bool take = false;
            foreach (var element in tab)
            {
                if (element is BoxEl { Key: { } key } && key.StartsWith(MarkPrefix, StringComparison.Ordinal))
                {
                    take = string.Equals(key.AsSpan(MarkPrefix.Length).ToString(), part.Section, StringComparison.Ordinal);
                    continue;
                }
                if (take) cards.Add(element);
            }
        }
        return TabStack(cards.ToArray());
    }

    static Element[] TabElements(Tab tab, SettingsPageProps props) => tab switch
    {
        Tab.General => GeneralTab(),
        Tab.Appearance => AppearanceTab(),
        Tab.Playback => PlaybackTab(),
        Tab.Notifications => NotificationsTab(),
        Tab.Storage => StorageTab(props.Storage),
        Tab.PrivacyDiagnostics => PrivacyDiagnosticsTab(),
        Tab.About => AboutTab(),
        Tab.Account => AccountTab(),
        Tab.Devices => DevicesTab(),
        Tab.Keyboard => KeyboardTab(),
        _ => FeedbackTab(),
    };

    static string[] AreaLabels()
    {
        var labels = new string[Settings.AreaTitleKeys.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = Loc.Get(Settings.AreaTitleKeys[i]);
        return labels;
    }

    // ══ account ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element[] AccountTab()
    {
        string name = D(Tab.Account, "identity").Current as string ?? "";
        int tier = D(Tab.Account, "plan").Current is int t ? t : 0;
        string? plan = tier == 0 ? null : Loc.Get(tier == 2 ? Strings.Auth.PremiumBadge : Strings.Auth.FreeBadge);
        var profile = D(Tab.Account, "profile");
        var manage = D(Tab.Account, "manageAccount");
        var signOut = D(Tab.Account, "signOut");
        var doc = SettingsDoc.Section(Tab.Account, "Account");
        return
        [
            Mark("Account"),
            SectionHeader(Loc.Get(doc.TitleKey), Icons.Contact, doc.SubtitleKey is { } sub ? Loc.Get(sub) : null),
            Row(name.Length > 0 ? name : Loc.Get(Strings.Settings.Account.SignedInAs), plan, null, Icons.Contact),
            DocRow(profile, icon: Icons.Contact, isClickEnabled: true, onClick: () => profile.Writer.Run!()),
            DocRow(manage, icon: Icons.Globe, isClickEnabled: true, onClick: () => manage.Writer.Run!()),
            DocRow(signOut, icon: Icons.SignOut, isClickEnabled: true, onClick: () => signOut.Writer.Run!()),
        ];
    }

    // ══ devices & connect ════════════════════════════════════════════════════════════════════════════════════════════════

    static Element[] DevicesTab()
    {
        var thisPc = D(Tab.Devices, "thisPc");
        var doc = SettingsDoc.Section(Tab.Devices, "Connect");
        return
        [
            Mark("Connect"),
            SectionHeader(Loc.Get(doc.TitleKey), Icons.Devices, doc.SubtitleKey is { } sub ? Loc.Get(sub) : null),
            DocRow(thisPc, new TextEl(thisPc.Current as string ?? "") { Size = ClassicSettingsRules.ValueTagSize, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, icon: Icons.Device),
            ToolLinkCard(Tab.Devices, "connectDiagnostics"),
        ];
    }

    // ══ keyboard ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element[] KeyboardTab()
    {
        var doc = SettingsDoc.Section(Tab.Keyboard, "Shortcuts");
        var cards = new List<Element>(doc.Rows.Count + 2)
        {
            Mark("Shortcuts"),
            SectionHeader(Loc.Get(doc.TitleKey), Icons.Font, doc.SubtitleKey is { } sub ? Loc.Get(sub) : null),
        };
        foreach (var row in doc.Rows)
            if (row.Visible(SettingsFacts.Live)) cards.Add(DocRow(row, KeyChip(row.Current as string ?? "")));
        return cards.ToArray();
    }

    static Element KeyChip(string chord) => new BoxEl
    {
        Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(ClassicSettingsRules.KeyChipPadX, ClassicSettingsRules.KeyChipPadY, ClassicSettingsRules.KeyChipPadX, ClassicSettingsRules.KeyChipPadY),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Corners = CornerRadius4.All(Spacing.XXS), HitTestVisible = false,
        Children = [new TextEl(Settings.Shortcuts.Words(chord)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };

    // ══ feedback & help ══════════════════════════════════════════════════════════════════════════════════════════════════

    static Element[] FeedbackTab()
    {
        var report = D(Tab.Feedback, "reportProblem");
        var suggest = D(Tab.Feedback, "suggestFeature");
        var ask = D(Tab.Feedback, "askQuestion");
        var copy = D(Tab.Feedback, "copyDiagnostics");
        var doc = SettingsDoc.Section(Tab.Feedback, "Help");
        return
        [
            Mark("Help"),
            SectionHeader(Loc.Get(doc.TitleKey), Icons.Attention, doc.SubtitleKey is { } sub ? Loc.Get(sub) : null),
            DocRow(report, icon: Icons.Attention, isClickEnabled: true, onClick: () => report.Writer.Run!()),
            DocRow(suggest, icon: Icons.Add, isClickEnabled: true, onClick: () => suggest.Writer.Run!()),
            DocRow(ask, icon: Icons.Info, isClickEnabled: true, onClick: () => ask.Writer.Run!()),
            // What the report dialog sends: the four lines it states, then its own sentence on what is sent and when.
            SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Settings.Feedback.HoldsTitle),
                Description = Loc.Get(Strings.Report.Subtitle),
                HeaderIcon = Icons.Shield,
                Items =
                [
                    Item(Loc.Get(Strings.Settings.Feedback.HoldsWrite), null, icon: Icons.Accept),
                    Item(Loc.Get(Strings.Settings.Feedback.HoldsVersions), null, icon: Icons.Accept),
                    Item(Strings.Settings.Feedback.HoldsLogs(Feedback.ReportBundle.ManualLogLines), null, icon: Icons.Accept),
                    Item(Loc.Get(Strings.Settings.Feedback.HoldsNever), null, icon: Icons.Cancel),
                ],
            }) with { Key = "feedback.holds" },

            Mark("Diagnostics"),
            SectionHeader(Loc.Get(Strings.Settings.Feedback.DiagnosticsTitle), Icons.Info),
            DocRow(copy, icon: Icons.Copy, isClickEnabled: true, onClick: () => copy.Writer.Run!()),
        ];
    }
}
