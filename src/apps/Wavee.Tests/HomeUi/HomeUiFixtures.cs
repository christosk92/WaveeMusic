// ── Wavee.Tests/HomeUi/HomeUiFixtures.cs — card/section fixtures for the Home/ZonePlanner suite (Wave 1, owner A1) ──
//
// Same shape as Wavee.Tests/HomeFixtures.cs (stage through the real commit, so a card a test holds reads the same
// columns a decoded feed's card does) but adds the facts ZonePlanner's rules need that HomeFixtures does not carry:
// a header image on a playlist (the wide-lead / DW-lead rule), an album release date (ReleaseDetect) and the
// Explicit/Unplayable card flags. Kept in this folder rather than added to HomeFixtures.cs itself, per the wave's
// disjoint-files rule (HomeFixtures.cs belongs to another owner/wave).

using System.Text;
using Wavee;

namespace Wavee.Tests.HomeUi;

static class HomeUiFixtures
{
    /// <summary>One card: its uri (the kind is the uri's), what the section says about it, and the entity row's text.</summary>
    public readonly record struct CardSpec(
        string Uri, string Title = "", string? Format = null, string? Image = null, string? HeaderImage = null,
        bool Unplayable = false, bool Explicit = false, long ReleasedAtMs = 0);

    public static CardSpec Playlist(string uri, string title = "", string? format = null, string? headerImage = null)
        => new(uri, title, format, HeaderImage: headerImage);

    public static CardSpec Album(string uri, string title = "", long releasedAtMs = 0)
        => new(uri, title, ReleasedAtMs: releasedAtMs);

    public static CardSpec Artist(string uri, string title = "") => new(uri, title);

    /// <summary>A D2 preview row: a thin track (<c>Authority.Seed</c> in the real fold, <c>Authority.Full</c> here —
    /// the fixture only needs the title/image columns present, not the authority ladder).</summary>
    public static CardSpec Track(string uri, string title = "") => new(uri, title);

    static int s_sequence;

    /// <summary>A section uri no other fact in this process minted.</summary>
    public static string NextSectionUri(string tag = "zoneplanner") => "spotify:section:home-" + tag + "-" + Interlocked.Increment(ref s_sequence);

    /// <summary>Stage + commit one band with its cards; the section handle.</summary>
    public static Section Band(string? title, SectionKind kind, params CardSpec[] cards)
        => Band(NextSectionUri(), title, kind, cards);

    public static Section Band(string sectionUri, string? title, SectionKind kind, params CardSpec[] cards)
    {
        var s = Staging.Rent();
        var id = new StagedId(s.Text(sectionUri));
        int mark = s.Edges.PendingMark;
        int factStart = s.CardFacts.Count;

        foreach (var spec in cards)
        {
            var cid = new StagedId(s.Text(spec.Uri));
            StageEntity(s, in cid, spec);

            ref var f = ref s.CardFacts.Add();
            f.Target = cid;
            f.Format = spec.Format is null ? default : s.Text(spec.Format);
            f.Flags = (byte)((spec.Explicit ? HomeCardFlags.Explicit : 0) | (spec.Unplayable ? HomeCardFlags.Unplayable : 0));
            f.SeedStart = -1;
            s.Edges.Push().Target = cid;
        }

        ref var row = ref s.Sections.RowFor(id, Authority.Full, (uint)SectionFields.Identity);
        row.Title = title is null ? default : s.Text(title);
        row.Kind = (byte)kind;
        row.Total = cards.Length;
        row.Raw = cards.Length;
        row.Cards = cards.Length;
        row.NextOffset = SectionPaging.NoCursor;
        row.HasFacts = true;
        row.FactStart = factStart;
        row.FactCount = s.CardFacts.Count - factStart;

        s.Edges.Close(Relation.SectionCards, in id, mark);
        TestScope.CommitAndPublish(s);
        return Entities.Section(sectionUri.AsSpan());
    }

    /// <summary>The same, plus D2's preview tracks (<c>Edges.SectionPreviewTracks</c>) — what <c>ClusterFold</c> now
    /// reads instead of the band's own cards.</summary>
    public static Section BandWithPreview(string? title, SectionKind kind, CardSpec[] cards, CardSpec[] previews)
        => BandWithPreview(NextSectionUri(), title, kind, cards, previews);

    public static Section BandWithPreview(string sectionUri, string? title, SectionKind kind, CardSpec[] cards, CardSpec[] previews)
    {
        var s = Staging.Rent();
        var id = new StagedId(s.Text(sectionUri));
        int mark = s.Edges.PendingMark;
        int factStart = s.CardFacts.Count;

        foreach (var spec in cards)
        {
            var cid = new StagedId(s.Text(spec.Uri));
            StageEntity(s, in cid, spec);

            ref var f = ref s.CardFacts.Add();
            f.Target = cid;
            f.Format = spec.Format is null ? default : s.Text(spec.Format);
            f.Flags = (byte)((spec.Explicit ? HomeCardFlags.Explicit : 0) | (spec.Unplayable ? HomeCardFlags.Unplayable : 0));
            f.SeedStart = -1;
            s.Edges.Push().Target = cid;
        }

        ref var row = ref s.Sections.RowFor(id, Authority.Full, (uint)SectionFields.Identity);
        row.Title = title is null ? default : s.Text(title);
        row.Kind = (byte)kind;
        row.Total = cards.Length;
        row.Raw = cards.Length;
        row.Cards = cards.Length;
        row.NextOffset = SectionPaging.NoCursor;
        row.HasFacts = true;
        row.FactStart = factStart;
        row.FactCount = s.CardFacts.Count - factStart;

        s.Edges.Close(Relation.SectionCards, in id, mark);

        int previewMark = s.Edges.PendingMark;
        foreach (var spec in previews)
        {
            var tid = new StagedId(s.Text(spec.Uri));
            StageEntity(s, in tid, spec);
            s.Edges.Push().Target = tid;
        }
        s.Edges.Close(Relation.SectionPreviewTracks, in id, previewMark);

        TestScope.CommitAndPublish(s);
        return Entities.Section(sectionUri.AsSpan());
    }

    static void StageEntity(Staging s, in StagedId id, CardSpec spec)
    {
        var title = s.Text(spec.Title);
        var image = spec.Image is null ? default : s.Text(spec.Image);
        switch (id.Kind(s))
        {
            case EntityKind.Playlist:
            case EntityKind.Collection:
                {
                    ref var p = ref s.Playlists.RowFor(id, Authority.Full, (uint)(PlaylistFields.Identity | PlaylistFields.Format));
                    p.Title = title;
                    p.Image = image;
                    p.HeaderImage = spec.HeaderImage is null ? default : s.Text(spec.HeaderImage);
                    break;
                }
            case EntityKind.Album:
                {
                    ref var a = ref s.Albums.RowFor(id, Authority.Full, (uint)AlbumFields.Card);
                    a.Title = title;
                    a.Image = image;
                    if (spec.ReleasedAtMs > 0) a.ReleaseAt = (int)(spec.ReleasedAtMs / 1000);
                    break;
                }
            case EntityKind.Artist:
                {
                    ref var a = ref s.Artists.RowFor(id, Authority.Full, (uint)ArtistFields.Identity);
                    a.Name = title;
                    a.Image = image;
                    break;
                }
            case EntityKind.Show:
                {
                    ref var sh = ref s.Shows.RowFor(id, Authority.Full, (uint)ShowFields.Identity);
                    sh.Title = title;
                    sh.Image = image;
                    break;
                }
            case EntityKind.Episode:
                {
                    ref var e = ref s.Episodes.RowFor(id, Authority.Full, (uint)EpisodeFields.Identity);
                    e.Title = title;
                    e.Image = image;
                    break;
                }
            case EntityKind.Track:
                {
                    ref var t = ref s.Tracks.RowFor(id, Authority.Full, (uint)TrackFields.Identity);
                    t.Title = title;
                    t.Image = image;
                    break;
                }
        }
    }
}
