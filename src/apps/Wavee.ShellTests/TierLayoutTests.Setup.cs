// ── Wavee.ShellTests/TierLayoutTests.Setup.cs — Classic's setup wizard at every tier size and threshold edge (themes plan 3.5; TC8) ──────────────
//
// The wizard is an overlay the core host opens, so it is mounted the way `SetupMountedTests` mounts it: `ViewKeys.SetupWizard` under Classic at the plan 3.5 sizes (300 x 640,
// 300 x 330, 400 x 860, 800 x 330, 800 x 600, 1440 x 960, 1920 x 1056, 2048 x 1114, 2560 x 1440, 3840 x 2160) and at every threshold edge of Classic's ladder (527/528, 567 from below,
// 1007/1008, 1047 from below, 1879/1880 from above, 1919/1920), resized into the size from its `From` width where the edge is directional. For each size and for the Terms, Sign in and
// Theme pages it asserts, on the facts, the record and the node geometry, never on pixels:
//
//   · the window tier is final in the FIRST frame and does not change on the next, and the record in use is the structure's record for that tier;
//   · the root is the record's size (the window for Compact's sheet, whose top inset keeps the 48 title row clear; the clamped plate otherwise) and lies inside the window;
//   · the icon column exists exactly where the record says (192 at Standard, 240 at Huge) and never overlaps the page; the footer has the record's form (stacked at Compact) and lies inside
//     the plate with the page region above it, so a body taller than the window scrolls (the 330-tall windows) instead of clipping the footer or its buttons;
//   · Back is the record's size and, at Compact, every hit target of the sheet is at least 40 x 40.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    /// <summary>The sign-in flows with nothing behind them (a pairing code on offer, so the scan card draws its QR).</summary>
    sealed class SetupTierSignIn : ISetupSignInHost
    {
        public SetupSignInFacts Facts() => new(Setup.SignInFacet.Idle, Handed: false, Minting: false, BusyMessageKey: Strings.Auth.WaitingApproval, FailureKey: "", NeedsPairingCode: false, UserCode: "ABCD-1234");
        public SetupPairingFacts Pairing() => new(true, false, false, "ABCD-1234", "https://example.test/pair", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000);
        public SetupLoginStepFacts Step() => new(1, false);
        public bool NeedsPairingChallenge(Setup.WizardPage activePage) => false;
        public SetupAccountFacts Account() => new("Fixture Person", null, Premium: true);
        public void StartBrowser() { }
        public void StartPairing() { }
        public void CancelBrowser() { }
        public void Retry() { }
        public void SwitchAccount() { }
        public void CloseDoor() { }
        public void OpenSignUp() { }
        public void OpenPremium() { }
        public void OpenExternal(string uri) { }
    }

    static SetupWizardProps SetupTierProps(Setup.WizardModel session) => new()
    {
        Session = session,
        Terms = new SetupTermsProps { Session = session, OpenPrivacy = static () => { }, Consent = new SetupConsent(3, static () => 0, static _ => { }) },
        SignIn = new SetupSignInPageProps { Session = session, Host = new SetupTierSignIn() },
        Theme = new SetupThemeProps { ThemePicker = static () => new FluentGpu.Dsl.BoxEl { Key = "test:theme-picker" }, ThemeOptions = static () => new FluentGpu.Dsl.BoxEl { Key = "test:theme-options" } },
        LocalPlayback = new SetupLocalPlaybackProps { Session = session, OpenSignature = static (_, _) => { }, RevealFolder = static _ => { } },
    };

    [Theory, MemberData(nameof(FrameSizes))]
    public void The_setup_wizard_holds_its_record_at_every_tier(string sizeName)
    {
        var size = Sizes[sizeName];
        host.Run(() =>
        {
            var session = new Setup.WizardModel(Setup.WizardEntry.FirstRun, signedIn: false);
            try
            {
                using var harness = ViewHarness.Mount(ViewKeys.SetupWizard, SetupTierProps(session),
                    new ViewHarnessOptions { Composition = ClassicTheme.Create(), WidthDip = size.From ?? size.Width, HeightDip = size.Height });
                if (size.From is not null)
                {
                    harness.RunFrames(20);
                    harness.Resize(size.Width, size.Height);   // the directional edges are reached by a resize from `From`
                }
                harness.RunFrames(1);

                // The tier is final in this frame, and the next one does not change it.
                AssertSetupTier(harness, size);
                harness.RunFrames(1);
                AssertSetupTier(harness, size);
                harness.RunFrames(40);

                AssertSetupPage(harness, size, "terms");
                session.Advance(Setup.WizardPage.SignIn);
                harness.RunFrames(60);
                AssertSetupPage(harness, size, "sign-in");
                session.Advance(Setup.WizardPage.Theme);
                harness.RunFrames(60);
                AssertSetupPage(harness, size, "theme");
                AssertSetupBack(harness, size);
            }
            finally { session.End(); }
        });
    }

    static void AssertSetupTier(ViewHarness harness, TierSize size)
    {
        var tiers = harness.Facts.Peek().Tiers;
        Assert.True(tiers.Window == size.Window, $"{size.Name}: the window tier is {tiers.Window}, not {size.Window}");
        Assert.Equal(size.Window, ClassicSetupTiers.Of(tiers).Tier);
    }

    static void AssertSetupPage(ViewHarness harness, TierSize size, string page)
    {
        var tiers = harness.Facts.Peek().Tiers;
        var layout = ClassicSetupTiers.Of(tiers);
        var scene = harness.Shell.Host.Scene;
        string at = $"{size.Name} / {page}";

        RectF root = harness.AbsoluteBounds(harness.Node("setup:root"));
        RectF plate = harness.AbsoluteBounds(harness.Node("setup:plate"));
        RectF footer = harness.AbsoluteBounds(harness.Node("setup:footer"));

        // The root is the record's size and lies inside the window; the sheet is the window under its title-row inset.
        Assert.Equal(layout.RootWidth(size.Width), root.W, 1f);
        Assert.Equal(layout.RootHeight(size.Height), root.H, 1f);
        Assert.True(root.X >= -0.5f && root.X + root.W <= size.Width + 0.5f, $"{at}: the root runs outside the window horizontally ({root})");
        Assert.True(root.Y >= -0.5f && root.Y + root.H <= size.Height + 0.5f, $"{at}: the root runs outside the window vertically ({root})");
        Assert.True(plate.X >= -0.5f && plate.X + plate.W <= size.Width + 0.5f, $"{at}: the plate runs outside the window horizontally ({plate})");
        Assert.True(plate.Y >= -0.5f && plate.Y + plate.H <= size.Height + 0.5f, $"{at}: the plate runs outside the window vertically ({plate})");
        if (layout.Sheet)
        {
            Assert.Equal(0f, plate.X, 0.5f);
            Assert.Equal(layout.TopInset, plate.Y, 0.5f);                       // the title row stays clear
            Assert.Equal(size.Width, plate.W, 0.5f);
            Assert.Equal(size.Height - layout.TopInset, plate.H, 0.5f);
        }

        // The footer is the record's form, whole inside the plate, and never squeezed: a body taller than the window scrolls above it.
        Assert.True(footer.X >= plate.X - 0.5f && footer.X + footer.W <= plate.X + plate.W + 0.5f, $"{at}: the footer runs outside the plate ({footer} in {plate})");
        Assert.True(footer.Y >= plate.Y - 0.5f && footer.Y + footer.H <= plate.Y + plate.H + 0.5f, $"{at}: the footer is clipped by the plate ({footer} in {plate})");
        if (layout.FooterStacked) Assert.True(footer.H > layout.FooterHeight + 20f && footer.H < plate.H, $"{at}: the stacked footer is {footer.H} tall");
        else Assert.Equal(layout.FooterHeight, footer.H, 0.5f);

        var frame = harness.Find("setup:frame:" + FrameIndex(page));
        Assert.False(frame.IsNull, $"{at}: the page frame is mounted");
        RectF frameRect = harness.AbsoluteBounds(frame);
        Assert.True(frameRect.Y + frameRect.H <= footer.Y + 0.5f, $"{at}: the page overlaps the footer ({frameRect} over {footer})");
        Assert.True(frameRect.X >= plate.X - 0.5f && frameRect.X + frameRect.W <= plate.X + plate.W + 0.5f, $"{at}: the page runs outside the plate ({frameRect})");

        // The icon column: the record's width where it shows, and it never overlaps the page beside it.
        var layoutNode = harness.Find("setup:layout:wide");
        if (layoutNode.IsNull) layoutNode = harness.Find("setup:layout:compact");
        Assert.False(layoutNode.IsNull, $"{at}: the page's layout node");
        var first = scene.FirstChild(layoutNode);
        RectF firstRect = harness.AbsoluteBounds(first);
        if (layout.ShowsIcon)
        {
            Assert.Equal(layout.IconColumn, firstRect.W, 0.5f);
            var second = scene.NextSibling(first);
            Assert.False(second.IsNull, $"{at}: the page sits beside the icon column");
            RectF secondRect = harness.AbsoluteBounds(second);
            Assert.True(firstRect.X + firstRect.W <= secondRect.X + 0.5f, $"{at}: the icon column overlaps the page ({firstRect} / {secondRect})");
        }
        else
        {
            Assert.True(firstRect.W > layout.IconColumn + 1f && firstRect.W > 0f, $"{at}: no icon column, the page's column is first ({firstRect})");
            Assert.True(layout.IconColumn == 0f);
        }

        if (size.Window == WidthTier.Compact) AssertSetupTargets(harness, size, at);
    }

    static int FrameIndex(string page) => page switch
    {
        "terms" => (int)Setup.WizardPage.Terms,
        "sign-in" => (int)Setup.WizardPage.SignIn,
        _ => (int)Setup.WizardPage.Theme,
    };

    /// <summary>Back (the Theme page's) is the record's size, and the layer it rides in puts it where the record says.</summary>
    static void AssertSetupBack(ViewHarness harness, TierSize size)
    {
        var layout = ClassicSetupTiers.Of(harness.Facts.Peek().Tiers);
        var back = harness.Find("setup:back");
        Assert.False(back.IsNull, $"{size.Name}: the Theme page shows Back");
        var button = harness.Shell.Host.Scene.FirstChild(back);
        RectF r = harness.AbsoluteBounds(button);
        Assert.Equal(layout.BackSize, r.W, 0.5f);
        Assert.Equal(layout.BackSize, r.H, 0.5f);
        RectF plate = harness.AbsoluteBounds(harness.Node("setup:plate"));
        Assert.True(r.X >= plate.X - 0.5f && r.X + r.W <= plate.X + plate.W + 0.5f, $"{size.Name}: Back runs outside the plate ({r})");
        Assert.True(r.Y >= plate.Y - 0.5f && r.Y + r.H <= plate.Y + plate.H + 0.5f, $"{size.Name}: Back runs outside the plate ({r})");
    }

    /// <summary>At Compact every hit target on the sheet (the footer's buttons, Back, the cards, the links, the radios) is at least 40 x 40.</summary>
    static void AssertSetupTargets(ViewHarness harness, TierSize size, string at)
    {
        var scene = harness.Shell.Host.Scene;
        int targets = 0;
        foreach (var n in harness.Subtree(harness.Node("setup:plate")))
        {
            if (scene.InCollapsedSubtree(n)) continue;
            var flags = scene.Flags(n);
            if ((flags & NodeFlags.Focusable) == 0 || (flags & NodeFlags.Visible) == 0) continue;
            var r = harness.AbsoluteBounds(n);
            if (r.W <= 0f || r.H <= 0f) continue;
            targets++;
            Assert.True(r.W >= 40f - 0.5f && r.H >= 40f - 0.5f, $"{at}: a setup target is {r.W} x {r.H}, under 40 x 40 ({r})");
        }
        Assert.True(targets >= 2, $"{at}: the Compact sheet offers its targets (found {targets})");
    }
}
