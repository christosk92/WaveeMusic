// ── Wavee.ShellTests/ClassicAlt.cs — the test-only theme that switches live (themes plan P4 exit, A4.2) ────────────────
//
// Classic's structure (every view key, the real frame) with a style whose definition differs from Classic's in NON-COLOUR tables:
// other spacing and other radii. A switch between the two can therefore not take the live colour path; it is a theme
// transaction, end to end, on the real mounted shell: prewarm, freeze, the apply flush (persist `theme.id`, activate, install the
// definition, remount the root), settle, thaw. It also declares one Transaction-mode style option (`squareCorners`, radii only)
// for the same-structure case (plan 5.6: an in-place re-render keeps the state of every slot whose template did not change).
//
// Never shipped: it lives in the test project and is installed next to Classic by the facts that need it.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;

namespace Wavee.ShellTests;

/// <summary>The test-only ClassicAlt theme: Classic's structure, a style with other geometry.</summary>
public static class ClassicAlt
{
    public const string Id = "classic-alt";

    /// <summary>The Transaction-mode option: square the card and control corners.</summary>
    public const string SquareCornersQualified = "style.squareCorners";

    /// <summary>Classic's definition (what the appearance seed installs for Classic).</summary>
    public static ThemeDefinition ClassicDefinition { get; } = new ClassicStyle().Definition(OptionValues.Empty, AccentRamp.FluentDefault);

    /// <summary>ClassicAlt's definition: Classic's colours with other spacing and radii (a non-colour change).</summary>
    public static ThemeDefinition Definition { get; } = ClassicDefinition with
    {
        Id = "classic-alt",
        Spacing = ClassicDefinition.Spacing with { S = ClassicDefinition.Spacing.S + 2f, M = ClassicDefinition.Spacing.M + 4f },
        Radii = ClassicDefinition.Radii with { Card = ClassicDefinition.Radii.Card + 4f },
    };

    /// <summary>The definition with <c>squareCorners</c> on.</summary>
    public static ThemeDefinition SquareDefinition { get; } = Definition with
    {
        Id = "classic-alt-square",
        Radii = Definition.Radii with { Control = 0f, Card = 0f, Overlay = 0f },
    };

    /// <summary>Puts Classic back alone, AS ACTIVE: <c>theme.id</c>, the installed set and the installed definition (an install
    /// activates the composition but leaves the token set to the appearance seed, so a fact that ended on ClassicAlt would
    /// otherwise leave its definition behind for the next fact). UI thread.</summary>
    public static void RestoreClassic()
    {
        Platform.Settings.Set(new SettingKey<string>("theme.id", ClassicTheme.Id), ClassicTheme.Id);
        ThemeHost.Install(ClassicTheme.Create());
        Design.SeedPalette(Tok.Theme);
    }

    /// <summary>The composition. Install it next to Classic: <c>ThemeHost.Install(ClassicTheme.Create(), ClassicAlt.Create())</c>.</summary>
    public static ThemeComposition Create() => new(
        new ThemeManifest(
            Id: Id,
            DisplayNameKey: "theme.classic-alt.name",
            ContractMajor: ContractVersion.Major,
            ContractMinor: ContractVersion.Minor,
            StructureId: ClassicStructure.LayerId,
            StyleId: Style.LayerId,
            InheritsFrom: null,
            Availability: ThemeAvailability.Public,
            Preview: new ThemePreview("themes/classic-light.png", "themes/classic-dark.png"),
            DescriptionKey: null),
        new ClassicStructure(),
        new Style());

    private sealed class Style : IStyleLayer
    {
        public const string LayerId = "ClassicAlt.Style";
        private static readonly ToggleOption SquareCorners = new("squareCorners", "theme.classic-alt.square", null, "look", Default: false)
        {
            Apply = ApplyMode.Transaction,
        };
        private static readonly ClassicStyle s_classic = new();

        public string Id => LayerId;
        public ThemeOptionSchema Options { get; } = ThemeOptionSchema.Of(SquareCorners);
        public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent)
            => style.GetBoxed(SquareCorners.Id) is true ? SquareDefinition : ClassicAlt.Definition;
        public StyleMetrics Metrics(OptionValues style) => s_classic.Metrics(style);
    }
}
