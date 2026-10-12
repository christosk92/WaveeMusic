// ── Themes/Classic/Style/ClassicRoles.cs — the roles of Wavee Classic (themes plan 6.1, T2a) ───────────────────────────
//
// Classic's table IS today's `Design`: every number below was a literal or a rung in `Design.Type`, `Design.Size`,
// `Design.Dock` and `Design.Motion` before T2a, copied as it stood, so a view that reads `R.*` gets exactly the value it
// read from `Design.*`. Nothing here reads a token (`Tok`), a pref or the app: building the table must work at any moment,
// including while the assembly loads (see the module initializer below).
//
// The type roles keep the old doc's contract: every role resolves a SIZE, a LINE HEIGHT and a WEIGHT (its rung's unless it
// states them) and the weight policy is 400 and 600 with SIX documented divergences: the three display-face identity roles
// that keep 700 (ArtistDisplay, ArtistTitle, ArtistCompactTitle: the masthead voice, not UI labels) and the three that keep
// SemiLight 350 (PivotLabel, NpvLyric, StatHero: the same WinUI SemiLight the engine's own pivot control uses). A SEVENTH is a
// regression. PivotLabel (19/25) is the second and last sanctioned off-ramp size (the other is the picker label's own line box).

using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Classic;

/// <summary>The roles of Wavee Classic.</summary>
public static class ClassicRoles
{
    private const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>Classic's role table. One immutable instance: the style publishes it on its definition and every read of
    /// <c>R</c> before a definition is installed falls back to it.</summary>
    public static WaveeStyleRoles Table { get; } = new(Type(), Space(), Motion(), Case());

    /// <summary>Registers this table as the roles a read uses while no installed definition carries any (the boot steps ahead of
    /// the appearance seed, and a unit-test process that never boots the shell). Runs when the assembly loads, so core code
    /// never names a theme and the first read of <c>R</c> cannot see an empty baseline.</summary>
    [ModuleInitializer]
    internal static void RegisterAsBaseline() => R.SetBaseline(Table);

    private static TypeRoles Type()
    {
        // The pair of rungs a role keeps from the engine ramp: Caption 12/16, Body 14/20, BodyStrong 14/20/600, BodyLarge 18/24,
        // Subtitle 20/28/600, Title 28/36/600, TitleLarge 40/52/600, Display 68/92/600. The rest of each role is its deviation.
        var pickQuote = new TextRole(TypeRung.Title, Face: DisplayFace, Weight: 400, Tracking: -12f);
        return new TypeRoles(
            DisplayFace: DisplayFace,
            TrackTitle: new TextRole(TypeRung.BodyStrong),                                           // 14/20/600
            CardTitle: new TextRole(TypeRung.BodyStrong),                                            // the TrackTitle rung, named for the card
            TrackMeta: new TextRole(TypeRung.Caption, Secondary: true),                               // 12/16, secondary ink
            Eyebrow: new TextRole(TypeRung.Caption, Weight: 600, Tracking: 30f),                      // 12/16/600, 30/1000 em, never uppercased
            MicroMeta: new TextRole(TypeRung.Caption, Size: 11f, LineHeight: 15f),                    // 11/15
            DenseMeta: new TextRole(TypeRung.Body, Size: 13f, LineHeight: 18f),                       // 13/18
            DenseTitle: new TextRole(TypeRung.BodyStrong, Size: 13f, LineHeight: 18f),                // 13/18/600
            SheetTitle: new TextRole(TypeRung.BodyLarge, Size: 16f, LineHeight: 22f, Weight: 600),    // 16/22/600
            RailHeader: new TextRole(TypeRung.Subtitle),                                             // 20/28/600
            ModuleHeader: new TextRole(TypeRung.Subtitle, Face: DisplayFace, Tracking: -6f),          // 20/28/600, display face, -6
            PageHero: new TextRole(TypeRung.Title),                                                  // 28/36/600
            DetailHero: new TextRole(TypeRung.Title, Face: DisplayFace, Tracking: -20f),              // 28/36/600, display face, -20
            SurfaceDisplay: new TextRole(TypeRung.TitleLarge, Face: DisplayFace, Weight: 400, Tracking: -12f),   // 40/52/400, display face
            HeroTitle: new TextRole(TypeRung.TitleLarge, Face: DisplayFace, Weight: 600, Tracking: -12f),        // 40/52/600, display face
            ArtistDisplay: new TextRole(TypeRung.Display, Face: DisplayFace, Size: 84f, LineHeight: 96f, Weight: 700, Tracking: -28f, MinSize: 68f),
            ArtistTitle: new TextRole(TypeRung.TitleLarge, Face: DisplayFace, Size: 48f, LineHeight: 60f, Weight: 700, Tracking: -20f, MinSize: 40f),
            ArtistCompactTitle: new TextRole(TypeRung.Title, Face: DisplayFace, Size: 32f, LineHeight: 40f, Weight: 700, Tracking: -12f, MinSize: 28f),
            NowPlayingTitle: new TextRole(TypeRung.Subtitle),                                        // 20/28/600
            PickQuote: pickQuote,                                                                    // 28/36/400, display face, -12
            FoldTitle: pickQuote,                                                                    // the same cut, named for the Fold tile
            PivotLabel: new TextRole(TypeRung.BodyLarge, Face: DisplayFace, Size: 19f, LineHeight: 25f, Weight: 350, Tracking: -6f),
            NpvLyric: new TextRole(TypeRung.Subtitle, Face: DisplayFace, Weight: 350, Tracking: -6f), // 20/28/350, display face
            StatHero: new TextRole(TypeRung.Title, Face: DisplayFace, Weight: 350, Tracking: -6f),    // 28/36/350, display face
            // The Home facet row: 28/36 display words, 400 at rest and 600 selected, 8-DIP plates 40 tall, 24 apart, the first item
            // pulled -8 so its WORD sits on the page's leading edge; the compact form is one rung down with the plates 8 apart.
            FacetTitle: new FacetRole(28f, 36f, DisplayFace, 0f, 400, 600, new Edges4(8f, 2f, 8f, 2f), 40f, 24f, -8f),
            FacetCompact: new FacetRole(20f, 28f, DisplayFace, 0f, 400, 600, new Edges4(8f, 2f, 8f, 2f), 40f, 8f, -8f));
    }

    private static SpaceRoles Space() => new(
        SectionGap: 32f, SectionGapWide: 40f, PageMaxW: 1600f,
        ShelfCardMin: 148f, ShelfCardMax: 188f, GridGap: 12f,
        WideTileMin: 330f, WideTileMax: 440f, WideTileAspect: 16f / 9f,
        FadeChip: 16f, FadeShelf: 24f, FadeRail: 36f,
        FacePileFlyoutW: 280f, FacePileFlyoutH: 360f, FacePileListW: 264f, FacePileListH: 344f,
        DockReserve: 72f);   // the player bar's height (Design.Size.PlayerBarH)

    // Chips and small toggles barely move; buttons and pills a little; a round media affordance the most. Stagger: 40 ms per list
    // item (capped by Design.Entrance), 45 ms for the two lines of a drill-in masthead.
    private static MotionRoles Motion() => new(
        ScaleSubtle: new ScaleTier(1.02f, 0.98f),
        ScaleStandard: new ScaleTier(1.04f, 0.96f),
        ScaleEmphatic: new ScaleTier(1.07f, 0.92f),
        StaggerMs: 40f,
        MastheadStaggerMs: 45f);

    // Classic writes every label as written; sentence case is the Fluent register and a transform on a localized string is a bug.
    private static CaseRoles Case() => new(TextCase.AsWritten, TextCase.AsWritten, TextCase.AsWritten, TextCase.AsWritten);
}
