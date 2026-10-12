// ── Theming/Composition/ThemeManifest.cs — what a theme says about itself, and the two layers it is built from ─────────
//
// A theme is a COMPOSITION of two layers (themes plan 1.2): a STRUCTURE (which views exist, how they are arranged, what the
// shell offers) and a STYLE (the `ThemeDefinition`: colours, type, shapes, motion, roles, metrics). Wavee Classic is
// Classic.Structure x Classic.Style; Panorama is Zune.Structure x WinUi.Style. The manifest names the composition and the
// contract version it was built against; the two interfaces are what core calls into. Core never names a theme.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>Who can pick a theme.</summary>
public enum ThemeAvailability : byte
{
    /// <summary>Everyone.</summary>
    Public,
    /// <summary>Only with Developer mode on; otherwise resolution falls back to Classic.</summary>
    Developer,
}

/// <summary>The preview images of a theme picker card (asset paths, light and dark).</summary>
public sealed record ThemePreview(string LightAsset, string DarkAsset);

/// <summary>The identity of a composition. <paramref name="Id"/> is lowercase letters, digits and dashes (it is part of
/// every setting key of the theme); <paramref name="InheritsFrom"/> names the theme this one derives from, or null (T1 stores
/// it and reads it nowhere; the first-run option seeding that may use it is a later PR).</summary>
public sealed record ThemeManifest(string Id, string DisplayNameKey, int ContractMajor, int ContractMinor,
    string StructureId, string StyleId, string? InheritsFrom, ThemeAvailability Availability, ThemePreview Preview);

/// <summary>The view roles a structure needs the style to define (the role ids of <c>Wavee.Theming.Roles</c>).</summary>
public readonly record struct RoleSet(ImmutableArray<string> RoleIds)
{
    /// <summary>No role required.</summary>
    public static RoleSet None => new(ImmutableArray<string>.Empty);
}

/// <summary>The STRUCTURE layer of a theme: registers its views and decides the policies and capabilities of the shell.</summary>
public interface IStructureLayer
{
    /// <summary>The structure id (<c>Classic.Structure</c>); equals <see cref="ThemeManifest.StructureId"/>.</summary>
    string Id { get; }

    /// <summary>Registers every view and style table of the structure. Called once, at install.</summary>
    void Register(ViewRegistryBuilder views);

    /// <summary>The structure's options, including its structure parameters (<see cref="OptionVisibility.Hidden"/>).</summary>
    ThemeOptionSchema Options { get; }

    /// <summary>The policies for these option values and the style's metrics.</summary>
    ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics);

    /// <summary>What the shell offers for these option values.</summary>
    ShellCapabilities Capabilities(OptionValues structure);

    /// <summary>The roles the style must define for this structure.</summary>
    RoleSet RequiredRoles { get; }
}

/// <summary>The STYLE layer of a theme: the definition and the metrics.</summary>
public interface IStyleLayer
{
    /// <summary>The style id (<c>Classic.Style</c>); equals <see cref="ThemeManifest.StyleId"/>.</summary>
    string Id { get; }

    /// <summary>The style's options.</summary>
    ThemeOptionSchema Options { get; }

    /// <summary>The definition for these option values and the OS accent. Pure and memoized per (stamp, accent): the same
    /// inputs return the same instance. <c>AppRoles</c> is the style's <c>WaveeStyleRoles</c>.</summary>
    ThemeDefinition Definition(OptionValues style, AccentRamp osAccent);

    /// <summary>The metrics for these option values.</summary>
    StyleMetrics Metrics(OptionValues style);
}
