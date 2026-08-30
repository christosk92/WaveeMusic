using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// SetupCommands.Resolve: the setup wizard's whole footer/label table in one place. Pins every row of the approved
// table verbatim, plus the cross-cutting invariants (never >2 buttons, BlocksDismiss's exact trigger, a rerun never
// reaching a SignIn row, and — the one that stops label rot — every emitted key actually existing in en-US.json).
public class SetupCommandsTests
{
    // ── the whole table, pinned ───────────────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<object?[]> TableRows()
    {
        (SetupCtx Ctx, string? P, SetupButtonKind Kind, bool PEnabled, string? S, bool SEnabled, bool Blocks)[] rows =
        {
            (Ctx(SetupPage.Welcome), Strings.Setup.StartSetup, SetupButtonKind.Accent, true, Strings.Setup.NotNow, true, false),
            (Ctx(SetupPage.Terms), Strings.Setup.Accept, SetupButtonKind.Accent, true, Strings.Setup.Decline, true, false),

            (SignInCtx(SetupSignInPhase.Idle), Strings.Auth.LogIn, SetupButtonKind.Spotify, true, Strings.Auth.Close, true, false),
            (SignInCtx(SetupSignInPhase.Busy), Strings.Auth.SigningIn, SetupButtonKind.Spotify, false, Strings.Auth.Cancel, true, false),
            (SignInCtx(SetupSignInPhase.Done), Strings.Setup.SignIn.YesContinue, SetupButtonKind.Accent, true, Strings.Setup.SignIn.NotMe, true, false),
            (SignInCtx(SetupSignInPhase.Failed), Strings.Auth.TryAgain, SetupButtonKind.Spotify, true, Strings.Auth.Close, true, false),
            (SignInCtx(SetupSignInPhase.Expired), Strings.Auth.GetNewCode, SetupButtonKind.Spotify, true, Strings.Auth.Close, true, false),
            (SignInCtx(SetupSignInPhase.Premium), Strings.Auth.Upgrade, SetupButtonKind.Accent, true, Strings.Auth.UseAnotherAccount, true, false),

            (RuntimeCtx(SetupRuntimeFacet.Offer), Strings.Playback.Runtime.DownloadSetup, SetupButtonKind.Accent, true, Strings.Playback.Runtime.NotNow, true, false),
            (RuntimeCtx(SetupRuntimeFacet.Catalog), Strings.Playback.Runtime.CheckingSupport, SetupButtonKind.Accent, false, Strings.Auth.Cancel, true, true),
            (RuntimeCtx(SetupRuntimeFacet.Versions), Strings.Playback.Runtime.Install, SetupButtonKind.Accent, true, Strings.Playback.Runtime.Back, true, false),
            (RuntimeCtx(SetupRuntimeFacet.Downloading), Strings.Playback.Runtime.Downloading, SetupButtonKind.Accent, false, Strings.Auth.Cancel, true, true),
            (RuntimeCtx(SetupRuntimeFacet.Verifying), Strings.Playback.Runtime.Verifying, SetupButtonKind.Accent, false, null, false, true),
            (RuntimeCtx(SetupRuntimeFacet.Untrusted), Strings.Playback.Runtime.LoadAnyway, SetupButtonKind.Accent, true, Strings.Playback.Runtime.Back, true, false),
            (RuntimeCtx(SetupRuntimeFacet.Ready), Strings.Setup.Continue, SetupButtonKind.Accent, true, null, false, false),
            (RuntimeCtx(SetupRuntimeFacet.Failed), Strings.Playback.Runtime.TryAgain, SetupButtonKind.Accent, true, Strings.Playback.Runtime.NotNow, true, false),

            (Ctx(SetupPage.Appearance), Strings.Setup.Continue, SetupButtonKind.Accent, true, Strings.Setup.DecideForMe, true, false),
            (Ctx(SetupPage.Sidebar), Strings.Setup.UseThisLayout, SetupButtonKind.Accent, true, Strings.Setup.DecideForMe, true, false),
            (Ctx(SetupPage.Sound), Strings.Setup.Continue, SetupButtonKind.Accent, true, Strings.Setup.DecideForMe, true, false),
            (Ctx(SetupPage.Notifications), Strings.Setup.Continue, SetupButtonKind.Accent, true, Strings.Setup.DecideForMe, true, false),

            (ApplyCtx(SetupApplyState.Idle), Strings.Setup.Continue, SetupButtonKind.Accent, true, Strings.Setup.NotNow, true, false),
            (ApplyCtx(SetupApplyState.Running), Strings.Setup.OpenWavee, SetupButtonKind.Accent, false, null, false, true),
            (ApplyCtx(SetupApplyState.Done), Strings.Setup.OpenWavee, SetupButtonKind.Accent, true, null, false, false),
        };

        foreach (var r in rows)
            yield return new object?[] { r.Ctx, r.P, r.Kind, r.PEnabled, r.S, r.SEnabled, r.Blocks };
    }

    [Theory]
    [MemberData(nameof(TableRows))]
    public void Resolve_MatchesTheApprovedTable(SetupCtx ctx, string? primary, SetupButtonKind kind, bool primaryEnabled,
                                                 string? secondary, bool secondaryEnabled, bool blocksDismiss)
    {
        var row = SetupCommands.Resolve(ctx);
        Assert.Equal(primary, row.PrimaryKey);
        Assert.Equal(kind, row.PrimaryKind);
        Assert.Equal(primaryEnabled, row.PrimaryEnabled);
        Assert.Equal(secondary, row.SecondaryKey);
        Assert.Equal(secondaryEnabled, row.SecondaryEnabled);
        Assert.Equal(blocksDismiss, row.BlocksDismiss);
    }

    // ── ShowBack ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShowBack_NeverOnWelcomeOrDone()
    {
        Assert.False(SetupCommands.Resolve(Ctx(SetupPage.Welcome)).ShowBack);
        Assert.False(SetupCommands.Resolve(ApplyCtx(SetupApplyState.Done)).ShowBack);
    }

    [Fact]
    public void ShowBack_StartsOnSignIn_AndHidesWhileBlocked()
    {
        Assert.False(SetupCommands.Resolve(Ctx(SetupPage.Terms)).ShowBack);
        Assert.True(SetupCommands.Resolve(SignInCtx(SetupSignInPhase.Idle)).ShowBack);
        // Catalog blocks dismissal -> Back must be hidden too, even though the page is in the middle-page range.
        Assert.False(SetupCommands.Resolve(RuntimeCtx(SetupRuntimeFacet.Catalog)).ShowBack);
    }

    [Theory]
    [InlineData(SetupPage.SignIn, LoginPhase.LoggedOut, false, true)]
    [InlineData(SetupPage.SignIn, LoginPhase.AwaitingApproval, false, true)]
    [InlineData(SetupPage.SignIn, LoginPhase.AwaitingApproval, true, false)]
    [InlineData(SetupPage.SignIn, LoginPhase.RequestingCode, false, false)]
    [InlineData(SetupPage.Terms, LoginPhase.LoggedOut, false, false)]
    public void NeedsPairingChallenge_OnlyStartsOneMissingIdleChallenge(
        SetupPage page, LoginPhase phase, bool hasChallenge, bool expected)
        => Assert.Equal(expected, SetupCommands.NeedsPairingChallenge(page, phase, hasChallenge));

    // ── invariants over the whole ctx space ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NeverMoreThanTwoButtons()
    {
        foreach (var ctx in AllCtxs())
        {
            var row = SetupCommands.Resolve(ctx);
            int count = (row.PrimaryKey is not null ? 1 : 0) + (row.SecondaryKey is not null ? 1 : 0);
            Assert.True(count <= 2, $"{ctx.Page}: {count} buttons");
        }
    }

    [Fact]
    public void PrimaryKeyIsNeverNull_DisabledInsteadWhereNeeded()
    {
        foreach (var ctx in AllCtxs())
        {
            var row = SetupCommands.Resolve(ctx);
            Assert.NotNull(row.PrimaryKey);
            if (ctx.Page == SetupPage.LocalPlayback && ctx.Runtime == SetupRuntimeFacet.Verifying)
                Assert.False(row.PrimaryEnabled);
            if (ctx.Page == SetupPage.Done && ctx.Apply == SetupApplyState.Running)
                Assert.False(row.PrimaryEnabled);
        }
    }

    [Fact]
    public void BlocksDismiss_IsExactlyTheseCases()
    {
        foreach (var ctx in AllCtxs())
        {
            var row = SetupCommands.Resolve(ctx);
            bool expected = (ctx.Page == SetupPage.LocalPlayback &&
                              ctx.Runtime is SetupRuntimeFacet.Downloading or SetupRuntimeFacet.Verifying or SetupRuntimeFacet.Catalog)
                          || (ctx.Page == SetupPage.Done && ctx.Apply == SetupApplyState.Running);
            Assert.Equal(expected, row.BlocksDismiss);
        }
    }

    [Fact]
    public void ARerunCtx_NeverReachesTheSignInPage()
    {
        // A rerun (launched from Settings, already authenticated) always skips SignIn — SkipSignIn(authed: true) is
        // true, so walking the real page flow from Welcome must never land on SetupPage.SignIn.
        var page = SetupPage.Welcome;
        var seen = new List<SetupPage> { page };
        while (page != SetupPage.Done)
        {
            page = SetupGating.NextPage(page, skipSignIn: true);
            seen.Add(page);
        }
        Assert.DoesNotContain(SetupPage.SignIn, seen);
    }

    [Fact]
    public void EveryEmittedKey_ResolvesToARealLocLeaf()
    {
        var known = FlattenLocKeys();
        Assert.NotEmpty(known);
        foreach (var ctx in AllCtxs())
        {
            var row = SetupCommands.Resolve(ctx);
            if (row.PrimaryKey is { } p) Assert.Contains(p, known);
            if (row.SecondaryKey is { } s) Assert.Contains(s, known);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static SetupCtx Ctx(SetupPage page) => new(page, default, default, default, false, false);
    static SetupCtx SignInCtx(SetupSignInPhase phase) => new(SetupPage.SignIn, phase, default, default, false, false);
    static SetupCtx RuntimeCtx(SetupRuntimeFacet facet) => new(SetupPage.LocalPlayback, default, facet, default, false, false);
    static SetupCtx ApplyCtx(SetupApplyState apply) => new(SetupPage.Done, default, default, apply, false, false);

    static IEnumerable<SetupCtx> AllCtxs()
    {
        yield return Ctx(SetupPage.Welcome);
        yield return Ctx(SetupPage.Terms);
        foreach (SetupSignInPhase p in Enum.GetValues<SetupSignInPhase>()) yield return SignInCtx(p);
        foreach (SetupRuntimeFacet f in Enum.GetValues<SetupRuntimeFacet>()) yield return RuntimeCtx(f);
        yield return Ctx(SetupPage.Appearance);
        yield return Ctx(SetupPage.Sidebar);
        yield return Ctx(SetupPage.Sound);
        yield return Ctx(SetupPage.Notifications);
        foreach (SetupApplyState a in Enum.GetValues<SetupApplyState>()) yield return ApplyCtx(a);
    }

    /// <summary>Every leaf dotted key in en-US.json (the VideoOverrideUxTests precedent), skipping <c>$</c>-prefixed
    /// metadata segments exactly like the loc-keys generator itself does.</summary>
    static HashSet<string> FlattenLocKeys()
    {
        string? path = FindLocFile();
        Assert.NotNull(path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path!));
        var set = new HashSet<string>(StringComparer.Ordinal);
        Walk(doc.RootElement, "", set);
        return set;
    }

    static void Walk(JsonElement element, string prefix, HashSet<string> into)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.StartsWith("$", StringComparison.Ordinal)) continue;
            string dotted = prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name;
            if (prop.Value.ValueKind == JsonValueKind.Object)
                Walk(prop.Value, dotted, into);
            else
                into.Add(dotted);
        }
    }

    static string? FindLocFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "src", "apps", "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
