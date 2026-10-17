// ── Themes/Zune/Structure/ZuneStructure.ShowEpisodeConcert.cs — the show, the episode and the concerts, registered (themes plan 6.3; P5 Z6b2) ──
//
// Zune's own views for the podcast show page (the show reader style: the rail's slots, the toolbar, the visit head, the headers, the foot), the
// episode page (its slots, the reader, the chapters, the discussion, the rating popover) and the concert surfaces (the hub, an artist's tour dashboard,
// the event page, the feed's tail and the location picker). They win over Classic's stand-in key by key (`ZuneStructure.Register`).
//
// WHAT THE FRAME BRINGS. The show and episode pages stand in Z6a1's detail frame (`ZuneDetailFrame`: the identity column, the panorama, the show
// header of the vertical arm): this registrar draws the slots it hosts. Their row 2 (the episode's four words, the show's "episodes" and its follow
// word, the concert's details · lineup · related concerts) is published by the pages through the `PageChromeSink`.
//
// DECIDED HERE (plan "Zune specifics": each lane records what it decided):
//   · `Concert.Tail` and `Concert.PlacePicker` are OWN (the tail is the grid's shimmering tiles, the picker the flyout's body), not Nothing;
//   · the show style (`StyleKeys.ShowReader`) carries no disabled bell (the stand-in's) — the board draws none;
//   · the hub, the schedule and the picker have no board (owner Q18) and are built from Zune's parts.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterShowEpisodeConcert(ViewRegistryBuilder views)
    {
        // ── the show: the reader's style (the rail's slots, the toolbar, the visit head, the headers, the foot, the seed) ──
        ZuneShow.Register(views);

        // ── the episode: the page's slots, the reader, the chapters, the discussion and the show rating popover ──
        ZuneEpisode.Register(views);

        // ── the concerts: the hub with its feed's tail and the location picker, an artist's tour dashboard, the event page ──
        views.Add(ViewKeys.ConcertsHub, ZuneConcertHub.Template, "zune.concerts.hub");
        views.Add(ViewKeys.ConcertTail, ZuneConcertHub.TailTemplate, "zune.concerts.tail");
        views.Add(ViewKeys.ConcertPlacePicker, ZuneConcertPicker.Template, "zune.concerts.place-picker");
        views.Add(ViewKeys.ArtistConcerts, ZuneConcertSchedule.Template, "zune.concerts.artist-schedule");
        views.Add(ViewKeys.ConcertDetail, ZuneConcertDetail.Template, "zune.concert.detail");
    }
}
