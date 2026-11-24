// ── Theming/Options/ColourKeys.cs — the stored colour sets (#184, plan 3.5) ──────────────────────────────────────────────────
//
// Flat keys under `theme.palette.*` (WVTH007: a `theme.*` key is minted only under Theming\Options). `ColourChoice` reads and writes
// them; nothing else does. The values are plain strings and ints: no JSON, nothing new for the trimmer. An id the catalog no longer has
// reads as the theme's own colours and is left in place, as an option store leaves a stale value.

namespace Wavee.Theming;

/// <summary>The persisted colour choice.</summary>
public static class ColourKeys
{
    /// <summary>The colour set for light mode (a <c>ColourCatalog</c> id); empty = the theme's own colours.</summary>
    public static readonly SettingKey<string> Light = new("theme.palette.light", "");

    /// <summary>The colour set for dark mode; as <see cref="Light"/>.</summary>
    public static readonly SettingKey<string> Dark = new("theme.palette.dark", "");

    /// <summary>Where the accent comes from while a set is active (<c>AccentSource</c>): 0 the theme · 1 the palette · 2 my colour. Default
    /// the palette's (plan D3): with no set active the source is not read, so the theme's accent stands until a set is picked.</summary>
    public static readonly SettingKey<int> Accent = new("theme.palette.accent", 1);

    /// <summary>The palette's named accent for light mode (<c>NamedAccent.Id</c>); empty = the palette's default.</summary>
    public static readonly SettingKey<string> AccentNameLight = new("theme.palette.accentName.light", "");

    /// <summary>The palette's named accent for dark mode; as <see cref="AccentNameLight"/>.</summary>
    public static readonly SettingKey<string> AccentNameDark = new("theme.palette.accentName.dark", "");

    /// <summary>My own accent, packed 0xAARRGGBB (the encoding a colour option stores); 0 = none picked (the palette's default stands).</summary>
    public static readonly SettingKey<int> AccentOwn = new("theme.palette.accentOwn", 0);

    /// <summary>Artwork colours while a set is active (plan D4): false (the default) keeps the set's exact colours, true lets the artwork tint the
    /// shell and the pages and lead the page accent as the theme's own options say.</summary>
    public static readonly SettingKey<bool> Artwork = new("theme.palette.artwork", false);

    /// <summary>My own light colour set, as its stored line (<c>CustomColours</c>: <c>v1;ground=#…;surface=#…;ink=#…;muted=#…;accent=#…[;surface2=#…]</c>);
    /// empty = none made yet. Picked by storing <c>custom.light</c> in <see cref="Light"/>.</summary>
    public static readonly SettingKey<string> CustomLight = new("theme.palette.custom.light", "");

    /// <summary>My own dark colour set; as <see cref="CustomLight"/>.</summary>
    public static readonly SettingKey<string> CustomDark = new("theme.palette.custom.dark", "");
}
