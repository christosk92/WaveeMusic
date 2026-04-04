using System;
using Microsoft.UI.Xaml.Data;

namespace Wavee.UI.WinUI.Converters;

public sealed class BoolToFollowTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool isFollowing && isFollowing ? "Following" : "Follow";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public sealed class BoolToFollowGlyphConverter : IValueConverter
{
    // EB51 = AddFriend, E8FB = CheckMark
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool isFollowing && isFollowing ? "\uE8FB" : "\uEB51";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
