// ── Theming/Contracts/CoreHostAttribute.cs — marks a type as a core host (WVTH008) ─────────────────────────────────────

namespace Wavee.Theming.Contracts;

/// <summary>Marks a class or struct as a CORE HOST: it positions slots (<c>ctx.View</c>, <c>Slots.View</c>) and wires
/// engine behaviour controls that arrive as slots, but builds no presentation of its own. WVTH008 reports any element
/// construction, token read or role read inside a marked type, with no baseline. A surface PR puts the attribute on the
/// host it extracts as part of the same change.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class CoreHostAttribute : Attribute
{
}
