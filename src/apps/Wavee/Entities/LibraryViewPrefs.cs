// ── Entities/LibraryViewPrefs.cs ───────────────────────────────────────────────────────────────────────────────────────
// the library page's per-kind view state as a typed pref: column widths, sort, direction, view, size, selection, the
// artists' scope and reader sort — one record read at mount, one verb per writer
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X41); library-rework-implementation.md §5.3
//
// The persisted shape is `Platform.Keys.Library*(kind)` (`library.<kind>.*`, built per kind by the keys themselves; the
// string shape is persisted and never changes). The two COLUMN WIDTHS are the exception: they are the active theme's STATE
// (`theme.<id>.state.library.<kind>.leftw` / `.midw`, M13; owner Q6: pane sizes are per theme), declared below because the page host
// writes them, clamped to the grip ranges. The page used to read and write each key by hand in its constructor and
// its `SaveState`; this is the ONE door, so a library page in any theme seeds and persists the same keys the same way.
//
// RETIRED KEYS (never reuse them): `library.<kind>.album.desc` / `.album.view` / `.album.size` steered the deleted
// discography grid; nothing reads or writes them (see the note in `Platform.Keys`). `library.<kind>.album.sort` IS live:
// it is where `Artist.Reader`'s own sort persists (`AlbumSort` below).

namespace Wavee;

/// <summary>One library kind's persisted view. <see cref="LeftW"/> and <see cref="MidW"/> are READ here but written by
/// their own verbs (<see cref="LibraryViewPrefs.SetLeftW"/>, <see cref="LibraryViewPrefs.SetMidW"/>: a column drag commits
/// them on release), never by <see cref="LibraryViewPrefs.Save"/>. <see cref="Scope"/> (0 in your library · 1 all
/// releases), <see cref="AlbumKey"/> and <see cref="AlbumSort"/> are the artists' reader's and are persisted only for it.</summary>
public readonly record struct LibraryViewState(
    float LeftW, float MidW, int Sort, bool Desc, int View, int Size,
    string SelectedKey, string AlbumKey, int Scope, int AlbumSort);

public static class LibraryViewPrefs
{
    /// <summary>The clamp of the persisted column widths: the grip ranges of Classic's library page (`ClassicLibraryRules.LeftMin/LeftMax/MidMin/MidMax`, which a test pins
    /// equal; core may not name the theme, and the state key must clamp on read).</summary>
    public const float LeftMin = 240f, LeftMax = 560f, MidMin = 300f, MidMax = 620f;

    /// <summary>The state key of a kind's navigator column width: the artists' reader is narrower by default (280, the rest 340); the range is the grip's.</summary>
    public static Theming.StateKey<float> LeftWKey(string kind)
        => new("library." + kind + ".leftw", kind == "artists" ? 280f : 340f, LeftMin, LeftMax);

    /// <summary>The state key of a kind's middle column width (the search shapes' third column).</summary>
    public static Theming.StateKey<float> MidWKey(string kind)
        => new("library." + kind + ".midw", 440f, MidMin, MidMax);

    /// <summary>The kind's persisted view: every key at its stored value, else the key's own per-kind default.</summary>
    public static LibraryViewState Read(string kind)
    {
        var s = Platform.Settings;
        return new LibraryViewState(
            LeftW: Theming.ThemeState.Get(LeftWKey(kind)),
            MidW: Theming.ThemeState.Get(MidWKey(kind)),
            Sort: s.Get(Platform.Keys.LibrarySort(kind)),
            Desc: s.Get(Platform.Keys.LibraryDesc(kind)),
            View: s.Get(Platform.Keys.LibraryView(kind)),
            Size: s.Get(Platform.Keys.LibrarySize(kind)),
            SelectedKey: s.Get(Platform.Keys.LibrarySelected(kind)),
            AlbumKey: s.Get(Platform.Keys.LibraryAlbumKey(kind)),
            Scope: s.Get(Platform.Keys.LibraryScope(kind)),
            AlbumSort: s.Get(Platform.Keys.LibraryAlbumSort(kind)));
    }

    /// <summary>The nav column's width, committed on release of its grip.</summary>
    public static void SetLeftW(string kind, float width) => Theming.ThemeState.Set(LeftWKey(kind), width);

    /// <summary>Has the user set the kind's navigator width (a grip release stored a width other than the default)? Until then the structure's tier default applies
    /// (contract 1.21; `Theming.ThemeState.IsCustomized`: a store that never held the key, or holds the default an older build saved, is unset).</summary>
    public static bool LeftWCustomized(string kind) => Theming.ThemeState.IsCustomized(LeftWKey(kind));

    /// <summary>The navigator's resting width: the <paramref name="stored"/> one once the user set it (<paramref name="customized"/>), else the structure's width for the
    /// <paramref name="content"/> tier (the artists' kind reads the reader's), clamped to the grip's range; the stored (the key's default) where the structure has none.</summary>
    public static float RestingLeftW(string kind, float stored, bool customized, Theming.Contracts.WidthTier content, Theming.LibraryTierPolicy policy)
    {
        if (customized) return stored;
        float tier = policy.NavWidthFor(content, reader: kind == "artists");
        return float.IsFinite(tier) ? Math.Clamp(tier, LeftMin, LeftMax) : stored;
    }

    /// <summary>The middle column's width (the search shapes' third column), committed on release of its grip.</summary>
    public static void SetMidW(string kind, float width) => Theming.ThemeState.Set(MidWKey(kind), width);

    /// <summary>Persist the view state (sort, direction, view, size, selection). The artists' reader keys (album key,
    /// scope, reader sort) follow only when <paramref name="artists"/> — the other kinds never touch them. The widths are
    /// not written here (see <see cref="LibraryViewState"/>). Every key is written whether or not it changed, as the page
    /// always did.</summary>
    public static void Save(string kind, in LibraryViewState state, bool artists)
    {
        var s = Platform.Settings;
        s.Set(Platform.Keys.LibrarySort(kind), state.Sort);
        s.Set(Platform.Keys.LibraryDesc(kind), state.Desc);
        s.Set(Platform.Keys.LibraryView(kind), state.View);
        s.Set(Platform.Keys.LibrarySize(kind), state.Size);
        s.Set(Platform.Keys.LibrarySelected(kind), state.SelectedKey);
        if (!artists) return;
        s.Set(Platform.Keys.LibraryAlbumKey(kind), state.AlbumKey);
        s.Set(Platform.Keys.LibraryScope(kind), state.Scope);
        s.Set(Platform.Keys.LibraryAlbumSort(kind), state.AlbumSort);
    }
}
