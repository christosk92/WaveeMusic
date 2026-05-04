using Windows.Globalization;

namespace Wavee.Controls.Lyrics.Models
{
    public class ExtendedLanguage
    {
        public string LanguageCode { get; set; }
        public string DisplayName { get; set; }

        public ExtendedLanguage(string languageCode)
        {
            LanguageCode = languageCode;
            try
            {
                DisplayName = new Language(languageCode).DisplayName;
            }
            catch
            {
                DisplayName = languageCode;
            }
        }

        public ExtendedLanguage(string languageCode, string displayName)
        {
            LanguageCode = languageCode;
            DisplayName = displayName;
        }
    }
}
