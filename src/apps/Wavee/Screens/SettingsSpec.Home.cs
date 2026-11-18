// ── Screens/SettingsSpec.Home.cs — the Settings home: your setup at a glance (design 3.3) ──────────────────────────────────────
//
// Role: CORE (settings rework phase 1). The home holds NO dials (a second control for a dial would be a second home): its groups are
// state and doors built from the facts and the hint service — At a glance (six read-only tiles, each a link to its dial), Suggestions
// (the hint service's top three) and Recently changed (the last five dial writes). It is a category so it deep-links and keeps its
// scroll like the others; what each group shows lands with the page (phase 3, phase 4).

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory HomeCategory() => new("home", Strings.Settings.Categories.Home.Title,
        Strings.Settings.Categories.Home.Strip, Strings.Settings.Categories.Home.Subtitle, "Home",
    [
        Group("glance", Strings.Settings.Groups.Home.Glance, null),
        Group("suggestions", Strings.Settings.Groups.Home.Suggestions, null),
        Group("recent", Strings.Settings.Groups.Home.Recent, null),
    ]);
}
