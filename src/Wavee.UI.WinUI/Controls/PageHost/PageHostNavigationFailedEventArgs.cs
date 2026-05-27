using System;

namespace Wavee.UI.WinUI.Controls.PageHost;

public sealed partial class PageHostNavigationFailedEventArgs : EventArgs
{
    public PageHostNavigationFailedEventArgs(Type? pageType, object? parameter, Exception exception)
    {
        PageType = pageType;
        Parameter = parameter;
        Exception = exception;
    }

    public Type? PageType { get; }
    public object? Parameter { get; }
    public Exception Exception { get; }
    public bool Handled { get; set; }
}
