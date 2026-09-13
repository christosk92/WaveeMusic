using System;
using System.IO;
using Wavee.Backend.Persistence;
using Xunit;

namespace Wavee.Tests;

// SidebarBootstrap (F.3.3 + F.4.2/F.4.3): the fresh-install truth table with each witness in isolation, the legacy
// v0→v1 pane-key migration, the "existing install never sees the chooser" invariant, and idempotence (the whole thing
// runs exactly once per install, guarded by sidebar.bootstrap.version).
//
// IsFreshInstall is driven with a TEMP data root: Environment.SpecialFolder.LocalApplicationData is not injectable on
// Windows, so the probe takes an explicit override (production passes null). No test ever touches the real %LOCALAPPDATA%.
public class SidebarBootstrapTests : IDisposable
{
    readonly string _local = Path.Combine(Path.GetTempPath(), "wavee-sidebar-tests", Guid.NewGuid().ToString("n"));

    public SidebarBootstrapTests() => Directory.CreateDirectory(_local);

    public void Dispose()
    {
        try { Directory.Delete(_local, recursive: true); } catch (Exception) { }
    }

    // ── witnesses ─────────────────────────────────────────────────────────────────────────────────────────────────────

    string WaveeDir()
    {
        string d = Path.Combine(_local, "Wavee");
        Directory.CreateDirectory(d);
        return d;
    }

    void WriteLibraryDb() => File.WriteAllText(Path.Combine(WaveeDir(), "library.db"), "sqlite");

    void WriteHistoryJson()
    {
        string d = Path.Combine(WaveeDir(), "WaveeMusic");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "history.json"), "[]");
    }

    /// <summary>A real store.json carrying a real credential, written through the production credential store — so the
    /// probe and the writer cannot drift on the key or the blob format.</summary>
    void WriteStoredCredential()
    {
        string storePath = Path.Combine(WaveeDir(), "store.json");
        var creds = new LocalCredentialStore(new FileLocalStore(storePath), new NoOpProtector());
        creds.Save(new Wavee.Backend.Spotify.Credential(
            Wavee.Backend.Spotify.CredentialKind.ReusableBlob, "someone", "c2VjcmV0"));
        Assert.True(File.Exists(storePath));
    }

    // ── the truth table (F.4.3) ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsFresh_WhenNoWitnessExists()
    {
        Assert.True(SidebarBootstrap.IsFreshInstall(new MemoryAppSettings(), _local));
    }

    [Fact]
    public void IsFresh_WhenTheDataRootDoesNotEvenExist()
    {
        string missing = Path.Combine(_local, "nope", "nowhere");
        Assert.True(SidebarBootstrap.IsFreshInstall(new MemoryAppSettings(), missing));
    }

    [Fact]
    public void NotFresh_WhenLibraryDbExists()
    {
        WriteLibraryDb();
        Assert.False(SidebarBootstrap.IsFreshInstall(new MemoryAppSettings(), _local));
    }

    [Fact]
    public void NotFresh_WhenCredentialsAreStored()
    {
        WriteStoredCredential();
        Assert.False(SidebarBootstrap.IsFreshInstall(new MemoryAppSettings(), _local));
    }

    [Fact]
    public void NotFresh_WhenHistoryJsonExists()
    {
        WriteHistoryJson();
        Assert.False(SidebarBootstrap.IsFreshInstall(new MemoryAppSettings(), _local));
    }

    [Fact]
    public void NotFresh_WhenLegacyWidthWasUserSet()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarWidthUserSet, true);
        Assert.False(SidebarBootstrap.IsFreshInstall(settings, _local));
    }

    [Fact]
    public void NotFresh_WhenLegacyCollapsedWasSet()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarCollapsed, true);
        Assert.False(SidebarBootstrap.IsFreshInstall(settings, _local));
    }

    [Fact]
    public void AnEmptyStoreJson_IsNotACredentialWitness()
    {
        // A store.json that exists but carries no credential (e.g. only a UI preference) must NOT make a first launch
        // look like an existing install.
        new FileLocalStore(Path.Combine(WaveeDir(), "store.json")).Set("something.else", "x");
        Assert.True(SidebarBootstrap.IsFreshInstall(new MemoryAppSettings(), _local));
    }

    [Fact]
    public void CorruptStoreJson_IsNotACredentialWitness()
    {
        File.WriteAllText(Path.Combine(WaveeDir(), "store.json"), "{ not json");
        Assert.True(SidebarBootstrap.IsFreshInstall(new MemoryAppSettings(), _local));
    }

    [Fact]
    public void IsFreshInstall_CreatesNothing()
    {
        string probe = Path.Combine(_local, "untouched");
        Assert.True(SidebarBootstrap.IsFreshInstall(new MemoryAppSettings(), probe));
        Assert.False(Directory.Exists(probe));   // no Directory.CreateDirectory side effect (never FileLocalStore.ForApp)
    }

    // ── Run: the fresh-install branch ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_FreshInstall_DefaultsToClassic_AndArmsTheChooser()
    {
        var settings = new MemoryAppSettings();
        SidebarBootstrap.Run(settings, _local);

        Assert.Equal((int)SidebarDesign.Classic, settings.Get(WaveeSettings.SidebarDesign));
        Assert.False(settings.Get(WaveeSettings.SidebarOnboardingSeen));   // the chooser will show ONCE
        Assert.Equal(SidebarBootstrap.TargetVersion, settings.Get(WaveeSettings.SidebarBootstrapVersion));
    }

    // ── Run: the existing-install branch ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_ExistingInstall_StaysClassic_AndNeverShowsTheChooser()
    {
        WriteHistoryJson();
        var settings = new MemoryAppSettings();

        SidebarBootstrap.Run(settings, _local);

        Assert.True(settings.Get(WaveeSettings.SidebarOnboardingSeen));
        Assert.Equal((int)SidebarDesign.Classic, settings.Get(WaveeSettings.SidebarDesign));
        // The design key is deliberately NOT WRITTEN: default 0 = Classic is load-bearing, and writing it would stomp a
        // user who already picked a design in a newer build before this migration ran.
        Assert.False(settings.WasWritten(WaveeSettings.SidebarDesign));
    }

    [Fact]
    public void Run_ExistingInstall_DoesNotStompAnAlreadyChosenDesign()
    {
        WriteLibraryDb();
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarDesign, (int)SidebarDesign.LibraryV3);

        SidebarBootstrap.Run(settings, _local);

        Assert.Equal((int)SidebarDesign.LibraryV3, settings.Get(WaveeSettings.SidebarDesign));
        Assert.True(settings.Get(WaveeSettings.SidebarOnboardingSeen));
    }

    // ── the v0 → v1 legacy pane-key migration (F.3.3) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_FoldsLegacyPaneKeysIntoClassicsBag()
    {
        WriteHistoryJson();                                   // an existing install — the case the migration exists for
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarWidth, 372f);
        settings.Set(WaveeSettings.SidebarCollapsed, true);
        settings.Set(WaveeSettings.SidebarWidthUserSet, true);

        SidebarBootstrap.Run(settings, _local);

        Assert.Equal(372f, settings.Get(SidebarKeys.Width(SidebarDesign.Classic)));
        Assert.True(settings.Get(SidebarKeys.Collapsed(SidebarDesign.Classic)));
        Assert.True(settings.Get(SidebarKeys.WidthUserSet(SidebarDesign.Classic)));

        // The legacy keys are LEFT IN PLACE (a downgrade must still find a sane width).
        Assert.Equal(372f, settings.Get(WaveeSettings.SidebarWidth));
        Assert.True(settings.Get(WaveeSettings.SidebarCollapsed));
    }

    [Fact]
    public void Run_DoesNotSeedTheV3OrCuratedBags()
    {
        WriteHistoryJson();
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarWidth, 372f);

        SidebarBootstrap.Run(settings, _local);

        // V3/Curated keep their OWN key defaults as their first-run values — the Classic width must not leak into them.
        Assert.False(settings.WasWritten(SidebarKeys.Width(SidebarDesign.LibraryV3)));
        Assert.False(settings.WasWritten(SidebarKeys.Width(SidebarDesign.Curated)));
        Assert.Equal(SidebarDesignInfo.Tiers(SidebarDesign.LibraryV3).Narrow, settings.Get(SidebarKeys.Width(SidebarDesign.LibraryV3)));
        Assert.Equal(SidebarDesignInfo.Tiers(SidebarDesign.Curated).Narrow, settings.Get(SidebarKeys.Width(SidebarDesign.Curated)));
    }

    // ── idempotence ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_IsIdempotent_AndNeverRunsTwice()
    {
        WriteHistoryJson();
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarWidth, 372f);

        SidebarBootstrap.Run(settings, _local);
        int writtenAfterFirst = settings.WrittenCount;

        // Simulate a later launch on which the user has since changed things: a second Run must touch NOTHING.
        settings.Set(SidebarKeys.Width(SidebarDesign.Classic), 300f);
        settings.Set(WaveeSettings.SidebarDesign, (int)SidebarDesign.Curated);
        int before = settings.WrittenCount;

        SidebarBootstrap.Run(settings, _local);

        Assert.Equal(before, settings.WrittenCount);
        Assert.Equal(300f, settings.Get(SidebarKeys.Width(SidebarDesign.Classic)));       // not re-migrated
        Assert.Equal((int)SidebarDesign.Curated, settings.Get(WaveeSettings.SidebarDesign));
        Assert.True(writtenAfterFirst > 0);
    }

    [Fact]
    public void Run_OnAFreshInstall_ThenAgain_LeavesTheChooserMarkerAlone()
    {
        var settings = new MemoryAppSettings();
        SidebarBootstrap.Run(settings, _local);                 // fresh → marker false, chooser armed
        settings.Set(WaveeSettings.SidebarOnboardingSeen, true);   // the chooser was answered

        SidebarBootstrap.Run(settings, _local);                 // a later launch

        Assert.True(settings.Get(WaveeSettings.SidebarOnboardingSeen));   // never re-armed
    }

    [Fact]
    public void Run_HonorsAFutureBootstrapVersion()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.SidebarBootstrapVersion, SidebarBootstrap.TargetVersion + 5);
        int before = settings.WrittenCount;

        SidebarBootstrap.Run(settings, _local);

        Assert.Equal(before, settings.WrittenCount);
    }

    [Fact]
    public void ChooserGate_IsExactlyOneBooleanRead()
    {
        // F.4.3: the gate is `!settings.Get(SidebarOnboardingSeen)` — no cross-referencing of design/bootstrap version.
        var fresh = new MemoryAppSettings();
        SidebarBootstrap.Run(fresh, _local);
        Assert.True(!fresh.Get(WaveeSettings.SidebarOnboardingSeen));

        WriteHistoryJson();
        var existing = new MemoryAppSettings();
        SidebarBootstrap.Run(existing, _local);
        Assert.False(!existing.Get(WaveeSettings.SidebarOnboardingSeen));
    }
}
