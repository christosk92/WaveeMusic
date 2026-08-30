using System;

namespace Wavee;

// §C6 + F.4.3 — the PURE decisions behind the selection UX: when the one-time chooser may open, what marks it seen,
// which design the chooser starts on, when the "Customize sidebar" affordance is live, and the int↔enum coercion the
// three preview cards select through.
//
// ENGINE-FREE BY CONSTRUCTION (System + IAppSettings + SidebarDesign + the generated Strings consts). That is
// load-bearing: this file is source-included by src/apps/Wavee.Tests (which has no FluentGpu.Engine reference) so
// SidebarDesignGatingTests drives the REAL marker state machine instead of a copy of it — exactly like SidebarDesign.cs
// and SidebarPinStore.cs. Nothing here may reference Signal<T>, Element, Loc or any other engine type: the picker's
// visuals live in SidebarDesignPicker.cs, and the chooser's scheduling in SidebarOnboardingChrome.cs.
//
// WHY A SEPARATE FILE AT ALL. The gate is one boolean read and the marker one boolean write, but getting either wrong is
// unrecoverable per install: a marker burned too early permanently denies the chooser to the fresh installs it exists
// for, and a marker never written shows a "one-time" dialog on every launch. Both are exactly the kind of thing a unit
// test pins and a code review does not.
static class SidebarDesignGating
{
    /// <summary>The chooser gate (F.4.3): exactly one boolean read, no cross-referencing. <c>SidebarBootstrap</c> already
    /// decided the marker at startup — an EXISTING install has it true (never sees the chooser, stays Classic) and a
    /// FRESH install has it false (and was written Classic explicitly; <c>SidebarBootstrap.Run</c>). Nothing else may
    /// gate the chooser; adding a second condition here is how a fresh install ends up never seeing it.</summary>
    public static bool ShouldShowChooser(IAppSettings? settings)
        => settings is not null && !settings.Get(WaveeSettings.SidebarOnboardingSeen);

    /// <summary>Burn the one-time marker. Called from EVERY chooser exit path (confirm · "Not now" · Escape ·
    /// light-of-modal · a shutdown-time close), so there is no path that leaves it false and the dialog can never appear
    /// twice. Idempotent; returns true only on the transition, for the log line / a test's "flipped once" assertion.
    /// Deliberately does NOT touch <c>sidebar.design</c>: whatever design is applied when the dialog closes is the
    /// user's answer (Classic — what <c>SidebarBootstrap</c> wrote — unless they clicked another card).</summary>
    public static bool MarkChooserSeen(IAppSettings? settings)
    {
        if (settings is null || settings.Get(WaveeSettings.SidebarOnboardingSeen)) return false;
        settings.Set(WaveeSettings.SidebarOnboardingSeen, true);
        return true;
    }

    /// <summary>The design the chooser (and the Settings picker) starts on: the persisted selection, coerced. On a fresh
    /// install <c>SidebarBootstrap</c> has already written Classic, so the chooser opens with Classic selected and the
    /// pane behind it already showing it — the dialog never disagrees with the live sidebar.</summary>
    public static SidebarDesign ActiveDesign(IAppSettings? settings)
        => settings is null ? SidebarDesign.Classic
                            : SidebarDesignInfo.FromInt(settings.Get(WaveeSettings.SidebarDesign));

    /// <summary>Does confirming <paramref name="confirmed"/> offer the "Customize now" follow-up? Only Curated: it is
    /// the only design with a document to edit, and offering the customizer for Classic/Library would navigate to a page
    /// that edits something the user is not looking at.</summary>
    public static bool OffersCustomize(SidebarDesign confirmed) => confirmed == SidebarDesign.Curated;

    /// <summary>Is the "Customize sidebar" affordance live for <paramref name="active"/>? Same rule as
    /// <see cref="OffersCustomize"/>, named separately because it answers a different question in a different place (the
    /// Settings link-row's presence, §C6.3) and the two could legitimately diverge later.</summary>
    public static bool CanCustomize(SidebarDesign active) => active == SidebarDesign.Curated;

    /// <summary>Design → the picker's card value. The values ARE the persisted ints of
    /// <c>WaveeSettings.SidebarDesign</c> (0 Classic · 1 Library · 2 Wavee Curated) — one numbering, so a card index, a
    /// settings value and an enum member can never drift.</summary>
    public static int IndexOf(SidebarDesign design) => (int)design;

    /// <summary>Card value → design, tolerating a hand-edited or future value (falls back to Classic, never throws) —
    /// the one coercion, shared with the persisted-setting path.</summary>
    public static SidebarDesign FromIndex(int value) => SidebarDesignInfo.FromInt(value);

    /// <summary>The card's title loc KEY (not the resolved string — resolution is the UI's job, at render/open time, so
    /// this file stays culture-free and testable).</summary>
    public static string TitleKey(SidebarDesign design) => design switch
    {
        SidebarDesign.LibraryV3 => Strings.Sidebar.Design.V3,
        SidebarDesign.Curated => Strings.Sidebar.Design.Custom,
        _ => Strings.Sidebar.Design.Classic,
    };

    /// <summary>The card's two-line subtitle loc key.</summary>
    public static string SubtitleKey(SidebarDesign design) => design switch
    {
        SidebarDesign.LibraryV3 => Strings.Sidebar.Design.V3Sub,
        SidebarDesign.Curated => Strings.Sidebar.Design.CustomSub,
        _ => Strings.Sidebar.Design.ClassicSub,
    };
}
