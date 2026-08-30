using Wavee.Core.Sidebar;

namespace Wavee;

// ── The built-in sidebar-layout documents (F.3.2) ─────────────────────────────────────────────────────────────────────
// This file owns ENVELOPES, not content. Every section list comes from Wavee.Core.Sidebar's SidebarTemplates (§C2) —
// the single source of truth for what a template contains — and this file only wraps one into a versioned
// SidebarLayoutDocDto. That split is deliberate: the templates are framework-neutral, unit-tested model data in
// Wavee.Core, while the document envelope (version, timestamps, the pin/V3/curated payload split) is persistence
// mechanics and belongs beside the store.
//
// The Curated document is also the CORRUPT-FILE FALLBACK: on any non-None load fault the service loads
// CuratedLayout() in memory, leaves the unreadable file untouched and suppresses writes (locked decision 8).
public static class SidebarLayoutDefaults
{
    /// <summary>The fresh-install / corrupt-fallback layout: the "Wavee Curated" template (§C2.1). A NEW instance with
    /// fresh section ids per call — never share one across two documents.</summary>
    public static SidebarCustomLayout CuratedLayout() => SidebarTemplates.Build(SidebarTemplates.Curated);

    /// <summary>A named template's layout; an unknown id yields Curated (SidebarTemplates.Build's own contract).</summary>
    public static SidebarCustomLayout LayoutOf(string? templateId) =>
        SidebarTemplates.Build(string.IsNullOrEmpty(templateId) ? SidebarTemplates.Curated : templateId!);

    /// <summary>An empty v1 envelope: no pins, no V3 overlay, no curated payload. What the very first commit of an
    /// install that has not opened the customizer writes.</summary>
    public static SidebarLayoutDocDto EmptyDocument() => new() { Version = SidebarLayoutStore.CurrentVersion };

    /// <summary>The fresh-install document: a v1 envelope carrying the Wavee Curated template as the curated payload.</summary>
    public static SidebarLayoutDocDto CuratedDocument() => Document(CuratedLayout());

    /// <summary>A v1 envelope carrying <paramref name="templateId"/>'s sections.</summary>
    public static SidebarLayoutDocDto DocumentOf(string? templateId) => Document(LayoutOf(templateId));

    /// <summary>Wrap an already-built layout in a v1 envelope. <c>UpdatedAtMs</c>/<c>AppVersion</c> are stamped by
    /// <see cref="SidebarLayoutStore.Commit"/>, not here — a default document that was never written must not claim to
    /// have been.</summary>
    public static SidebarLayoutDocDto Document(SidebarCustomLayout layout) => new()
    {
        Version = SidebarLayoutStore.CurrentVersion,
        Curated = SidebarLayoutWire.WriteCurated(layout, SidebarWireCarry.Empty),
        // The shell top-bar band is an ENVELOPE member, so wrapping a layout has to carry it too. A template layout has
        // none (null ⇒ the built-in default ⇒ the member is omitted), so this is a no-op for every built-in document —
        // it exists so wrapping an EDITED layout can never silently drop the user's band.
        TopBar = SidebarLayoutWire.WriteTopBar(layout.TopBar, SidebarWireCarry.Empty),
    };
}
