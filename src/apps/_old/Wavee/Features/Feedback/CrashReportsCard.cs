using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Settings → Diagnostics: the up-to-10 newest crash reports on disk (<see cref="CrashReportFiles"/>),
/// each row a one-click "Report…" straight into <see cref="ReportRequests"/> (crash mode, prefilled with that
/// exact file) and an "Open" that reveals the raw <c>crash-report-*.txt</c> in Explorer. The listing is read
/// once at mount — a report written by THIS session lands here again only after the tab remounts, same as the
/// rest of Diagnostics' snapshot-on-mount rows.</summary>
sealed class CrashReportsCard : Component
{
    public override Element Render()
    {
        var reports = UseMemo(() => CrashReportFiles.List(CrashReport.DefaultDirectory, 10), DepKey.Empty);

        Element[] items = reports.Length == 0
            ? [EmptyRow()]
            : BuildRows(reports);

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Report.CrashReports),
            Description = Loc.Get(Strings.Report.CrashReportsSub),
            HeaderIcon = Icons.StatusWarning,
            InitiallyExpanded = false,
            Items = items,
        });
    }

    static Element EmptyRow() =>
        SettingsExpander.Item("", null,
            new TextEl(Loc.Get(Strings.Report.CrashReportsEmpty)) { Size = 12f, Color = Tok.TextTertiary })
        with
        { Key = "crash-reports:empty" };

    static Element[] BuildRows((string Path, DateTime Stamp)[] reports)
    {
        var rows = new Element[reports.Length];
        for (int i = 0; i < reports.Length; i++)
        {
            var (path, stamp) = reports[i];
            rows[i] = SettingsExpander.Item(stamp.ToString("g"), null,
                HStack(Spacing.S,
                    Button.Standard(Loc.Get(Strings.Report.ReportButton),
                        () => ReportRequests.Open(ReportKind.Crash, new ReportPrefill(CrashReportPath: path))),
                    Button.Standard(Loc.Get(Strings.Report.OpenButton),
                        () => ShellOpen.RevealInExplorer(path))))
            with
            { Key = "crash-reports:" + path };
        }
        return rows;
    }
}
