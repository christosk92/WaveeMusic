namespace Wavee.UI.WinUI.Services;

/// <summary>
/// XAML-friendly static getters for localized strings consumed via
/// <c>{x:Bind services:LocalizedStrings.Foo}</c>. Use this when an x:Uid
/// can't be applied to the target element (custom DP whose property name
/// doesn't follow the resource-loader's conventions, or a binding sink
/// inside a DataTemplate where x:Uid wouldn't be reached during item
/// realisation). For built-in Text/Content/Header/etc. on a control, prefer
/// x:Uid + a resw entry — that path costs no per-bind allocation.
/// </summary>
public static class LocalizedStrings
{
    public static string ArtistSubtitle => AppLocalization.GetString("ContentType_Artist");
    public static string AlbumSubtitle => AppLocalization.GetString("ContentType_Album");
    public static string PlaylistSubtitle => AppLocalization.GetString("ContentType_Playlist");
    public static string PodcastSubtitle => AppLocalization.GetString("ContentType_Podcast");

    public static string ArtistStat_MonthlyListeners => AppLocalization.GetString("ArtistStat_MonthlyListeners");
    public static string ArtistStat_Followers => AppLocalization.GetString("ArtistStat_Followers");
    public static string ArtistStat_Albums => AppLocalization.GetString("ArtistStat_Albums");
    public static string ArtistStat_SinglesAndEPs => AppLocalization.GetString("ArtistStat_SinglesAndEPs");
    public static string ArtistStat_UpcomingConcerts => AppLocalization.GetString("ArtistStat_UpcomingConcerts");
    public static string ArtistStat_RelatedArtists => AppLocalization.GetString("ArtistStat_RelatedArtists");

    public static string SidebarPinnedEyebrow => AppLocalization.GetString("Shell_SidebarPinned");
}
