using Microsoft.UI.Xaml;
using Vanara.PInvoke;
using WinRT.Interop;

namespace Wavee.Controls.Lyrics.Extensions
{
    public static class WindowExtensions
    {
        public static void SetIsBorderless(this Window window, bool enable)
        {
            nint hwnd = WindowNative.GetWindowHandle(window);
            int style = User32.GetWindowLong(hwnd, User32.WindowLongFlags.GWL_STYLE);

            if (enable)
            {
                style &= ~(int)(User32.WindowStyles.WS_CAPTION | User32.WindowStyles.WS_THICKFRAME);
            }
            else
            {
                style |= (int)(User32.WindowStyles.WS_CAPTION | User32.WindowStyles.WS_THICKFRAME);
            }

            User32.SetWindowLong(hwnd, User32.WindowLongFlags.GWL_STYLE, style);
        }
    }
}
