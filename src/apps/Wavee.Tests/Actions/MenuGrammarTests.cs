using System;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

/// <summary>
/// The MENU GRAMMAR rules that are pure decisions rather than composition: whether a "Go to artist" row has anywhere
/// to go (<see cref="ActionRules"/>), the clipper every dynamic menu label rides, and the flattener a menu header
/// uses to turn an HTML subtitle fragment into plain text.
/// </summary>
public class MenuGrammarTests
{
    // ── the pure rule: a "Go to artist" row must have somewhere to go ────────────────────────────────────────────────

    [Fact]
    public void CanGoToArtist_RequiresAPrimaryArtistUri()
    {
        var ok = ActionTarget.ForTracks(new[] { T.Mk("a") });            // seeds spotify:artist:ar0
        Assert.True(ActionRules.CanGoToArtist(in ok));

        var noArtists = ActionTarget.ForTracks(new[] { T.Mk("a", artists: 0) });
        Assert.False(ActionRules.CanGoToArtist(in noArtists));

        // The reported shape: a projected row (Menus.TrackFromEntry) carries an artist NAME with no uri. Navigating it
        // would open the route "artist:" — a dead page. The row must be absent instead.
        var nameOnly = ActionTarget.ForTracks(new[]
        {
            new Track("z", "spotify:track:z", "Track z",
                new[] { new ArtistRef("", "", "Unknown") },
                new AlbumRef("al1", "spotify:album:al1", "Album One"), 180_000, false, null),
        });
        Assert.False(ActionRules.CanGoToArtist(in nameOnly));
    }

    [Fact]
    public void CanGoToArtist_IsSingleTargetOnly()
    {
        var multi = ActionTarget.ForTracks(new[] { T.Mk("a"), T.Mk("b") });
        Assert.False(ActionRules.CanGoToArtist(in multi));   // a selection has no single artist to go to
    }

    // ── the raw-HTML header defect ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Entity subtitles arrive as HTML FRAGMENTS ("Song • &lt;a href=…&gt;Name&lt;/a&gt;") because the row
    /// renderers parse them into clickable links. A menu header is a plain text element, so the reported defect was a
    /// header rendering the raw tag. The flattener the header rides is the shared one, and it does what the header
    /// needs: a link becomes its NAME. (The walk itself is <c>SpotifyExportMapper.ToPlainText</c>, covered by
    /// ExportMapperTextTests; this pins the one shape the menu header defect was reported with.)</summary>
    [Fact]
    public void TheFlattener_TurnsAnArtistLinkIntoItsName()
    {
        const string wire = "Song • <a href=\"spotify:artist:2Q3eZMfDc\">Kali Uchis</a>";
        string? plain = SpotifyExportMapper.ToPlainText(wire);
        Assert.Equal("Song • Kali Uchis", plain);
        Assert.DoesNotContain("<a ", plain, StringComparison.Ordinal);
    }

    // ── dynamic names inside menu labels ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A long dynamic name inside a menu label is CLIPPED where the label is minted. A menu row's label is a
    /// single-line text element with no trimming of its own, so "Move out of “{a 90-character folder name}”" widened
    /// the whole flyout and every row in it.</summary>
    [Fact]
    public void ALongInterpolatedNameIsClippedBeforeItReachesTheLabel()
    {
        // A name that fits is never decorated…
        Assert.Equal("Chill", MenuLabel.Clip("Chill"));
        Assert.Equal("", MenuLabel.Clip(null));
        Assert.Equal(new string('x', MenuLabel.NameChars), MenuLabel.Clip(new string('x', MenuLabel.NameChars)));
        // …and one that does not comes back at the width, ellipsis included, with no dangling space before it.
        string clipped = MenuLabel.Clip(new string('x', MenuLabel.NameChars + 40));
        Assert.Equal(MenuLabel.NameChars, clipped.Length);
        Assert.EndsWith("…", clipped, StringComparison.Ordinal);
        Assert.Equal("Late night…", MenuLabel.Clip("Late night listening", 12));
    }
}
