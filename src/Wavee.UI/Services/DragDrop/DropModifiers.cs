using System;

namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Modifier keys held at drop time. Captured by the WinUI adapter from
/// CoreWindow / KeyboardCapabilities and forwarded into DropContext so
/// handlers can flip semantics (e.g. Shift = Play Next vs Add To Queue).
/// </summary>
[Flags]
public enum DropModifiers
{
    None    = 0,
    Shift   = 1 << 0,
    Control = 1 << 1,
    Alt     = 1 << 2,
}
