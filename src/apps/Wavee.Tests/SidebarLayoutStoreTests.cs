using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// SidebarLayoutStore's FILE mechanics (F.3.2.3): atomic tmp → File.Replace with exactly one rotated .bak, load-time
// fault classification (None / Corrupt / TooNew / Unreadable), .bak recovery, the preserve-don't-destroy corruption
// policy (locked decision 8: keep the file byte-for-byte, suppress every write, surface the fault) and DiscardCorrupt.
//
// Every test runs against its own temp file (the FileLocalStore / HistoryStore.Init injectable-path precedent); the real
// %LOCALAPPDATA% is never touched.
public class SidebarLayoutStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-sidebar-tests", Guid.NewGuid().ToString("n"));
    readonly string _path;

    public SidebarLayoutStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "sidebar-layout.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    SidebarLayoutStore Store() => new(_path);

    static SidebarLayoutDocDto DocWith(params string[] sectionIds)
    {
        var sections = new SidebarSectionSpec[sectionIds.Length];
        for (int i = 0; i < sectionIds.Length; i++)
            sections[i] = new SidebarSectionSpec(sectionIds[i], SidebarSectionKind.Pinned);
        return new SidebarLayoutDocDto
        {
            Version = SidebarLayoutStore.CurrentVersion,
            Curated = SidebarLayoutWire.WriteCurated(new SidebarCustomLayout(SidebarTemplates.Curated, sections), null),
        };
    }

    void CommitAndWait(SidebarLayoutStore store, SidebarLayoutDocDto doc)
    {
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000), "the pool write did not finish inside 10 s");
    }

    static string[] SectionIdsOf(SidebarLayoutDocDto doc)
    {
        var sections = doc.Curated?.Sections ?? Array.Empty<SidebarSectionDto>();
        var ids = new string[sections.Length];
        for (int i = 0; i < sections.Length; i++) ids[i] = sections[i].Id ?? "";
        return ids;
    }

    // ── first run ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstRun_IsNotAFault()
    {
        var load = Store().Load();
        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.None, load.Fault);
        Assert.Null(load.Detail);
        Assert.False(File.Exists(_path));   // Load must not create anything
    }

    [Fact]
    public void FirstCommit_CreatesTheFileAndNoBackup()
    {
        var store = Store();
        CommitAndWait(store, DocWith("sec_a"));

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(store.BakPath));
        Assert.False(File.Exists(store.TmpPath));
        Assert.Equal(new[] { "sec_a" }, SectionIdsOf(store.Load().Doc!));
    }

    [Fact]
    public void Commit_StampsVersionUpdatedAtAndAppVersion()
    {
        var store = Store();
        var doc = DocWith("sec_a");
        doc.Version = 0;
        doc.UpdatedAtMs = 0;
        CommitAndWait(store, doc);

        var back = store.Load().Doc!;
        Assert.Equal(SidebarLayoutStore.CurrentVersion, back.Version);
        Assert.True(back.UpdatedAtMs > 0);
        Assert.NotNull(back.AppVersion);
    }

    // ── atomic write + one .bak ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AtomicWrite_LeavesNoTempFile_AndCreatesOneBackup()
    {
        var store = Store();

        CommitAndWait(store, DocWith("first"));
        Assert.False(File.Exists(store.TmpPath));
        Assert.False(File.Exists(store.BakPath));

        CommitAndWait(store, DocWith("second"));
        Assert.False(File.Exists(store.TmpPath));
        Assert.True(File.Exists(store.BakPath));

        // exactly ONE backup — never .bak.1 / .bak.2
        Assert.Single(Directory.GetFiles(_dir, "*.bak"));
        // …and it holds the PREVIOUS content
        var bak = JsonSerializer.Deserialize(File.ReadAllBytes(store.BakPath), SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto)!;
        Assert.Equal(new[] { "first" }, SectionIdsOf(bak));
        Assert.Equal(new[] { "second" }, SectionIdsOf(store.Load().Doc!));

        CommitAndWait(store, DocWith("third"));
        Assert.Single(Directory.GetFiles(_dir, "*.bak"));
        bak = JsonSerializer.Deserialize(File.ReadAllBytes(store.BakPath), SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto)!;
        Assert.Equal(new[] { "second" }, SectionIdsOf(bak));   // rotated forward, still exactly one
    }

    [Fact]
    public void Commit_Coalesces_ABurstIntoTheLastSnapshot()
    {
        var store = Store();
        CommitAndWait(store, DocWith("seed"));

        // A burst of editor commands: last-wins, earlier pool tasks bail on the sequence check.
        for (int i = 0; i < 25; i++) store.Commit(DocWith("burst_" + i));
        Assert.True(store.WaitForWrites(10_000));

        Assert.Equal(new[] { "burst_24" }, SectionIdsOf(store.Load().Doc!));
        Assert.False(File.Exists(store.TmpPath));
    }

    [Fact]
    public void Commit_ReturnsBeforeTheWriteIsObservable()
    {
        // Documents the snapshot-then-background-write cadence: the UI thread is never blocked on the disk. The write
        // becomes observable after WaitForWrites (the test/drain seam).
        var store = Store();
        store.Commit(DocWith("async"));
        Assert.True(store.WaitForWrites(10_000));
        Assert.True(File.Exists(_path));
    }

    // ── version gating ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownFutureVersion_ReportsTooNew_RetainsFile_AndBlocksWrites()
    {
        string payload = """{ "version": 99, "curated": { "templateId": "curated", "sections": [] } }""";
        File.WriteAllText(_path, payload);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.TooNew, load.Fault);
        Assert.Contains("99", load.Detail);
        Assert.DoesNotContain(_path, load.Detail);
        Assert.Contains("version", load.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(store.WritesBlocked);

        // Writes are suppressed for the rest of the process and the newer build's file is untouched.
        store.Commit(DocWith("should_not_land"));
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void MissingVersion_IsNotSilentlyAcceptedAsV1()
    {
        // DEVIATION from the C8.1 test name `MissingVersion_LoadsAsV1`: foundation (F.3.2.3 step 4) is canonical for the
        // corruption mechanics and treats version <= 0 as malformed. v1 is the FIRST schema that ever shipped, so no real
        // file lacks a version — while `{}` and a JSON object of some other schema BOTH deserialize with version 0, and
        // accepting them as "an empty layout" would let the very next commit overwrite a real document with nothing.
        File.WriteAllText(_path, "{ }");
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.Corrupt, load.Fault);
        Assert.Contains("version", load.Detail);
        Assert.True(store.WritesBlocked);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // ── corruption ────────────────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, byte[]> CorruptPayloads() => new()
    {
        { "truncated json", Encoding.UTF8.GetBytes("{ \"version\": 1, \"curated\": { \"sections\": [ { \"id\": \"sec_a\"") },
        { "binary garbage", new byte[] { 0x00, 0xFF, 0x13, 0x37, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00 } },
        { "array where an object was expected", Encoding.UTF8.GetBytes("[ 1, 2, 3 ]") },
        { "empty file", Array.Empty<byte>() },
    };

    [Theory]
    [MemberData(nameof(CorruptPayloads))]
    public void CorruptJson_ReportsFault_KeepsFileByteForByte_AndBlocksWrites(string label, byte[] payload)
    {
        File.WriteAllBytes(_path, payload);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.Corrupt, load.Fault);
        Assert.NotNull(load.Detail);
        Assert.DoesNotContain(_path, load.Detail);                  // normal UI detail is redaction-safe
        Assert.NotEmpty(load.Detail);
        Assert.True(store.WritesBlocked, label);

        // The unreadable file is preserved BYTE-FOR-BYTE, and the service's in-memory fallback is the Curated default.
        Assert.Equal(before, File.ReadAllBytes(_path));
        var fallback = SidebarLayoutDefaults.CuratedLayout();
        Assert.NotEmpty(fallback.Sections);

        // …and nothing this session can overwrite it.
        store.Commit(DocWith("blocked"));
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void BackupRecovery_UsedWhenPrimaryIsCorrupt_AndReportsNoFault()
    {
        var store = Store();
        CommitAndWait(store, DocWith("good_one"));
        CommitAndWait(store, DocWith("good_two"));      // rotates good_one into .bak
        Assert.True(File.Exists(store.BakPath));

        File.WriteAllText(_path, "{ \"version\": 1, \"curated\": ");   // truncate the primary

        var recovered = Store();
        var load = recovered.Load();

        Assert.NotNull(load.Doc);
        Assert.Equal(SidebarLoadFault.None, load.Fault);
        Assert.Equal("recovered from .bak", load.Detail);
        Assert.Equal(new[] { "good_one" }, SectionIdsOf(load.Doc!));
        Assert.False(recovered.WritesBlocked);          // recovery keeps writes ENABLED

        // The next commit rewrites the primary, so the recovery is self-healing.
        CommitAndWait(recovered, DocWith("healed"));
        Assert.Equal(new[] { "healed" }, SectionIdsOf(Store().Load().Doc!));
    }

    [Fact]
    public void BackupAlsoCorrupt_IsAFault()
    {
        File.WriteAllText(_path, "not json at all");
        File.WriteAllText(_path + ".bak", "also not json");

        var store = Store();
        var load = store.Load();

        Assert.Equal(SidebarLoadFault.Corrupt, load.Fault);
        Assert.Null(load.Doc);
        Assert.True(store.WritesBlocked);
    }

    [Fact]
    public void OrphanBackup_WithNoPrimary_IsAFirstRun()
    {
        // A .bak with no primary is indistinguishable from a leftover; a first-run default is the safe answer, and the
        // first commit then recreates the primary.
        File.WriteAllText(_path + ".bak", """{ "version": 1, "curated": { "templateId": "curated", "sections": [] } }""");

        var load = Store().Load();
        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.None, load.Fault);
    }

    // ── DiscardCorrupt ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DiscardCorrupt_RenamesToCorrupt_DropsStaleBackup_AndUnblocksWrites()
    {
        File.WriteAllText(_path, "{ truncated");
        File.WriteAllText(_path + ".bak", "{ also broken");

        var store = Store();
        Assert.Equal(SidebarLoadFault.Corrupt, store.Load().Fault);
        Assert.True(store.WritesBlocked);

        store.DiscardCorrupt();

        Assert.False(File.Exists(_path));
        Assert.False(File.Exists(store.BakPath));
        Assert.True(File.Exists(store.CorruptPath));
        Assert.Equal("{ truncated", File.ReadAllText(store.CorruptPath));   // the user's bytes are PRESERVED, not deleted
        Assert.False(store.WritesBlocked);

        CommitAndWait(store, DocWith("fresh_start"));
        Assert.Equal(new[] { "fresh_start" }, SectionIdsOf(store.Load().Doc!));
    }

    [Fact]
    public void DiscardCorrupt_ReplacesAPreviousCorruptFile()
    {
        File.WriteAllText(_path + ".corrupt", "an older casualty");
        File.WriteAllText(_path, "the newest casualty");

        var store = Store();
        store.Load();
        store.DiscardCorrupt();

        Assert.Equal("the newest casualty", File.ReadAllText(store.CorruptPath));
        Assert.Single(Directory.GetFiles(_dir, "*.corrupt"));
    }

    // ── paths ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultPath_SitsBesideHistoryJson()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wavee", "WaveeMusic", "sidebar-layout.json");
        Assert.Equal(expected, SidebarLayoutStore.DefaultPath());
    }

    [Fact]
    public void SidecarPaths_AreDerivedFromTheDocumentPath()
    {
        var store = Store();
        Assert.Equal(_path, store.FilePath);
        Assert.Equal(_path + ".bak", store.BakPath);
        Assert.Equal(_path + ".tmp", store.TmpPath);
        Assert.Equal(_path + ".corrupt", store.CorruptPath);
    }

    [Fact]
    public void Commit_CreatesMissingDirectories()
    {
        string nested = Path.Combine(_dir, "a", "b", "sidebar-layout.json");
        var store = new SidebarLayoutStore(nested);
        store.Commit(DocWith("deep"));
        Assert.True(store.WaitForWrites(10_000));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void CurrentVersion_IsTwo()
    {
        // The document's "version": 2 (LAYOUT V2) and SidebarLayoutStore.CurrentVersion must not drift apart.
        Assert.Equal(2, SidebarLayoutStore.CurrentVersion);
        Assert.Equal(2, SidebarLayoutDefaults.EmptyDocument().Version);
    }

    // ── LAYOUT V2 size budgets: refuse WHOLE, never truncate, never partially write ────────────────────────────────────

    [Fact]
    public void Budgets_AreThePlatformDocsFigures()
    {
        Assert.Equal(64 * 1024, SidebarLayoutStore.MaxSectionConfigBytes);
        Assert.Equal(2 * 1024 * 1024, SidebarLayoutStore.MaxDocumentBytes);
        // The per-section cap has ONE owner: the model constant the reducer enforces.
        Assert.Equal(SidebarExtensionRef.MaxConfigBytes, SidebarLayoutStore.MaxSectionConfigBytes);
    }

    /// <summary>An extension section whose config serializes to <paramref name="payloadBytes"/>-ish bytes.</summary>
    static SidebarLayoutDocDto DocWithExtensionConfig(int payloadBytes)
    {
        var config = SidebarJson.Detach("{\"blob\":\"" + new string('x', payloadBytes) + "\"}");
        var layout = new SidebarCustomLayout(SidebarTemplates.Curated,
        [
            new SidebarSectionSpec("sec_x", SidebarSectionKind.Extension)
            {
                Extension = new SidebarExtensionRef("wavee", "artist.topTracks", 1, config),
            },
        ]);
        return new SidebarLayoutDocDto
        {
            Version = SidebarLayoutStore.CurrentVersion,
            Curated = SidebarLayoutWire.WriteCurated(layout, null),
        };
    }

    [Fact]
    public void OversizedSectionConfig_IsASaveFault_AndNothingIsWritten()
    {
        var store = Store();
        CommitAndWait(store, DocWith("sec_good"));                     // a healthy document exists first
        byte[] before = File.ReadAllBytes(_path);

        store.Commit(DocWithExtensionConfig(SidebarLayoutStore.MaxSectionConfigBytes + 1024));
        store.WaitForWrites(5000);

        Assert.Equal(SidebarSaveFault.ConfigTooLarge, store.SaveFault);
        Assert.True(store.SaveFaulted);
        Assert.Contains("sec_x", store.SaveFaultDetail);
        Assert.Contains("per-section", store.SaveFaultDetail);
        // Refused WHOLE: the previous document is byte-for-byte intact, no temp file was ever created, and the fault does
        // NOT latch the way a corrupt LOAD does — ordinary writes still work.
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.False(File.Exists(store.TmpPath));
        Assert.False(store.WritesBlocked);

        CommitAndWait(store, DocWith("sec_recovered"));
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);          // the next in-budget commit clears it
        Assert.Null(store.SaveFaultDetail);
        Assert.Equal(new[] { "sec_recovered" }, SectionIdsOf(store.Load().Doc!));
    }

    [Fact]
    public void SectionConfig_JustUnderTheCap_IsWritten()
    {
        var store = Store();
        // 60 KiB of payload plus the {"blob":"…"} wrapper is comfortably inside the 64 KiB budget.
        CommitAndWait(store, DocWithExtensionConfig(60 * 1024));

        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
        var back = SidebarLayoutWire.ReadCurated(store.Load().Doc!.Curated).Layout;
        Assert.Equal("wavee", back.Sections[0].Extension!.ExtensionId);
        Assert.True(back.Sections[0].Extension!.ConfigByteCount > 60 * 1024);
    }

    [Fact]
    public void OversizedDocument_IsASaveFault_AndNothingIsWritten()
    {
        var store = Store();
        CommitAndWait(store, DocWith("sec_good"));
        byte[] before = File.ReadAllBytes(_path);

        // No single config is over cap — the DOCUMENT is. 40 sections × 500 long-keyed items blows past 2 MiB, which is
        // also the honest note that the reducer's structural caps and the byte budget are independent walls.
        var sections = new List<SidebarSectionSpec>(SidebarLayoutReducer.MaxSections);
        for (int s = 0; s < SidebarLayoutReducer.MaxSections; s++)
        {
            var items = new SidebarItemSpec[SidebarLayoutReducer.MaxItemsPerSection];
            for (int i = 0; i < items.Length; i++)
                items[i] = new SidebarItemSpec($"itm_{s:x2}{i:x4}", SidebarItemTarget.Entity,
                    $"spotify:playlist:{s}_{i}_{new string('p', 40)}");
            sections.Add(new SidebarSectionSpec($"sec_{s:x8}", SidebarSectionKind.CustomGroup) { Items = items });
        }
        var huge = new SidebarLayoutDocDto
        {
            Version = SidebarLayoutStore.CurrentVersion,
            Curated = SidebarLayoutWire.WriteCurated(new SidebarCustomLayout(SidebarTemplates.Curated, sections), null),
        };

        store.Commit(huge);
        Assert.True(store.WaitForWrites(20_000));

        Assert.Equal(SidebarSaveFault.DocumentTooLarge, store.SaveFault);
        Assert.Contains("budget", store.SaveFaultDetail);
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.False(File.Exists(store.TmpPath));
        Assert.False(store.WritesBlocked);

        CommitAndWait(store, DocWith("sec_small_again"));
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
    }

    [Fact]
    public void DiscardCorrupt_AlsoClearsASaveFault()
    {
        var store = Store();
        store.Commit(DocWithExtensionConfig(SidebarLayoutStore.MaxSectionConfigBytes + 1));
        Assert.Equal(SidebarSaveFault.ConfigTooLarge, store.SaveFault);

        store.DiscardCorrupt();
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
        Assert.Null(store.SaveFaultDetail);
    }

    [Fact]
    public void CompletedWrite_PublishesAHealthyMeasuredResult()
    {
        var store = Store();
        SidebarWriteResult observed = default;
        store.WriteCompleted = result => observed = result;

        CommitAndWait(store, DocWith("sec_health"));

        Assert.True(observed.Success);
        Assert.Equal(SidebarPersistenceFault.None, observed.Fault);
        Assert.True(observed.Bytes > 0);
        Assert.True(observed.ElapsedMs >= 0);
        Assert.Equal(observed, store.LastWriteResult);
    }

    [Fact]
    public void BudgetRefusal_PublishesSynchronously()
    {
        var store = Store();
        SidebarWriteResult observed = default;
        store.WriteCompleted = result => observed = result;

        store.Commit(DocWithExtensionConfig(SidebarLayoutStore.MaxSectionConfigBytes + 1));

        Assert.False(observed.Success);
        Assert.Equal(SidebarPersistenceFault.ConfigTooLarge, observed.Fault);
        Assert.Contains("per-section", observed.SafeDetail);
        Assert.DoesNotContain(_path, observed.SafeDetail);
    }

    [Fact]
    public void IoFailure_IsReactive_Redacted_AndAHealthyRetryReportsRecovery()
    {
        string blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "block directory creation");
        string target = Path.Combine(blocker, "sidebar-layout.json");
        var log = new WaveeLog();
        var store = new SidebarLayoutStore(target, log);
        var observed = new List<SidebarWriteResult>();
        store.WriteCompleted = observed.Add;

        store.Commit(DocWith("sec_fail"));
        Assert.True(store.WaitForWrites());

        var failed = Assert.Single(observed);
        Assert.False(failed.Success);
        Assert.Equal(SidebarPersistenceFault.IoFailure, failed.Fault);
        Assert.Equal(SidebarSaveFault.IoFailure, store.SaveFault);
        Assert.DoesNotContain(target, failed.SafeDetail);
        var failureEntry = Assert.Single(log.Snapshot(), e => e.EventId == "sidebar.layout.save_failed");
        Assert.Equal("sidebar", failureEntry.Category);
        Assert.DoesNotContain(target, failureEntry.Format());

        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        store.Commit(DocWith("sec_recovered"));
        Assert.True(store.WaitForWrites());

        Assert.Equal(2, observed.Count);
        Assert.True(observed[^1].Success);
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
        Assert.Single(log.Snapshot(), e => e.EventId == "sidebar.layout.save_recovered");
    }
}
