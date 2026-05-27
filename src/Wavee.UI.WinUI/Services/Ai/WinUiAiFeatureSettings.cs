using Wavee.AI;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services.Ai;

public sealed partial class WinUiAiFeatureSettings : IAiFeatureSettings
{
    private readonly ISettingsService _settings;

    public WinUiAiFeatureSettings(ISettingsService settings)
    {
        _settings = settings;
    }

    public bool AiFeaturesEnabled => _settings.Settings.AiFeaturesEnabled;
    public bool AiLyricsSummarizeEnabled => _settings.Settings.AiLyricsSummarizeEnabled;
    public bool AiBioSummarizeEnabled => _settings.Settings.AiBioSummarizeEnabled;
    public bool AiAlbumSummarizeEnabled => _settings.Settings.AiAlbumSummarizeEnabled;
}
