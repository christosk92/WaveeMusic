using Wavee.Controls.Lyrics.Helper;
using Microsoft.UI.Windowing;

namespace Wavee.Controls.Lyrics.Extensions
{
    public static class AppWindowExtensions
    {
        extension(AppWindow appWindow)
        {
            public void SetIcons()
            {
                appWindow.SetIcon(PathHelper.LogoPath);
                appWindow.SetTaskbarIcon(PathHelper.LogoPath);
                appWindow.SetTitleBarIcon(PathHelper.LogoPath);
            }
        }
    }
}
