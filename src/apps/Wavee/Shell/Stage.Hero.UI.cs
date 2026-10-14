// ── Shell/Stage.Hero.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// The ARTIST layout's facts: HeroFacts (what the playing artist's header looks like), zero-size machinery
//
// Role: UI (the core half: the photo, its Ken Burns, the scrims and the name are the structure's `Stage.Hero` and `Stage.ArtistName` views,
// `Themes\Classic\Structure\Views\Stage\ClassicStageHero.cs`, T7a2)
// Owner: K
// Wave: 7
// Budget: 330 lines
// Spec: Hero.dc.html / HeroLyrics.dc.html; Stage.Layouts.cs (HeroRules, the geometry)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The photo's layer asks the stage context for the header the lookups found (`HeroFact`, `HeroArtist`, `NextHeroUrl`); this file is the
// lookups. They run only while the Artist layout is the stored choice in Visualizer mode (Card users never pay for artist lookups).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee;

public static partial class Stage
{
    // ══ the facts ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Zero-size. Asks the first billed artists for their overview, walks them in billing order (<see cref="HeroRules.Pick"/>) and writes
    /// <c>ctx.HeroFact</c> / <c>ctx.HeroArtist</c>; also ensures the NEXT queued track's first artist and publishes its header url. Mounted only
    /// while the Artist layout is stored in Visualizer mode (Card users never pay for artist lookups); its unmount resets the facts, so a stale
    /// None never flashes Large art on the next mount.</summary>
    sealed class HeroFacts : Component
    {
        readonly bool[] _known = new bool[HeroRules.MaxBilled], _has = new bool[HeroRules.MaxBilled];

        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            // reads Artists.Changed / TrackArtists.Changed / Queue.Changed and the track key: re-runs on exactly those, before any paint
            UseSignalEffect(() =>
            {
                _ = Entities.ScopeEpoch.Value;
                _ = Entities.Current.Artists.Changed.Value;
                _ = Entities.Current.Edges.TrackArtists.Changed.Value;
                _ = Entities.Current.Edges.Queue.Changed.Value;
                var track = ctx.RowValue();
                ReadOnlySpan<int> slots = track.IsValid ? track.ArtistSlots : default;
                int n = Math.Min(HeroRules.MaxBilled, slots.Length);
                for (int i = 0; i < n; i++)
                {
                    var a = new Artist(slots[i]);
                    _known[i] = false; _has[i] = false;
                    if (!a.IsValid) continue;
                    Entities.Ensure(a, ArtistFields.Overview, i == 0 ? FetchPriority.Visible : FetchPriority.Prefetch);
                    _known[i] = a.Knows(ArtistFields.Header);
                    _has[i] = !a.HeaderId.IsEmpty;
                }
                var (state, index) = HeroRules.Pick(_known.AsSpan(0, n), _has.AsSpan(0, n));
                ctx.HeroFact.SetIfChanged(state);
                ctx.HeroArtist.SetIfChanged(state == HeroState.Header && index >= 0 ? slots[index] : 0);

                // the next track's header, decoded hidden under the hero shortly before the switch
                string next = "";
                if (Queue.UpNext(out int start, out int length) && length > 0)
                {
                    var r = Queue.RefAt(start);
                    if (!r.IsNone && r.Kind == EntityKind.Track)
                    {
                        var nextSlots = new Track(r.Slot).ArtistSlots;
                        if (nextSlots.Length > 0)
                        {
                            var na = new Artist(nextSlots[0]);
                            if (na.IsValid)
                            {
                                Entities.Ensure(na, ArtistFields.Overview, FetchPriority.Prefetch);
                                if (na.Knows(ArtistFields.Header) && !na.HeaderId.IsEmpty) next = Controls.ArtUrl(na.HeaderId) ?? "";
                            }
                        }
                    }
                }
                ctx.NextHeroUrl.SetIfChanged(next);
            });
            UseEffect(() => () => { ctx.HeroFact.Value = HeroState.Pending; ctx.HeroArtist.Value = 0; ctx.NextHeroUrl.Value = ""; }, DepKey.Empty);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }
}
