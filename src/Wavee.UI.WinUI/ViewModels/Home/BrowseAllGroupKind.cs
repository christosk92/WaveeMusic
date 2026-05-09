namespace Wavee.UI.WinUI.ViewModels.Home;

internal enum BrowseAllGroupKind
{
    Top,
    ForYou,
    Genres,
    MoodAndActivity,
    Charts,
    More,
}

internal static class BrowseAllGroupKindExtensions
{
    public static string ResourceKey(this BrowseAllGroupKind kind) => kind switch
    {
        BrowseAllGroupKind.Top             => "BrowseAll_Eyebrow_Top",
        BrowseAllGroupKind.ForYou          => "BrowseAll_Eyebrow_ForYou",
        BrowseAllGroupKind.Genres          => "BrowseAll_Eyebrow_Genres",
        BrowseAllGroupKind.MoodAndActivity => "BrowseAll_Eyebrow_MoodAndActivity",
        BrowseAllGroupKind.Charts          => "BrowseAll_Eyebrow_Charts",
        BrowseAllGroupKind.More            => "BrowseAll_Eyebrow_More",
        _                                  => "BrowseAll_Eyebrow_More",
    };
}
