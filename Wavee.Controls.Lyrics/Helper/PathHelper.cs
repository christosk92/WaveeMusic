using System;
using System.IO;

namespace Wavee.Controls.Lyrics.Helper
{
    public static class PathHelper
    {
        private static readonly string _baseDir = AppContext.BaseDirectory;

        public static string LanguageProfilePath => Path.Combine(_baseDir, "Assets", "Wiki82.profile.xml");
        public static string AlbumArtPlaceholderPath => Path.Combine(_baseDir, "Assets", "AlbumArtPlaceholder.png");
        public static string LogoPath { get; set; } = Path.Combine(_baseDir, "Assets", "Logo.ico");
    }
}
