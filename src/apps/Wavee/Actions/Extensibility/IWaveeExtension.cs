using System;

namespace Wavee;

// THE TRUSTED COMPILE-TIME SDK SEAM (platform doc "Trusted compile-time SDK"; plan REVISION 2 item 2 + the
// forward-compatibility guardrails). These two interfaces are the shape M3's sandboxed host and M4's public SDK bolt
// onto WITHOUT rework:
//
//   * First-party is literally the trusted extension "wavee" — there is no privileged non-extension registration path.
//     BuiltInExtensionTable.RegisterAll is hand-written today and SOURCE-GENERATED in M4 polish, against this exact
//     call shape (no reflection, no runtime assembly discovery — the AOT contract).
//   * A sandboxed (M3) extension never implements IWaveeExtension in-process: its manifest contributions are replayed
//     onto the SAME registrar by the host, so the registry cannot tell first-party from third-party apart from the
//     key's publisher segment and the permission set. That is the point.
//
// THREADING: Register is called on the UI thread at startup (or, in M3, after the host has marshalled a manifest onto
// the UI thread). A registrar is not thread-safe and is not meant to be retained past Register.

/// <summary>A trusted, compile-time extension. Implementations register their contributions and keep no reference to
/// the registrar afterwards.</summary>
public interface IWaveeExtension
{
    void Register(IWaveeExtensionRegistrar registrar);
}

/// <summary>What an extension may contribute in M1: actions (bindable from the customizer's action picker) and sidebar
/// data sources (the row providers behind <c>SidebarSectionKind.Extension</c>). M3–M5 widen this interface with sections,
/// routes/pages, widgets and settings — additive, so an M1 implementation keeps compiling.</summary>
public interface IWaveeExtensionRegistrar
{
    /// <summary>Contribute one action. The descriptor's <see cref="WaveeActionDescriptor.Key"/> must be a namespaced
    /// <c>publisher.contribution</c> key (<see cref="WaveeExtensionKey.IsValid"/>). A duplicate key is REJECTED — the
    /// first registration wins — and recorded as a diagnostic.</summary>
    void RegisterAction(WaveeActionDescriptor descriptor);

    /// <summary>Contribute one sidebar data source, keyed by its own <c>Id</c> (the same namespaced key scheme:
    /// <c>wavee.library</c>, <c>wavee.artist.topTracks</c>, …). Same first-wins duplicate policy.</summary>
    void RegisterDataSource(ISidebarDataSource source);
}
