using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.UI.WinUI.ViewModels;

public sealed class EpisodeChapterVm : ObservableObject
{
    private bool _isActive;
    private bool _isCompleted;
    private double _timelineCellProgress;

    public int Number { get; init; }
    public bool IsFirst { get; set; }
    public bool IsLast { get; set; }
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public long StartMilliseconds { get; init; }
    public long StopMilliseconds { get; init; }

    public string StartTime => FormatTime(StartMilliseconds);
    public string ChapterLabel => Number > 0 ? $"Chapter {Number}" : "Chapter";
    public bool IsActive => _isActive;
    public bool IsCompleted => _isCompleted;
    public double TimelineCellProgress => _timelineCellProgress;
    public double ContentOpacity => IsActive ? 1 : IsCompleted ? 0.86 : 0.66;
    public double ChromeOpacity => IsActive ? 1 : IsCompleted ? 0.82 : 0.58;

    public string TimeRange
    {
        get
        {
            if (StopMilliseconds > StartMilliseconds)
                return $"{FormatTime(StartMilliseconds)} - {FormatTime(StopMilliseconds)}";

            return FormatTime(StartMilliseconds);
        }
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public void SetTimelineState(bool isActive, bool isCompleted, double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        var changed = SetProperty(ref _isActive, isActive, nameof(IsActive));
        changed |= SetProperty(ref _isCompleted, isCompleted, nameof(IsCompleted));
        changed |= SetProperty(ref _timelineCellProgress, clamped, nameof(TimelineCellProgress));

        if (!changed)
            return;

        OnPropertyChanged(nameof(ContentOpacity));
        OnPropertyChanged(nameof(ChromeOpacity));
    }
}
