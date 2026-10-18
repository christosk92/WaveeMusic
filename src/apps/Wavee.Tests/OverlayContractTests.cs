// ── Wavee.Tests/OverlayContractTests.cs — the overlays' and dialog bodies' published keys (themes plan 6.0, T11a) ───────────────────
//
// The account chip and menu, the notification panel, the runtime banner, the update toast's card, the offline strip, the context menu's tile
// and the dialog bodies (the log-out card, the play-a-link card, the signature sheet, the destination picker, the credits sheet) are the
// structure's since T11a: twelve keys. What is pure about the contract: the ids (a published id never changes), Classic registering every one
// under its template id with a clean validation, and the header builder that hands a context menu its tile. The mounted facts are
// `Wavee.ShellTests\OverlaysMountedTests.cs`; the decisions that stay core are `OverlayGatesTests`, `NotifyPanelRulesTests`,
// `PlayLinkRulesTests` and `TrackDrawerRulesTests`.

using FluentGpu.Dsl;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class OverlayContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Overlay.ProfileChip", ViewKeys.OverlayProfileChip.Id);
        Assert.Equal("Overlay.ProfileMenu", ViewKeys.OverlayProfileMenu.Id);
        Assert.Equal("Overlay.Notifications", ViewKeys.OverlayNotifications.Id);
        Assert.Equal("Overlay.RuntimeBanner", ViewKeys.OverlayRuntimeBanner.Id);
        Assert.Equal("Overlay.Toast", ViewKeys.OverlayToast.Id);
        Assert.Equal("Overlay.OfflineStrip", ViewKeys.OverlayOfflineStrip.Id);
        Assert.Equal("Overlay.OfflineVacancy", ViewKeys.OverlayOfflineVacancy.Id);
        Assert.Equal("Menu.Context", ViewKeys.MenuContext.Id);
        Assert.Equal("Dialog.LogoutConfirm", ViewKeys.DialogLogoutConfirm.Id);
        Assert.Equal("Dialog.PlayLink", ViewKeys.DialogPlayLink.Id);
        Assert.Equal("Dialog.Signature", ViewKeys.DialogSignature.Id);
        Assert.Equal("Dialog.FolderPicker", ViewKeys.DialogFolderPicker.Id);
        Assert.Equal("Dialog.Sheet", ViewKeys.DialogSheet.Id);
    }

    [Fact]
    public void Classic_registers_the_twelve_overlay_and_dialog_views()
    {
        var classic = ClassicTheme.Create();

        Assert.Equal("classic.overlay.profile-chip", classic.Registry.TemplateIdOf(ViewKeys.OverlayProfileChip.Id));
        Assert.Equal("classic.overlay.profile-menu", classic.Registry.TemplateIdOf(ViewKeys.OverlayProfileMenu.Id));
        Assert.Equal("classic.overlay.notifications", classic.Registry.TemplateIdOf(ViewKeys.OverlayNotifications.Id));
        Assert.Equal("classic.overlay.runtime-banner", classic.Registry.TemplateIdOf(ViewKeys.OverlayRuntimeBanner.Id));
        Assert.Equal("classic.overlay.toast", classic.Registry.TemplateIdOf(ViewKeys.OverlayToast.Id));
        Assert.Equal("classic.overlay.offline-strip", classic.Registry.TemplateIdOf(ViewKeys.OverlayOfflineStrip.Id));
        Assert.Equal("classic.overlay.offline-vacancy", classic.Registry.TemplateIdOf(ViewKeys.OverlayOfflineVacancy.Id));
        Assert.Equal("classic.menu.context-tile", classic.Registry.TemplateIdOf(ViewKeys.MenuContext.Id));
        Assert.Equal("classic.dialog.logout-confirm", classic.Registry.TemplateIdOf(ViewKeys.DialogLogoutConfirm.Id));
        Assert.Equal("classic.dialog.play-link", classic.Registry.TemplateIdOf(ViewKeys.DialogPlayLink.Id));
        Assert.Equal("classic.dialog.signature", classic.Registry.TemplateIdOf(ViewKeys.DialogSignature.Id));
        Assert.Equal("classic.dialog.folder-picker", classic.Registry.TemplateIdOf(ViewKeys.DialogFolderPicker.Id));
        Assert.Equal("classic.dialog.sheet", classic.Registry.TemplateIdOf(ViewKeys.DialogSheet.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void The_shared_vacancy_key_is_Classics_default_and_Zunes_own_picture()
    {
        Assert.Equal("State.Vacancy", ViewKeys.StateVacancy.Id);
        Assert.Equal("classic.state.vacancy", ClassicTheme.Create().Registry.TemplateIdOf(ViewKeys.StateVacancy.Id));
        Assert.Equal("zune.state.vacancy", Wavee.Themes.Zune.ZuneTheme.Create().Registry.TemplateIdOf(ViewKeys.StateVacancy.Id));
        Assert.Empty(Wavee.Themes.Zune.ZuneTheme.Create().Validate());
    }

    [Fact]
    public void A_context_menu_header_has_a_tile_only_when_it_has_art_or_its_own_leading_element()
    {
        Assert.Null(Actions.Menu.Header(null, "Title", "Sub").Leading);
        Assert.Null(Actions.Menu.Header("", "Title", null).Leading);
        Assert.NotNull(Actions.Menu.Header("https://i.scdn.co/image/abc", "Title", "Sub").Leading);

        var own = new BoxEl();
        Assert.Same(own, Actions.Menu.Header("https://i.scdn.co/image/abc", "Liked Songs", null, leading: own).Leading);
    }

    [Fact]
    public void The_tile_props_compare_by_value_so_an_equal_header_re_renders_nothing()
    {
        Assert.Equal(new ContextMenuTileProps("art", true), new ContextMenuTileProps("art", true));
        Assert.NotEqual(new ContextMenuTileProps("art", true), new ContextMenuTileProps("art", false));
    }
}
