using System;

namespace Wavee.UI.WinUI.Controls.PageHost;

public sealed partial class PageHostNavigatingEventArgs : EventArgs
{
    public PageHostNavigatingEventArgs(Type pageType, object? parameter, PageHostNavigationMode mode)
    {
        PageType = pageType;
        Parameter = parameter;
        Mode = mode;
    }

    public Type PageType { get; }
    public object? Parameter { get; }
    public PageHostNavigationMode Mode { get; }
    public bool Cancel { get; set; }
}
