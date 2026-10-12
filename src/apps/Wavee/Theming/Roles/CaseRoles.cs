// ── Theming/Roles/CaseRoles.cs — how a style cases the words it writes (themes plan 5.4) ─────────────────────────────────
//
// Casing is the style's: Classic writes every label as written, a lowercase style lowers its navigation and headings. It is
// applied at SHAPING (`TextEl.Case`, length-preserving and culture-invariant, no string allocated per render), so it is only
// ever used for words the theme writes (labels, navigation, headings, commands). CONTENT (titles, lyrics, user text) keeps
// its own casing: a transform on a localized string mangles Turkish dotted i and German sharp s and shouts a user's own name
// back at them.

using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The kind of theme-written word a casing applies to.</summary>
public enum LabelKind : byte { Label, Navigation, Heading, Command }

/// <summary>The casing roles of a style, one per <see cref="LabelKind"/>. Every style defines all of them (themes plan S2).</summary>
public sealed record CaseRoles(TextCase Label, TextCase Navigation, TextCase Heading, TextCase Command)
{
    /// <summary>The casing of <paramref name="kind"/>.</summary>
    public TextCase For(LabelKind kind) => kind switch
    {
        LabelKind.Navigation => Navigation,
        LabelKind.Heading => Heading,
        LabelKind.Command => Command,
        _ => Label,
    };
}
