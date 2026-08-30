namespace Wavee;

/// <summary>The visual state of one step in a step card / step list — <see cref="SetupDecision.StepCard"/> (the
/// local-playback decision column) and the Done page's checklist (a later work package) share this ONE vocabulary
/// rather than two similar-but-different enums drifting apart. <c>Attention</c> is a step that needs the user's eyes
/// (untrusted signature) without having outright failed; <c>Failed</c> is a step that did.
///
/// ENGINE-FREE BY CONSTRUCTION, exactly like <c>SetupGating.cs</c> and <c>SetupPage.cs</c>'s <see cref="SetupPage"/>
/// enum: this file is source-included by <c>Wavee.Tests</c> so a step-state theory test drives the REAL type, never a
/// copy of it.</summary>
public enum SetupStepState : byte { Pending, Current, Done, Attention, Failed }
