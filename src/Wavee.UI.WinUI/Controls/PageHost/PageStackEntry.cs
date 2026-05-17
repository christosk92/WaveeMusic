using System;

namespace Wavee.UI.WinUI.Controls.PageHost;

public sealed class PageStackEntry
{
    public PageStackEntry(Type pageType, object? parameter)
    {
        PageType = pageType;
        Parameter = parameter;
    }
    public Type PageType { get; set; }
    public object? Parameter { get; set; }
}
