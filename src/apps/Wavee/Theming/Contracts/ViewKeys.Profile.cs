// ── Theming/Contracts/ViewKeys.Profile.cs — the profile page's published view key (themes plan 6.0, T10c2) ───────────────────
//
// T10c2 publishes the PROFILE page (`Pages.User`, your own profile and another person's): the scroll column and its page-level skeleton region, the round-avatar
// hero (avatar, name, stats, actions) and its collapse into the 56-DIP context band, the sentinel, the blend wash and the clipped magazine, the band (title,
// pivot, Follow as a word), and one region per section (the shelves, their seeds, the empty and failed vacancies and the "isn't available" face).
//
// What stays core (`Profile.PageHost`): the demands and the ProfileAsk plan, the render-cost fold, the load faces (`ProfileLoadRule.Header`), the reveal
// latch, the section plan and each section's body verdict (`ProfileSections`), the card model, the latched hero metrics (`ProfileHeroLayout` over the artist
// hero's tiers), the watched accent, the scroll spy and its anchors, the shell-tint claim, the own-versus-other decision and every verb (open, play, share,
// the context menu, the lists, retry). The tiers are core: the artist page reads the same ones.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The profile page. Mounted by `Profile.PageHost` (the host). Props: <see cref="ProfilePageProps"/>.</summary>
    public static readonly ViewKey<ProfilePageProps> ProfilePage = new("Pages.User");
}
