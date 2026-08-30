namespace FluentGpu.Controls;

// The ONE FluentGpu.Controls type App\AppUpdateToasts.cs depends on, so the REAL decision table (source-included from
// src/apps/Wavee) compiles into this test assembly WITHOUT referencing FluentGpu.Controls — which would drag the whole
// engine's control kit in for a four-member enum. Same pattern, same reason as VirtualCollectionSignalShim.cs.
//
// The values MIRROR src/FluentGpu.Controls/InfoBar.cs verbatim (including the explicit ordinals): the tests assert
// which severity a transition plans, and a drifted ordinal would make a green test meaningless.

/// <summary>Severity palette shared by InfoBar and Toast (shim — see file header).</summary>
public enum InfoBarSeverity : byte { Informational = 0, Success = 1, Warning = 2, Error = 3 }
