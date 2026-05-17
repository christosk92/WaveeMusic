using System;
using System.ComponentModel;

namespace Wavee.UI.WinUI.ViewModels;

public sealed class ConcertVm : INotifyPropertyChanged
{
    public string? Title { get; init; }
    public string? Venue { get; init; }
    public string? City { get; init; }
    public string? DateFormatted { get; init; }
    public string? DayOfWeek { get; init; }
    public string? Year { get; init; }
    public bool IsFestival { get; init; }
    public string? Uri { get; init; }

    /// <summary>
    /// Raw date/time of the concert. Preserved alongside the formatted strings
    /// so the artist-page tour banner can categorise "Upcoming show" vs
    /// "Upcoming tour" vs "On tour now" by counting + first-date proximity,
    /// without re-parsing <see cref="DateFormatted"/>.
    /// </summary>
    public DateTimeOffset Date { get; init; }

    private bool _isNearUser;
    public bool IsNearUser
    {
        get => _isNearUser;
        set
        {
            if (_isNearUser == value) return;
            _isNearUser = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNearUser)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
