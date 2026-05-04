using Microsoft.Windows.ApplicationModel.Resources;

namespace Wavee.Controls.Lyrics.Services.LocalizationService
{
    public class LocalizationService : ILocalizationService
    {
        private readonly ResourceLoader? _resourceLoader;

        public LocalizationService()
        {
            try
            {
                _resourceLoader = new ResourceLoader();
            }
            catch
            {
                // ResourceMap not found — no .resw files available
            }
        }

        public string GetLocalizedString(string id)
        {
            if (_resourceLoader is null) return id;
            try
            {
                return _resourceLoader.GetString(id);
            }
            catch
            {
                return id;
            }
        }
    }
}
