using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// User-facing "Report a problem" flow: builds a complete diagnostics bundle (including
/// the AudioHost log), reveals it in File Explorer, then lets the user pick how to send
/// it — GitHub issue or email. Shared by the Settings → About card and the audio-engine
/// failure notification so both produce the same, complete bundle.
/// </summary>
public static class DiagnosticsReporter
{
    /// <param name="xamlRoot">
    /// XamlRoot for the chooser dialog. Falls back to the main window's content root.
    /// </param>
    /// <param name="context">
    /// Optional pre-filled problem description (e.g. the audio-engine failure message).
    /// </param>
    public static async Task ReportAsync(XamlRoot? xamlRoot = null, string? context = null)
    {
        string? zipPath = null;
        try
        {
            zipPath = await CrashReportPackager.CreateZipAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DiagnosticsReporter: bundle creation failed: {ex}");
        }

        // Reveal first so File Explorer is already showing the zip behind the chooser —
        // this also satisfies the "open folder" affordance the user asked for.
        if (zipPath is not null) CrashReportPackager.RevealInExplorer(zipPath);

        xamlRoot ??= MainWindow.Instance?.Content?.XamlRoot;
        if (xamlRoot is null)
        {
            // No UI surface for a dialog — fall back to the direct GitHub path.
            await CrashReportPackager.OpenGitHubIssueAsync(CrashReportPackager.BuildIssueBodyTemplate(context));
            return;
        }

        var hasLogs = zipPath is not null;
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = AppLocalization.GetString("Diagnostics_ReportTitle"),
            Content = AppLocalization.GetString(hasLogs ? "Diagnostics_ReportBody" : "Diagnostics_ReportBody_NoLogs"),
            PrimaryButtonText = AppLocalization.GetString("Diagnostics_ReportGitHub"),
            SecondaryButtonText = AppLocalization.GetString("Diagnostics_ReportEmail"),
            CloseButtonText = AppLocalization.GetString("Diagnostics_ReportDone"),
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another ContentDialog is already open, or no UI thread — fall back.
            Debug.WriteLine($"DiagnosticsReporter: dialog failed: {ex}");
            await CrashReportPackager.OpenGitHubIssueAsync(CrashReportPackager.BuildIssueBodyTemplate(context));
            return;
        }

        switch (result)
        {
            case ContentDialogResult.Primary:
                await CrashReportPackager.OpenGitHubIssueAsync(CrashReportPackager.BuildIssueBodyTemplate(context));
                break;
            case ContentDialogResult.Secondary:
                await CrashReportPackager.OpenEmailDraftAsync(CrashReportPackager.BuildEmailBody(context));
                break;
            // Close ("Done") — the zip is already revealed in Explorer; nothing more to do.
        }
    }
}
