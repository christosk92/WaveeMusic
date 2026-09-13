using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Wavee;

static class StartupNotice
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    const uint MB_OK = 0x0;
    const uint MB_YESNO = 0x4;
    const uint MB_ICONWARNING = 0x30;
    const uint MB_ICONERROR = 0x10;
    const int IDYES = 6;

    public static void Warning(string title, string body)
    {
        WaveeLog.Instance.Warn("app", title + ": " + body);
        Show(title, body, MB_ICONWARNING);
    }

    public static void Error(string title, string body)
    {
        WaveeLog.Instance.Error("app", title + ": " + body);
        Show(title, body, MB_ICONERROR);
    }

    public static bool ErrorYesNo(string title, string body)
    {
        WaveeLog.Instance.Error("app", title + ": " + body);
        return ShowYesNo(title, body, MB_ICONERROR);
    }

    static void Show(string title, string body, uint icon)
    {
        if (OperatingSystem.IsWindows())
            MessageBoxW(IntPtr.Zero, body, title, MB_OK | icon);
        else
            Console.Error.WriteLine(title + ": " + body);
    }

    static bool ShowYesNo(string title, string body, uint icon)
    {
        if (OperatingSystem.IsWindows())
            return MessageBoxW(IntPtr.Zero, body, title, MB_YESNO | icon) == IDYES;
        Console.Error.WriteLine(title + ": " + body);
        return false;
    }
}
