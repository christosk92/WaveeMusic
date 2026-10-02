// ── Shell/Shell.Omnibar.Rows.cs ────────────────────────────────────────────────────────────────────────────────────
// The search flyout's rich-row RULES, engine-free: which entity an omnibar row stands for, which menu it carries, whether
// it drags, and its surface shape. The flyout (Shell.Masthead.UI.cs) renders each row on the shared media surface in slot
// mode; these decisions are pinned by OmnibarRowRulesTests.
//
// Role: UI
// Owner: I
// Wave: interaction-consistency (WP7)
// Budget: 80 lines

namespace Wavee;

public static class OmnibarRowRules
{
    /// <summary>Which menu a row's right-click / "…" opens.</summary>
    public enum MenuKind : byte { None, Track, Container }

    /// <summary>The entity kind a row's uri resolves to (a podcast is a Show). Genres and audiobooks have no table:
    /// <see cref="EntityKind.Unknown"/>.</summary>
    public static EntityKind EntityKindOf(Shell.Omnibar.ItemKind kind) => kind switch
    {
        Shell.Omnibar.ItemKind.Track => EntityKind.Track,
        Shell.Omnibar.ItemKind.Episode => EntityKind.Episode,
        Shell.Omnibar.ItemKind.Album => EntityKind.Album,
        Shell.Omnibar.ItemKind.Artist => EntityKind.Artist,
        Shell.Omnibar.ItemKind.Playlist => EntityKind.Playlist,
        Shell.Omnibar.ItemKind.Podcast => EntityKind.Show,
        Shell.Omnibar.ItemKind.User => EntityKind.User,
        _ => EntityKind.Unknown,
    };

    /// <summary>A track gets the track menu; an album / artist / playlist / show the container menu; the rest (episode,
    /// profile, genre, audiobook) none — the same set as the search page (<see cref="Search.HasMenu"/>).</summary>
    public static MenuKind MenuOf(Shell.Omnibar.ItemKind kind)
        => !Search.HasMenu(EntityKindOf(kind)) ? MenuKind.None
         : kind == Shell.Omnibar.ItemKind.Track ? MenuKind.Track : MenuKind.Container;

    /// <summary>Every row but a profile, a genre and an audiobook is a drag source (<see cref="SearchHitRules.Drags"/>).</summary>
    public static bool Drags(Shell.Omnibar.ItemKind kind) => SearchHitRules.Drags(EntityKindOf(kind));

    /// <summary>The row's surface shape: a 44 art square in a 58 floor, its "…" in a lane of its own after the heart /
    /// Follow pill — the search page's rule (<see cref="SearchHitRules.RowMenu"/>), for the same reason.</summary>
    public static SurfaceShape RowShape => Shape.Row(44f) with { MinHeight = 58f, Menu = SearchHitRules.RowMenu };
}
