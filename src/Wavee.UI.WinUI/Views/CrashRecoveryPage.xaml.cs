using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Wavee.UI.WinUI.Views;

public sealed partial class CrashRecoveryPage : UserControl, IPageHostAware
{
    private CrashRecoveryReport? _report;

    public CrashRecoveryPage()
    {
        InitializeComponent();
    }

    public bool ShouldCacheInHost => false;

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        _report = parameter as CrashRecoveryReport ?? CrashRecoveryReportStore.TryReadPending();
        ApplyReport();
    }

    public void OnLeaving()
    {
    }

    private void ApplyReport()
    {
        var report = _report;
        if (report is null)
        {
            StopCodeText.Text = AppLocalization.GetString("CrashRecovery_UnknownStopCode");
            SourceText.Text = AppLocalization.GetString("CrashRecovery_UnknownSource");
            RecordedText.Text = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
            StackTraceText.Text = AppLocalization.GetString("CrashRecovery_MissingStackTrace");
            return;
        }

        StopCodeText.Text = report.ExceptionType;
        SourceText.Text = report.Source;
        RecordedText.Text = report.TimestampDisplay;
        StackTraceText.Text = report.FullText;
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        ContinueButton.IsEnabled = false;
        try
        {
            await MainWindow.Instance.ContinueAfterCrashRecoveryAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Crash recovery continue failed: {ex}");
            ContinueButton.IsEnabled = true;
        }
    }

    private async void ReportIssue_Click(object sender, RoutedEventArgs e)
    {
        ReportIssueButton.IsEnabled = false;
        try
        {
            await CrashReportPackager.OpenIssueReportAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Crash report launch failed: {ex}");
        }
        finally
        {
            ReportIssueButton.IsEnabled = true;
        }
    }

    private void CopyStackTrace_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(_report?.FullText ?? StackTraceText.Text ?? string.Empty);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy crash stacktrace failed: {ex}");
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            var arguments = File.Exists(AppPaths.CrashLogPath)
                ? $"/select,\"{AppPaths.CrashLogPath}\""
                : $"\"{AppPaths.AppDataDirectory}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open crash logs failed: {ex}");
        }
    }
}
