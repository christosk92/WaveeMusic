// ── Themes/Classic/Structure/Views/Liked/ClassicLiked.cs — registers Classic's Liked Songs views (themes plan 6.1, T9c) ──────────────────
//
// The cover (nine treatments, palette leaves, ambient loops), its "Cover style" picker and flyout, and the chip rail. The page, the cover's
// snapshot and demand, the chip set and the table's filter stay core (`User.LikedPage`, `User.LikedCover`'s hosts).

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's Liked Songs registration.</summary>
internal static class ClassicLiked
{
    /// <summary>Registers the Liked views; called from <c>ClassicStructure.Register</c>.</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.LikedCover, ClassicLikedCover.CoverTemplate, "classic.liked.cover");
        views.Add(ViewKeys.LikedCoverPicker, ClassicLikedCover.PickerTemplate, "classic.liked.cover-picker");
        views.Add(ViewKeys.LikedCoverStyleFlyout, ClassicLikedCover.FlyoutTemplate, "classic.liked.cover-style-flyout");
        views.Add(ViewKeys.LikedChipBar, ClassicLikedChipBar.Template, "classic.liked.chip-bar");
    }
}
