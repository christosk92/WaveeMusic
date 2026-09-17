// ── Wavee.Tests/HomeFixtures.cs — Home card handles built through the real commit (Wave 5, owner P) ──────────────────
//
// 0.2.9's Home tests built `HomeCard` RECORDS (`new HomeCard(uri, title, subtitle, image, kind, Meta: …)`). In 0.3 a card
// is a HANDLE over the tables — an entity ref plus the section it was read from — so a ported fact needs rows to point
// at. This helper stages exactly what `Spotify.Decode.HomeFeed` stages for a band: the entity rows, one card fact per card
// (the raw format token, the subtitle, the payload accent, the flags, the seeds), the section ledger and the
// `SectionCards` run — and commits it through `Entities.Commit`, so every card a test holds reads the same columns a
// decoded feed's card does. No shortcut writes a column.
//
// Every section needs a scope: call `TestScope.Fresh()` first. Uris must be unique per fact where identity matters (the
// interner is process-wide, the tables are per scope).

using System.Text;
using Wavee;

namespace Wavee.Tests;

static class HomeFixtures
{
    /// <summary>One card: its uri (the kind is the uri's), what the section says about it, and the entity row's text.</summary>
    public readonly record struct Spec(
        string Uri,
        string Title = "",
        string? Format = null,
        string? Subtitle = null,
        uint Accent = 0,
        string? Image = null,
        bool Audiobook = false,
        bool Video = false,
        int DurationMs = 0,
        int ResumeMs = 0,
        double Rating = 0,
        string? Author = null,
        string? Signifier = null,
        string[]? Seeds = null);

    public static Spec Playlist(string uri, string title = "", string? format = null, string? subtitle = null, uint accent = 0,
        string? image = null, string[]? seeds = null)
        => new(uri, title, format, subtitle, accent, image, Seeds: seeds);

    public static Spec Album(string uri, string title = "", string? subtitle = null, uint accent = 0, string? image = null)
        => new(uri, title, Subtitle: subtitle, Accent: accent, Image: image);

    static int s_sequence;

    /// <summary>A section uri no other fact in this process minted.</summary>
    public static string NextSectionUri(string tag = "fixture") => "spotify:section:home-" + tag + "-" + Interlocked.Increment(ref s_sequence);

    /// <summary>Stage + commit one band with its cards; the section handle.</summary>
    public static Section Band(string sectionUri, string? title, SectionKind kind, params Spec[] cards)
        => Band(sectionUri, title, kind, total: cards.Length, nextOffset: SectionPaging.NoCursor, flags: SectionFlags.None, cards);

    public static Section Band(string sectionUri, string? title, SectionKind kind, int total, int nextOffset, SectionFlags flags,
        params Spec[] cards)
    {
        var s = Staging.Rent();
        var id = new StagedId(s.Text(sectionUri));
        int mark = s.Edges.PendingMark;
        int factStart = s.CardFacts.Count;

        for (int i = 0; i < cards.Length; i++)
        {
            var spec = cards[i];
            var cid = new StagedId(s.Text(spec.Uri));
            StageEntity(s, in cid, spec);

            ref var f = ref s.CardFacts.Add();
            f.Target = cid;
            f.Format = spec.Format is null ? default : s.Text(spec.Format);
            f.Subtitle = spec.Subtitle is null ? default : s.Text(spec.Subtitle);
            f.Author = spec.Author is null ? default : s.Text(spec.Author);
            f.Signifier = spec.Signifier is null ? default : s.Text(spec.Signifier);
            f.Accent = spec.Accent;
            f.DurationMs = spec.DurationMs;
            f.ResumeMs = spec.ResumeMs;
            f.Rating = (ushort)Math.Round(spec.Rating * 100d);
            f.Flags = (byte)((spec.Audiobook ? HomeCardFlags.Audiobook : 0) | (spec.Video ? HomeCardFlags.HasVideo : 0));
            f.SeedStart = -1;
            if (spec.Seeds is { Length: > 0 } seeds)
            {
                f.SeedStart = s.CardSeeds.Count;
                f.SeedCount = seeds.Length;
                foreach (var seed in seeds) s.CardSeeds.Add() = s.Text(seed);
            }
            s.Edges.Push().Target = cid;
        }

        ref var row = ref s.Sections.RowFor(id, Authority.Full, (uint)SectionFields.Identity);
        row.Title = title is null ? default : s.Text(title);
        row.Kind = (byte)kind;
        row.Flags = (byte)flags;
        row.Total = total;
        row.Raw = cards.Length;
        row.Cards = cards.Length;
        row.NextOffset = nextOffset;
        row.HasFacts = true;
        row.FactStart = factStart;
        row.FactCount = s.CardFacts.Count - factStart;

        s.Edges.Close(Relation.SectionCards, in id, mark);
        TestScope.CommitAndPublish(s);
        return Entities.Section(sectionUri.AsSpan());
    }

    /// <summary>The cards of a freshly committed band, in order (duplicates within the band folded, as the view does).</summary>
    public static IReadOnlyList<HomeCard> Cards(string? title, SectionKind kind, params Spec[] cards)
        => HomeSectionView.Of(Band(NextSectionUri(), title, kind, cards)).Cards;

    /// <summary>One card in its own one-card band.</summary>
    public static HomeCard Card(Spec spec) => Cards(null, SectionKind.HomeGeneric, spec)[0];

    /// <summary>The same view <see cref="HomeSectionView.Of"/> answers for a committed band.</summary>
    public static HomeSectionView View(Section s) => HomeSectionView.Of(s);

    static void StageEntity(Staging s, in StagedId id, Spec spec)
    {
        var title = s.Text(spec.Title);
        var image = spec.Image is null ? default : s.Text(spec.Image);
        switch (id.Kind(s))
        {
            case EntityKind.Playlist:
            case EntityKind.Collection:
                {
                    ref var p = ref s.Playlists.RowFor(id, Authority.Full, (uint)PlaylistFields.Identity);
                    p.Title = title;
                    p.Image = image;
                    break;
                }
            case EntityKind.Album:
                {
                    ref var a = ref s.Albums.RowFor(id, Authority.Full,
                        (uint)(AlbumFields.Title | AlbumFields.Image | AlbumFields.Year | AlbumFields.Kind));
                    a.Title = title;
                    a.Image = image;
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

    /// <summary>UTF-8 of a fixture file under <c>Fixtures/&lt;folder&gt;/</c>.</summary>
    public static byte[] Fixture(string folder, string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name));

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
