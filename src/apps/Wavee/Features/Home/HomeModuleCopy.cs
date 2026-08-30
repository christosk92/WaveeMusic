using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

// The one place Home's module names cross from the loc system into Wavee.Core. Core is framework-neutral (it has no
// Loc), so the composer takes its labels as a record; this resolves that record once per call site.
//
// Rebuilt per read rather than cached in a static field: Loc is live — a language change re-resolves every string, and a
// snapshot taken at class-init would pin the startup language for the process lifetime.
static class HomeModuleCopy
{
    public static HomeModuleTitles Titles => new(
        JumpBackIn: Loc.Get(Strings.Home.JumpBackIn),
        Recents: Loc.Get(Strings.Home.Recents),
        MadeForYou: Loc.Get(Strings.Home.MadeForYou),
        TopMixes: Loc.Get(Strings.Home.TopMixes),
        Radio: Loc.Get(Strings.Home.Radio),
        UpNext: Loc.Get(Strings.Home.UpNext),
        Audiobooks: Loc.Get(Strings.Home.AudiobooksForYou),
        EditorsPicks: Loc.Get(Strings.Home.EditorsPicks),
        BecauseYouListened: Loc.Get(Strings.Home.BecauseYouListened),
        Podcasts: Loc.Get(Strings.Home.Podcasts));
}
