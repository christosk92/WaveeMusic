// ── Themes/Classic/Structure/Rules/StageGeometry.Artist.cs — the numbers of Classic's stage Artist pane (themes plan 6.1, T7a2) ─
//
// What `Shell\Stage.Artist.UI.cs` spelled inline in the pane's sections, moved verbatim with the pictures. The pane's sizes are the
// pane's type scale (`Stage.Layout.QueueScale`) applied to a base DIP; `ClassicStageArtistKit.Px` rounds the product, exactly as the pane
// always did. The decisions (the two-column split, the header height, the reading measure, how many avatars fit) are `Stage.ArtistRules`.

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's stage Artist pane.</summary>
internal static class ClassicStageArtistGeometry
{
    /// <summary>The About body's reading size (× QueueScale): 18/28.</summary>
    public const float BioSize = 18f, BioLine = 28f;

    // ── the header band ──────────────────────────────────────────────────────────────────────────────────────────────

    public const float NameSize = 48f, NameLine = 56f, MetaSize = 18f, MetaLine = 26f, TourEyebrowSize = 12f, TourHeadlineSize = 14f, TourHeadlineLine = 20f;

    /// <summary>The stage veil rising over the photo: its alphas at the top, 35 % down and the foot.</summary>
    public const float VeilMidStop = 0.35f, VeilMidAlpha = 0.12f, VeilFootAlpha = 0.9f;

    // ── the sections ─────────────────────────────────────────────────────────────────────────────────────────────────

    public const float SectionTitleSize = 20f, SectionTitleLine = 28f;

    public const float ReadMoreSize = 14f, ReadMoreLine = 20f;

    public const float CreditSize = 15f, CreditLine = 22f, CreditRowMinH = 36f;

    public const float PopularNumberW = 24f, PopularNumberSize = 14f, PopularNumberLine = 20f, PopularArt = 40f, PopularTitleSize = 15f, PopularTitleLine = 22f,
        PopularPlaysSize = 12f, PopularPlaysLine = 16f, PopularDurationSize = 13f, PopularDurationLine = 18f, PopularRowH = 56f, ExplicitBadge = 14f;

    public const float CityBar = 4f, CityNameSize = 15f, CityNameLine = 22f, CityCountSize = 13f, CityCountLine = 18f;

    public const float FanAvatar = 72f, FanNameSize = 12f, FanNameLine = 16f;
}
