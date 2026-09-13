using System;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

/// <summary>The ONE place a playlist mutation failure becomes something the user sees. The decision itself is the pure
/// <see cref="PlaylistEditErrorKinds"/>; this is the engine-side raise (toast severity + localization).</summary>
static class PlaylistEditErrors
{
    /// <summary>The localized sentence for one failure — always a mapped one, never the raw exception text.</summary>
    public static string UserMessage(Exception ex, PlaylistEditVerb verb = PlaylistEditVerb.Generic)
        => Loc.Get(PlaylistEditErrorKinds.KeyFor(PlaylistEditErrorKinds.KindOf(ex), verb));

    public static void Toast(Exception ex, PlaylistEditVerb verb = PlaylistEditVerb.Generic)
    {
        var kind = PlaylistEditErrorKinds.KindOf(ex);
        Toast(kind, verb);
    }

    /// <summary>Raise an ALREADY-CLASSIFIED failure (the bridge chokepoint has the kind in hand and announces it too).</summary>
    public static void Toast(PlaylistMutationFailure kind, PlaylistEditVerb verb = PlaylistEditVerb.Generic)
        => FluentGpu.Controls.Toast.Show(Loc.Get(PlaylistEditErrorKinds.KeyFor(kind, verb)), new ToastOptions
        {
            // "Queued offline" / "still syncing" are not errors: the edit is kept and will land. Dressing them in the
            // error severity told the user their change was lost when it was not.
            Severity = PlaylistEditErrorKinds.IsInformational(kind) ? InfoBarSeverity.Informational : InfoBarSeverity.Error,
        });
}
