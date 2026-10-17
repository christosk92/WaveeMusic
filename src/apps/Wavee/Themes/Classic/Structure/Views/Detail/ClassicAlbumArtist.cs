// ── Themes/Classic/Structure/Views/Detail/ClassicAlbumArtist.cs — Classic's album attribution: the billed-artist line and the every-artist flyout (T9b) ──
//
// From `Entities\Album.UI.cs` §1 (the face pile). `Album.FacePileHost` keeps the gate (the stamp over the artist, track and edge tables), the demand of the
// portraits, the every-artist set, the width mode's hysteresis, the open / close state of the flyout and what a click does (one artist: straight to it;
// more: the flyout). The line is the shared kit's look (`ClassicDetailKit.ArtistLineBox`, also the rail's default line); the flyout is W16's: every DISTINCT
// artist across the album, 44-DIP rows in a 280 × 360 flyout, built at OPEN.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's album attribution views.</summary>
internal static class ClassicAlbumArtist
{
    /// <summary>The registered template of <see cref="ViewKeys.AlbumArtistLine"/>: the rail's ONE artist-line look in the mode the host decided.</summary>
    public static Element LineTemplate(AlbumArtistLineProps p, in ViewContext ctx)
        => p.Line is { } line ? ClassicDetailKit.ArtistLineBox(p.Mode, line) : new BoxEl();

    /// <summary>The registered template of <see cref="ViewKeys.AlbumArtistFlyout"/>.</summary>
    public static Element FlyoutTemplate(PersonFlyoutProps p, in ViewContext ctx)
    {
        var rows = new Element[p.Rows.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = FlyoutRow(p.Rows[i]);
        var list = new BoxEl { Direction = 1, Gap = ClassicAlbumLayout.FlyoutListGap, Width = R.Space.FacePileListW, Children = rows };
        return new BoxEl
        {
            Direction = 1, Width = R.Space.FacePileFlyoutW, MaxHeight = R.Space.FacePileFlyoutH,
            Padding = Edges4.All(Spacing.S),
            Children =
            [
                Ui.ScrollView(list) with
                {
                    Width = R.Space.FacePileListW, MaxHeight = R.Space.FacePileListH,
                    ContentSized = true, AutoEdgeFade = true, Grow = 0f,
                },
            ],
        };
    }

    static Element FlyoutRow(PersonFlyoutRow row)
    {
        bool live = row.Go is not null;
        return new BoxEl
        {
            Direction = 0, Height = Design.Size.FacePileRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = CornerRadius4.All(ClassicAlbumLayout.FlyoutRowRadius),
            Role = AutomationRole.MenuItem, Focusable = true,
            Cursor = live ? CursorId.Hand : (CursorId?)null,
            OnClick = row.Go,
            Children =
            [
                PersonPicture.Create("", ClassicAlbumLayout.FlyoutPortrait, displayName: row.Name, imageSourcePath: row.ImageUrl),
                new TextEl(row.Name)
                {
                    Size = ClassicAlbumLayout.FlyoutNameSize, LineHeight = ClassicAlbumLayout.FlyoutNameLine, Weight = 600, Color = Tok.TextPrimary,
                    Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        }.Interactive(Interaction.Subtle);
    }
}
