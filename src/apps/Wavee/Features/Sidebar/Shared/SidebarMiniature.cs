using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Neutral quarter-scale sidebar shapes shared by the design chooser and template confirmations. These are a
/// diagram of the real document, not a second sidebar renderer and never a data-loading skeleton.</summary>
static class SidebarMiniature
{
    public static Element Bar(float width, float height, ColorF fill) => new BoxEl
    {
        Width = width, Height = height, Shrink = 0f,
        Corners = CornerRadius4.All(height / 2f), Fill = fill,
    };

    public static Element Pill(float width, float height, ColorF fill) => Bar(width, height, fill);

    public static Element Hairline(ColorF fill) => new BoxEl
    {
        Height = 1f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Fill = fill,
        Margin = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
    };

    public static Element IconRow(float barWidth, ColorF block, ColorF faint) => new BoxEl
    {
        Direction = 0, Height = Spacing.S, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = Spacing.S, Height = Spacing.S, Shrink = 0f,
                Corners = Radii.ControlAll, Fill = block,
            },
            Bar(barWidth, Spacing.XXS, faint),
        ],
    };

    public static Element GridCell(ColorF block, ColorF faint) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, Height = 18f, MinWidth = 0f, Gap = Spacing.XXS,
        Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f),
        Corners = Radii.ControlAll, Fill = faint,
        Children =
        [
            new BoxEl
            {
                Width = 14f, Height = 14f, Shrink = 0f,
                Corners = Radii.ControlAll, Fill = block,
            },
            Bar(22f, Spacing.XXS, block),
        ],
    };

    /// <summary>A compact but realistic sidebar preview derived from the target template. It uses the bundled fake
    /// catalog's real cover art and the same row/art/count geometry as the live pane; only the sample data is synthetic.</summary>
    public static Element Template(SidebarCustomLayout layout)
    {
        var rows = new List<Element>(14);
        Append(layout.Sections, rows);
        if (rows.Count == 0)
            rows.Add(new BoxEl
            {
                Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Gap = Spacing.S,
                Children =
                [
                    Icon(Icons.Add, 20f, Tok.TextTertiary),
                    new TextEl(Loc.Get(SidebarTemplates.DescriptionLocKey(layout.TemplateId)))
                    {
                        Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2,
                    },
                ],
            });

        var pane = new BoxEl
        {
            Direction = 1, Width = 258f, Shrink = 0f, Gap = Spacing.XXS,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.M), ClipToBounds = true,
            Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = [.. rows],
        };
        return new BoxEl
        {
            Direction = 0, Height = 220f, Shrink = 0f, ClipToBounds = true,
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = [pane, Workspace()],
        };
    }

    static void Append(IReadOnlyList<SidebarSectionSpec> sections, List<Element> rows)
    {
        for (int i = 0; i < sections.Count && rows.Count < 14; i++)
        {
            var section = sections[i];
            if (section.Hidden) continue;
            AppendSection(section, i, rows);
            if (section.Kind == SidebarSectionKind.CustomGroup) Append(section.ChildList, rows);
        }
    }

    static void AppendSection(SidebarSectionSpec section, int index, List<Element> rows)
    {
        if (section.Kind == SidebarSectionKind.Divider) { rows.Add(Hairline(Tok.StrokeDividerDefault)); return; }
        string title = section.Title is { Length: > 0 } authored ? authored
            : section.TitleLocKey is { Length: > 0 } key ? Loc.Get(key)
            : SidebarSectionKinds.PaletteNameLocKey(section.Kind) is { Length: > 0 } palette ? Loc.Get(palette)
            : "";
        if (section.Kind == SidebarSectionKind.Header)
        {
            rows.Add(SectionTitle(title));
            return;
        }
        if (title.Length > 0) rows.Add(SectionTitle(title));

        if (section.Opts.Presentation == SidebarPresentation.Grid)
        {
            rows.Add(new BoxEl
            {
                Direction = 0, Shrink = 0f, Gap = Spacing.XS,
                Children = [PreviewGridCell(2), PreviewGridCell(7)],
            });
            return;
        }
        if (section.Kind == SidebarSectionKind.Pinned)
        {
            var a = FakeData.Playlist(1);
            var artist = FakeData.Artist(3);
            rows.Add(PreviewRow(a.Name, a.Cover, Icons.MusicNote, section, "pin:playlist", a.TrackCount));
            rows.Add(PreviewRow(artist.Name, artist.Image, Icons.Contact, section, "pin:artist", null, circular: true));
            return;
        }
        if (section.Kind is SidebarSectionKind.CollectionShortcuts or SidebarSectionKind.StaticLinks)
        {
            var items = section.ItemList;
            for (int i = 0; i < items.Count && i < 3; i++)
            {
                var item = items[i];
                var dest = ShellNav.Dest(item.Key);
                int? count = section.Opts.CountBadges ? ShortcutCount(item.Key) : null;
                rows.Add(PreviewRow(dest.Title, null, SidebarIcons.For(item, dest.Glyph), section,
                    "route:" + item.Key, count));
            }
            return;
        }
        if (section.Kind == SidebarSectionKind.PlaylistTree)
        {
            rows.Add(PreviewFolder(section));
            var playlist = FakeData.Playlist(5);
            rows.Add(PreviewRow(playlist.Name, playlist.Cover, Icons.MusicNote, section,
                "tree:playlist", playlist.TrackCount, depth: 1));
            return;
        }

        var sample = FakeData.Playlist(index + 6);
        rows.Add(PreviewRow(sample.Name, sample.Cover, CzGlyphs.ForKind(section.Kind), section,
            "section:" + section.Kind, sample.TrackCount));
    }

    static Element SectionTitle(string title) => WaveeType.Eyebrow(title) with
    {
        Color = Tok.TextTertiary, MaxLines = 1,
        Trim = TextTrim.CharacterEllipsis,
        Margin = new Edges4(Spacing.XS, Spacing.XXS, Spacing.XS, Spacing.XXS),
    };

    static Element PreviewRow(string title, Image? cover, string glyph, SidebarSectionSpec section,
        string seed, int? count, bool circular = false, int depth = 0)
    {
        float height = SidebarRowGeometry.HeightFor(section.Opts.Density, section.Opts.Subtitles);
        float art = section.Opts.Density switch
        {
            SidebarDensity.Compact => SidebarCover.S20,
            SidebarDensity.Comfortable => SidebarCover.S28,
            _ => SidebarCover.S28,
        };
        Element leading = section.Opts.Artwork
            ? SidebarCover.Art(cover, null, seed, art, circular)
            : SidebarCover.Glyph(glyph, art, circular);
        var children = new List<Element>(4);
        if (depth > 0) children.Add(PreviewTreeGuide(height));
        children.Add(leading);
        children.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
            Children =
            [
                new TextEl(title)
                {
                    Size = 12f, Color = Tok.TextPrimary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
                section.Opts.Subtitles
                    ? new TextEl(Loc.Get("sidebar.v3.filter.playlists"))
                    {
                        Size = 10f, Color = Tok.TextTertiary, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    }
                    : new BoxEl { Height = 0f },
            ],
        });
        if (section.Opts.CountBadges && count is { } n)
            children.Add(new TextEl(n.ToString()) { Size = 10f, Color = Tok.TextTertiary, Shrink = 0f });
        return new BoxEl
        {
            Direction = 0, Height = height, Shrink = 0f, Gap = Spacing.S,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.ControlAll,
            Fill = seed.EndsWith("playlist", System.StringComparison.Ordinal) ? Tok.FillSubtleSecondary : ColorF.Transparent,
            Children = [.. children],
        };
    }

    static Element PreviewTreeGuide(float height) => new BoxEl
    {
        Width = Spacing.M, Height = height, Shrink = 0f, ZStack = true, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Width = 1f, Height = height / 2f, Shrink = 0f,
                Margin = new Edges4(Spacing.XS, 0f, 0f, 0f), Fill = Tok.StrokeDividerDefault,
            },
            new BoxEl
            {
                Width = Spacing.S, Height = 1f, Shrink = 0f,
                Margin = new Edges4(Spacing.XS, height / 2f, 0f, 0f), Fill = Tok.StrokeDividerDefault,
            },
        ],
    };

    static Element PreviewFolder(SidebarSectionSpec section) => new BoxEl
    {
        Direction = 0,
        Height = SidebarRowGeometry.HeightFor(section.Opts.Density, section.Opts.Subtitles),
        Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Children =
        [
            Icon(Icons.ChevronDown, 10f, Tok.TextTertiary),
            SidebarCover.Folder(SidebarCover.S28, expanded: true),
            new TextEl(Loc.Get("sidebar.playlists"))
            {
                Size = 12f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f,
                MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            section.Opts.CountBadges
                ? new TextEl("2") { Size = 10f, Color = Tok.TextTertiary, Shrink = 0f }
                : new BoxEl { Width = 0f },
        ],
    };

    static int? ShortcutCount(string route) => route switch
    {
        "liked" => FakeData.LibraryStats().LikedSongs,
        "albums" => FakeData.LibraryStats().Albums,
        "artists" => FakeData.LibraryStats().Artists,
        "podcasts" => FakeData.LibraryStats().Podcasts,
        _ => null,
    };

    static Element PreviewGridCell(int seed)
    {
        var playlist = FakeData.Playlist(seed);
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = 48f,
            Gap = Spacing.XS, AlignItems = FlexAlign.Center, Padding = Edges4.All(Spacing.XS),
            Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
            Children =
            [
                SidebarCover.Art(playlist.Cover, null, "grid:" + seed, SidebarCover.S40),
                new TextEl(playlist.Name)
                {
                    Size = 11f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f,
                    MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    static Element Workspace()
    {
        var hero = FakeData.Playlist(8);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
            Padding = Edges4.All(Spacing.L), Gap = Spacing.L, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        SidebarCover.Art(hero.Cover, null, "workspace:hero", SidebarCover.S64),
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S,
                            Children =
                            [
                                Bar(92f, Spacing.S, Tok.FillSubtleTertiary),
                                Bar(156f, Spacing.XS, Tok.StrokeDividerDefault),
                                Bar(118f, Spacing.XS, Tok.StrokeDividerDefault),
                            ],
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Spacing.M,
                    Children = [PreviewWorkspaceCard(10), PreviewWorkspaceCard(12), PreviewWorkspaceCard(14)],
                },
            ],
        };
    }

    static Element PreviewWorkspaceCard(int seed)
    {
        var playlist = FakeData.Playlist(seed);
        return new BoxEl
        {
            Direction = 1, Width = SidebarCover.S64, Shrink = 0f, Gap = Spacing.XS,
            Children =
            [
                SidebarCover.Art(playlist.Cover, null, "workspace:" + seed, SidebarCover.S64),
                new TextEl(playlist.Name)
                {
                    Size = 10f, Color = Tok.TextSecondary, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }
}
