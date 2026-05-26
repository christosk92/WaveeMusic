namespace Wavee.AI;

public interface IAiFeatureSettings
{
    bool AiFeaturesEnabled { get; }
    bool AiLyricsSummarizeEnabled { get; }
    bool AiBioSummarizeEnabled { get; }
    bool AiAlbumSummarizeEnabled { get; }
}
