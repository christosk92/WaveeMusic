// ── Wavee.Tests/SidebarStoreTests.cs — the layout file, the pin store, first-seen, and navigation recency ─────────
//
// Ported from the 0.2.9 suite (src/apps/_old/Wavee.Tests: SidebarLayoutStoreTests.cs, SidebarPinStoreTests.cs,
// SidebarPinSyncTests.cs, SidebarLayoutV2MigrationTests.cs, SidebarBootstrapTests.cs) against Shell/Sidebar.Host.cs's
// STORE region (search "STORE: sidebar-layout.json persistence, pins, first-seen, navigation recency"). This is the
// proof that a user's layout survives a crash, a full disk and a corrupt file — every fact below drives the REAL
// `SidebarLayoutStore` / `SidebarPinStore` / `SidebarFirstSeen` / `SidebarRecency`, never a copy of them.
//
// Every layout-store fact opens its own fresh temp directory under Path.GetTempPath() and deletes it in Dispose();
// nothing here ever reads, writes or deletes anything under Platform.LocalFolder, %LOCALAPPDATA%\Wavee or the
// registry. The one path-composition fact (DefaultPath) asserts the STRING only, against the documented
// "%LOCALAPPDATA%\Wavee\sidebar-layout.json" contract — it never calls Platform.LocalFolder (whose getter creates
// the real directory as a side effect) and never touches the file itself.
//
// Regions, in file order:
//   SidebarLayoutStoreTests              — file I/O: first run, atomic write + ONE rotated .bak, corruption
//                                          (preserve-don't-destroy), .bak recovery, DiscardCorrupt, the two LAYOUT V2
//                                          size budgets (refused WHOLE, never truncated), the named debounce and its
//                                          early flush, and I/O-failure fault classification/recovery.
//   SidebarLayoutStoreVersionLadderTests — the version ladder through the REAL store (not just the in-memory DTO):
//                                          a v1 file loads as v2 in memory and re-saves losslessly, v3+ is TooNew and
//                                          blocks writes, and a v2 document's own new members (and an even-newer
//                                          kind) round-trip through a real commit. Every other migration fact (the
//                                          document-only ones) already lives in SidebarDocTests.cs — this file only
//                                          covers what needs the real store.
//   SidebarPinStoreTests                 — the one ordered, persisted pin list: identity, order, idempotent
//                                          pin/unpin (a re-pin is a MOVE, never a duplicate), Insert/Move/Touch,
//                                          LoadFrom, ApplyRemote, the OnChanged/OnLocalPinChanged commit-point
//                                          asymmetry, and the SidebarPinId identity scheme (what is pinnable at all,
//                                          the canonical-id/raw-uri-alias migration).
//   SidebarFirstSeenTests                — the bounded first-observation map: first observation wins, cap 2000
//                                          (oldest evicted), pruned on save.
//   SidebarRecencyTests                  — navigation recency for the "recently opened" FEED only — explicitly NOT
//                                          what the Recents SORT reads.
//
// NOT ported here (see the porting agent's handoff for the full accounting):
//   - SidebarPinSyncTests.cs, in full: every fact there drives `SidebarPinSync` / `InMemoryStore` / `IPinMutations` —
//     a live store-and-network bridge that only exists under src/apps/_old in this build. The one thing in that file
//     that IS pure — the `PinSyncRules` wire-uri map — is already ported and driven directly in
//     Wavee.Tests/SidebarPinTests.cs, so it is not repeated here.
//   - SidebarBootstrapTests.cs, in full: every fact there drives `SidebarBootstrap.IsFreshInstall`/`Run` — the
//     fresh-install-witness and legacy-settings-migration subsystem, which also only exists under src/apps/_old.
//     Nothing in that file is actually about SidebarLayoutStore or SidebarPinStore.
//   - `SidebarPinStoreTests.cs`'s `Destination_CanonicalizesSearch_AndRetainsBrowseIdentity`: `SidebarDestination`
//     was not ported to 0.3.
//   - The rest of `SidebarLayoutV2MigrationTests.cs` (document-only Upgrade/ExactLegacyCuratedDefault/LegacyDefault
//     facts): already covered by SidebarDocTests.cs, which deliberately left the real-store facts for this file.
//
// These tests never start the engine loop, never open a window and never touch the network.

using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── SidebarLayoutStore: the file I/O gate ───────────────────────────────────────────────────────────────────────────

public class SidebarLayoutStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-sidebar-store-tests", Guid.NewGuid().ToString("n"));
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

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

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

    static void CommitAndWait(SidebarLayoutStore store, SidebarLayoutDocDto doc)
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

    /// <summary>An extension section whose config serializes to roughly <paramref name="payloadBytes"/> bytes.</summary>
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

    // ── first run ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void First_run_is_not_a_fault()
    {
        var load = Store().Load();
        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.None, load.Fault);
        Assert.Null(load.Detail);
        Assert.False(File.Exists(_path));   // Load must not create anything
    }

    [Fact]
    public void First_commit_creates_the_file_and_no_backup()
    {
        var store = Store();
        CommitAndWait(store, DocWith("sec_a"));

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(store.BakPath));
        Assert.False(File.Exists(store.TmpPath));
        Assert.Equal(new[] { "sec_a" }, SectionIdsOf(store.Load().Doc!));
    }

    [Fact]
    public void Commit_stamps_version_updated_at_and_app_version()
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

    // ── atomic write + exactly one rotated .bak ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Atomic_write_leaves_no_temp_file_and_creates_exactly_one_backup()
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

    // ── the named debounce, and its early flush ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommitDebounceMs_is_the_documented_300_ms_window()
        => Assert.Equal(300, SidebarLayoutStore.CommitDebounceMs);

    [Fact]
    public void Commit_coalesces_a_burst_into_the_last_snapshot()
    {
        var store = Store();
        CommitAndWait(store, DocWith("seed"));

        // A burst of editor commands (a drag, a resize, a property edit): last-wins, one write lands
        // CommitDebounceMs after the LAST one — never one write per call.
        for (int i = 0; i < 25; i++) store.Commit(DocWith("burst_" + i));
        Assert.True(store.WaitForWrites(10_000));

        Assert.Equal(new[] { "burst_24" }, SectionIdsOf(store.Load().Doc!));
        Assert.False(File.Exists(store.TmpPath));
    }

    [Fact]
    public void Commit_returns_before_the_write_is_observable()
    {
        // The snapshot-then-background-write cadence: the UI thread is never blocked on the disk (C9). The write
        // becomes observable after WaitForWrites (the test/drain seam) — never synchronously after Commit.
        var store = Store();
        store.Commit(DocWith("async"));
        Assert.True(store.WaitForWrites(10_000));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void FlushNow_fires_the_pending_write_before_the_window_elapses_so_a_shutdown_never_loses_the_last_edit()
    {
        var store = Store();
        CommitAndWait(store, DocWith("seed"));

        // Still well inside CommitDebounceMs (300 ms) — nothing would have fired on its own yet.
        store.Commit(DocWith("closing_edit"));
        store.FlushNow();
        Assert.True(store.WaitForWrites(2000));

        Assert.Equal(new[] { "closing_edit" }, SectionIdsOf(store.Load().Doc!));
    }

    // ── version gating ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_future_version_reports_too_new_retains_the_file_and_blocks_writes()
    {
        string payload = """{ "version": 99, "curated": { "templateId": "curated", "sections": [] } }""";
        File.WriteAllText(_path, payload);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.TooNew, load.Fault);
        Assert.Contains("99", load.Detail!);
        Assert.DoesNotContain(_path, load.Detail!);
        Assert.Contains("version", load.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.True(store.WritesBlocked);

        // Writes are suppressed for the rest of the process and the newer build's file is untouched.
        store.Commit(DocWith("should_not_land"));
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Missing_version_is_not_silently_accepted_as_v1()
    {
        // v1 is the FIRST schema that ever shipped, so no real file lacks a version — while "{}" and a JSON object of
        // some other schema both deserialize with version 0, and accepting them as "an empty layout" would let the
        // very next commit overwrite a real document with nothing.
        File.WriteAllText(_path, "{ }");
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.Corrupt, load.Fault);
        Assert.Contains("version", load.Detail!);
        Assert.True(store.WritesBlocked);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // ── corruption: preserve, never rewrite ─────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, byte[]> CorruptPayloads() => new()
    {
        { "truncated json", Encoding.UTF8.GetBytes("{ \"version\": 1, \"curated\": { \"sections\": [ { \"id\": \"sec_a\"") },
        { "binary garbage", new byte[] { 0x00, 0xFF, 0x13, 0x37, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00 } },
        { "array where an object was expected", Encoding.UTF8.GetBytes("[ 1, 2, 3 ]") },
        { "empty file", Array.Empty<byte>() },
    };

    [Theory]
    [MemberData(nameof(CorruptPayloads))]
    public void Corrupt_json_reports_a_fault_keeps_the_file_byte_for_byte_and_blocks_writes(string label, byte[] payload)
    {
        File.WriteAllBytes(_path, payload);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.Corrupt, load.Fault);
        Assert.NotNull(load.Detail);
        Assert.DoesNotContain(_path, load.Detail!);                  // normal UI detail is redaction-safe
        Assert.NotEmpty(load.Detail!);
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
    public void Backup_recovery_is_used_when_the_primary_is_corrupt_and_reports_no_fault()
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
        Assert.Equal("recovered from .bak", load.Detail!);
        Assert.Equal(new[] { "good_one" }, SectionIdsOf(load.Doc!));
        Assert.False(recovered.WritesBlocked);          // a good backup is a full recovery: writes stay ENABLED

        // The next commit rewrites the primary, so the recovery is self-healing.
        CommitAndWait(recovered, DocWith("healed"));
        Assert.Equal(new[] { "healed" }, SectionIdsOf(Store().Load().Doc!));
    }

    [Fact]
    public void Backup_also_corrupt_is_a_fault()
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
    public void Orphan_backup_with_no_primary_is_a_first_run()
    {
        // A .bak with no primary is indistinguishable from a leftover; a first-run default is the safe answer, and
        // the first commit then recreates the primary.
        File.WriteAllText(_path + ".bak", """{ "version": 1, "curated": { "templateId": "curated", "sections": [] } }""");

        var load = Store().Load();
        Assert.Null(load.Doc);
        Assert.Equal(SidebarLoadFault.None, load.Fault);
    }

    // ── DiscardCorrupt ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DiscardCorrupt_renames_to_dot_corrupt_drops_the_stale_backup_and_unblocks_writes()
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
    public void DiscardCorrupt_replaces_a_previous_corrupt_file()
    {
        File.WriteAllText(_path + ".corrupt", "an older casualty");
        File.WriteAllText(_path, "the newest casualty");

        var store = Store();
        store.Load();
        store.DiscardCorrupt();

        Assert.Equal("the newest casualty", File.ReadAllText(store.CorruptPath));
        Assert.Single(Directory.GetFiles(_dir, "*.corrupt"));
    }

    [Fact]
    public void DiscardCorrupt_also_clears_a_save_fault()
    {
        var store = Store();
        store.Commit(DocWithExtensionConfig(SidebarLayoutStore.MaxSectionConfigBytes + 1));
        Assert.Equal(SidebarSaveFault.ConfigTooLarge, store.SaveFault);

        store.DiscardCorrupt();
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
        Assert.Null(store.SaveFaultDetail);
    }

    // ── paths ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultPath_sits_beside_store_json_in_the_wavee_local_folder()
    {
        // Composed as a STRING only — this never calls Platform.LocalFolder (its getter creates the real directory)
        // and never reads or writes the real file. "Wavee" is Platform.Host.cs's own Publisher constant; 0.3 dropped
        // 0.2.9's extra "WaveeMusic" segment (the file now sits directly beside store.json).
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wavee", "sidebar-layout.json");
        Assert.Equal(expected, SidebarLayoutStore.DefaultPath());
    }

    [Fact]
    public void Sidecar_paths_are_derived_from_the_document_path()
    {
        var store = Store();
        Assert.Equal(_path, store.FilePath);
        Assert.Equal(_path + ".bak", store.BakPath);
        Assert.Equal(_path + ".tmp", store.TmpPath);
        Assert.Equal(_path + ".corrupt", store.CorruptPath);
    }

    [Fact]
    public void Commit_creates_missing_directories()
    {
        string nested = Path.Combine(_dir, "a", "b", "sidebar-layout.json");
        var store = new SidebarLayoutStore(nested);
        store.Commit(DocWith("deep"));
        Assert.True(store.WaitForWrites(10_000));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void CurrentVersion_is_two()
    {
        // The document's "version": 2 (LAYOUT V2) and SidebarLayoutStore.CurrentVersion must not drift apart.
        Assert.Equal(2, SidebarLayoutStore.CurrentVersion);
        Assert.Equal(2, SidebarLayoutDefaults.EmptyDocument().Version);
    }

    // ── LAYOUT V2 size budgets: refuse WHOLE, never truncate, never partially write ─────────────────────────────────

    [Fact]
    public void Budgets_are_the_platform_docs_figures_exactly()
    {
        Assert.Equal(64 * 1024, SidebarLayoutStore.MaxSectionConfigBytes);
        Assert.Equal(2 * 1024 * 1024, SidebarLayoutStore.MaxDocumentBytes);
        // The per-section cap has ONE owner: the model constant the reducer enforces.
        Assert.Equal(SidebarExtensionRef.MaxConfigBytes, SidebarLayoutStore.MaxSectionConfigBytes);
    }

    [Fact]
    public void Oversized_section_config_is_a_save_fault_and_nothing_is_written()
    {
        var store = Store();
        CommitAndWait(store, DocWith("sec_good"));                     // a healthy document exists first
        byte[] before = File.ReadAllBytes(_path);

        store.Commit(DocWithExtensionConfig(SidebarLayoutStore.MaxSectionConfigBytes + 1024));
        store.WaitForWrites(5000);

        Assert.Equal(SidebarSaveFault.ConfigTooLarge, store.SaveFault);
        Assert.True(store.SaveFaulted);
        Assert.Contains("sec_x", store.SaveFaultDetail!);
        Assert.Contains("per-section", store.SaveFaultDetail!);
        Assert.Contains((SidebarLayoutStore.MaxSectionConfigBytes).ToString(), store.SaveFaultDetail!);
        // Refused WHOLE: the previous document is byte-for-byte intact, no temp file was ever created, and the fault
        // does NOT latch the way a corrupt LOAD does — ordinary writes still work.
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.False(File.Exists(store.TmpPath));
        Assert.False(store.WritesBlocked);

        CommitAndWait(store, DocWith("sec_recovered"));
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);          // the next in-budget commit clears it
        Assert.Null(store.SaveFaultDetail);
        Assert.Equal(new[] { "sec_recovered" }, SectionIdsOf(store.Load().Doc!));
    }

    [Fact]
    public void Section_config_just_under_the_cap_is_written()
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
    public void Oversized_document_is_a_save_fault_and_nothing_is_written()
    {
        var store = Store();
        CommitAndWait(store, DocWith("sec_good"));
        byte[] before = File.ReadAllBytes(_path);

        // No single config is over cap — the DOCUMENT is. MaxSections sections x MaxItemsPerSection long-keyed items
        // blows past 2 MiB, which is also the honest note that the reducer's structural caps and the byte budget are
        // independent walls (this document is built directly, bypassing the reducer entirely).
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
        Assert.Contains("budget", store.SaveFaultDetail!);
        Assert.Contains((SidebarLayoutStore.MaxDocumentBytes).ToString(), store.SaveFaultDetail!);
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.False(File.Exists(store.TmpPath));
        Assert.False(store.WritesBlocked);

        CommitAndWait(store, DocWith("sec_small_again"));
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
    }

    // ── the completed-write signal ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Completed_write_publishes_a_healthy_measured_result()
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
    public void Budget_refusal_publishes_synchronously_and_is_redacted()
    {
        var store = Store();
        SidebarWriteResult observed = default;
        store.WriteCompleted = result => observed = result;

        store.Commit(DocWithExtensionConfig(SidebarLayoutStore.MaxSectionConfigBytes + 1));

        Assert.False(observed.Success);
        Assert.Equal(SidebarPersistenceFault.ConfigTooLarge, observed.Fault);
        Assert.Contains("per-section", observed.SafeDetail!);
        Assert.DoesNotContain(_path, observed.SafeDetail!);
    }

    [Fact]
    public void IoFailure_is_classified_reactively_and_a_healthy_retry_clears_it()
    {
        // 0.2.9 injected a fake WaveeLog through a second constructor overload to assert on the logged event; 0.3's
        // SidebarLayoutStore takes only a path (logging goes through the static, always-on Log — see MISMATCH in the
        // porting report), so this keeps the write/fault-classification/recovery contract and drops the log-content
        // assertions rather than faking the injection.
        string blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "block directory creation");
        string target = Path.Combine(blocker, "sidebar-layout.json");
        var store = new SidebarLayoutStore(target);
        var observed = new List<SidebarWriteResult>();
        store.WriteCompleted = observed.Add;

        store.Commit(DocWith("sec_fail"));
        Assert.True(store.WaitForWrites());

        var failed = Assert.Single(observed);
        Assert.False(failed.Success);
        Assert.Equal(SidebarPersistenceFault.IoFailure, failed.Fault);
        Assert.Equal(SidebarSaveFault.IoFailure, store.SaveFault);
        Assert.DoesNotContain(target, failed.SafeDetail!);

        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        store.Commit(DocWith("sec_recovered"));
        Assert.True(store.WaitForWrites());

        Assert.Equal(2, observed.Count);
        Assert.True(observed[^1].Success);
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);   // reactive, not latched — the good retry clears it
    }
}

// ── SidebarLayoutStore: the version ladder through the REAL store ──────────────────────────────────────────────────
//
// SidebarDocTests.cs pins the version ladder as pure in-memory DTO facts (Upgrade is total, idempotent, preserves
// the carry). What is left for here — deliberately — is everything that needs the actual file: a v1 file loading
// through Load() without a fault, a v1 file re-saving as v2 through a real Commit()/WaitForWrites() cycle without
// losing a single member, and the TooNew gate blocking a real write.

public sealed class SidebarLayoutStoreVersionLadderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-sidebar-v2-tests", Guid.NewGuid().ToString("n"));
    readonly string _path;

    public SidebarLayoutStoreVersionLadderTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "sidebar-layout.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    SidebarLayoutStore Store() => new(_path);

    static SidebarLayoutDocDto Parse(string json) =>
        JsonSerializer.Deserialize(json, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto)!;

    /// <summary>A realistic document as the SHIPPED v1 build wrote it: pins, the V3 overlay, a curated layout using
    /// only v1 kinds, an unknown envelope member from an even newer build, and no v2 member anywhere.</summary>
    const string V1Document = """
    {
      "version": 1,
      "updatedAtMs": 1753893041233,
      "appVersion": "1.0.0",
      "pins": [
        { "id": "liked", "kind": 0, "uri": "spotify:collection:tracks", "name": "Liked Songs", "addedAtMs": 1753013400000 },
        { "id": "folder:6a1f2c", "kind": 5, "uri": "", "name": "Cafe & chill", "addedAtMs": 1753301100000 }
      ],
      "v3": {
        "customOrder": ["pl:a", "pl:b"],
        "expandedFolders": ["6a1f2c"],
        "firstSeen": [ { "id": "pl:b", "ms": 1753578000000 } ]
      },
      "curated": {
        "templateId": "curated",
        "sections": [
          { "id": "sec_pin", "kind": "pinned", "items": [ { "id": "itm_1", "target": "entity", "key": "spotify:playlist:1", "label": "Alias" } ] },
          { "id": "sec_jump", "kind": "jumpBackIn", "display": { "maxItems": 4 } },
          { "id": "sec_list", "kind": "entityList", "query": { "kinds": ["artists"], "sort": "alphabetical", "descending": false } },
          { "id": "sec_grp", "kind": "customGroup", "gravity": "down",
            "children": [ { "id": "sec_kid", "kind": "staticLinks", "items": [ { "id": "itm_2", "target": "route", "key": "home" } ] } ] },
          { "id": "sec_div", "kind": "divider" }
        ]
      },
      "telemetryOptIn": true
    }
    """;

    // ── v1 → v2 is IDENTITY, proven through a real Load()/Commit() ─────────────────────────────────────────────────────

    [Fact]
    public void V1_document_loads_without_a_fault_and_stamps_version_two_in_memory()
    {
        File.WriteAllText(_path, V1Document);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Equal(SidebarLoadFault.None, load.Fault);
        Assert.NotNull(load.Doc);
        Assert.Equal(2, load.Doc!.Version);                 // upgraded IN MEMORY…
        Assert.Equal(before, File.ReadAllBytes(_path));      // …and the file is untouched until an ordinary commit
        Assert.False(store.WritesBlocked);
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
    }

    [Fact]
    public void V1_document_re_saves_as_v2_without_losing_anything()
    {
        File.WriteAllText(_path, V1Document);

        var store = Store();
        var doc = store.Load().Doc!;
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000));

        string saved = File.ReadAllText(_path);
        Assert.Contains("\"version\": 2", saved);              // the ONLY visible difference
        Assert.DoesNotContain("\"version\": 1", saved);
        Assert.Contains("telemetryOptIn", saved);              // the envelope carry
        // pins — the default JSON encoder unicode-escapes '&', so probe the name's words rather than the raw glyph
        Assert.True(saved.Contains("Cafe") && saved.Contains("chill"),
            "the pin name did not survive the v1->v2 re-save");
        Assert.Contains("6a1f2c", saved);                      // the V3 overlay
        Assert.Contains("sec_kid", saved);                     // nesting
        Assert.Contains("gravity", saved);                     // an unknown SECTION member, via the wire carry
        Assert.DoesNotContain("\"extension\"", saved);         // …and no v2 member is invented on the way out
        Assert.DoesNotContain("\"action\"", saved);
        Assert.DoesNotContain("includeUris", saved);

        // Reading the re-saved file yields the SAME layout, structurally — "no visual change to existing layouts".
        var reloaded = Store().Load();
        Assert.Equal(SidebarLoadFault.None, reloaded.Fault);
        Assert.Equal(2, reloaded.Doc!.Version);
        var before = SidebarLayoutWire.ReadCurated(Parse(V1Document).Curated).Layout;
        var after = SidebarLayoutWire.ReadCurated(reloaded.Doc!.Curated).Layout;
        Assert.True(SidebarLayoutCompare.Equal(before, after), SidebarLayoutCompare.FirstDifference(before, after) ?? "");
    }

    // ── the version gate above v2 ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    public void Version_above_two_is_too_new_keeps_the_file_and_blocks_writes(int version)
    {
        string payload = $$"""
        { "version": {{version}}, "curated": { "templateId": "curated", "sections": [
          { "id": "sec_future", "kind": "extension",
            "extension": { "extensionId": "acme", "contributionId": "thing", "config": { "k": 1 } } } ] } }
        """;
        File.WriteAllText(_path, payload);
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();

        Assert.Equal(SidebarLoadFault.TooNew, load.Fault);
        Assert.Null(load.Doc);
        Assert.Contains(version.ToString(), load.Detail!);
        Assert.True(store.WritesBlocked);

        store.Commit(new SidebarLayoutDocDto { Version = 2 });
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));             // a newer build owns the file
    }

    // ── a v2 document, read and re-saved through a real store ──────────────────────────────────────────────────────

    [Fact]
    public void V2_payload_read_and_re_saved_keeps_every_v2_member()
    {
        // The v2 members this build DOES understand survive a full store round trip; the opaque config inside them
        // is never inspected, so an unknown extension's settings come back byte-for-byte in meaning.
        const string v2 = """
        {
          "version": 2,
          "curated": {
            "templateId": "curated",
            "sections": [
              { "id": "sec_x", "kind": "extension",
                "extension": { "extensionId": "acme.stats", "contributionId": "listening.heatmap", "schemaVersion": 4,
                               "config": { "buckets": [1, 2, 3], "palette": { "warm": "#f00" } } } },
              { "id": "sec_g", "kind": "customGroup", "items": [
                { "id": "itm_a", "target": "action", "key": "wavee.play",
                  "action": { "providerId": "wavee", "actionId": "play", "targetMode": "fixedEntity",
                              "targetKey": "spotify:playlist:1", "arguments": { "shuffle": true } } } ] },
              { "id": "sec_e", "kind": "entityList",
                "query": { "kinds": ["artists"], "includeUris": ["spotify:artist:a"], "excludeUris": ["spotify:artist:b"] } }
            ]
          }
        }
        """;
        File.WriteAllText(_path, v2);

        var store = Store();
        var doc = store.Load().Doc!;
        Assert.Equal(2, doc.Version);

        var read = SidebarLayoutWire.ReadCurated(doc.Curated);
        var x = read.Layout.Sections[0].Extension!;
        Assert.Equal("acme.stats", x.ExtensionId);
        Assert.Equal(4, x.SchemaVersion);
        var binding = read.Layout.Sections[1].ItemList[0].Action!;
        Assert.Equal(SidebarActionTargetMode.FixedEntity, binding.TargetMode);
        Assert.Equal("spotify:playlist:1", binding.TargetKey);
        Assert.Equal(new[] { "spotify:artist:a" }, read.Layout.Sections[2].Query!.IncludeList);

        doc.Curated = SidebarLayoutWire.WriteCurated(read.Layout, read.Carry);
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000));

        string saved = File.ReadAllText(_path);
        Assert.Contains("\"kind\": \"extension\"", saved);
        Assert.Contains("listening.heatmap", saved);
        Assert.Contains("\"warm\": \"#f00\"", saved);
        Assert.Contains("\"targetMode\": \"fixedEntity\"", saved);
        Assert.Contains("\"shuffle\": true", saved);
        Assert.Contains("\"includeUris\"", saved);
        Assert.Contains("\"excludeUris\"", saved);

        // …and the second read equals the first, so a save/load cycle is a fixed point.
        var again = SidebarLayoutWire.ReadCurated(Store().Load().Doc!.Curated).Layout;
        Assert.True(SidebarLayoutCompare.Equal(read.Layout, again),
            SidebarLayoutCompare.FirstDifference(read.Layout, again) ?? "");
    }

    [Fact]
    public void V2_document_with_a_future_kind_still_round_trips_the_opaque_section()
    {
        // The unknown-KIND policy is unchanged by v2 (and now also covers a kind a v3 build introduces).
        const string v2WithFuture = """
        { "version": 2, "curated": { "templateId": "curated", "sections": [
          { "id": "sec_known", "kind": "extension",
            "extension": { "extensionId": "wavee", "contributionId": "queue" } },
          { "id": "sec_future", "kind": "hologram", "config": { "spin": 3 } } ] } }
        """;
        File.WriteAllText(_path, v2WithFuture);

        var store = Store();
        var doc = store.Load().Doc!;
        var read = SidebarLayoutWire.ReadCurated(doc.Curated);
        Assert.Single(read.Layout.Sections);
        Assert.Equal(1, read.Carry.UnknownSectionCount);

        doc.Curated = SidebarLayoutWire.WriteCurated(read.Layout, read.Carry);
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000));

        string saved = File.ReadAllText(_path);
        Assert.Contains("\"kind\": \"hologram\"", saved);
        Assert.Contains("\"spin\"", saved);
        Assert.Contains("\"contributionId\": \"queue\"", saved);
    }
}

// ── SidebarPinStore: the one ordered, persisted pin list ────────────────────────────────────────────────────────────
//
// Engine-free apart from Signal<int> (the real Shell/Sidebar.Host.cs type), so the contracts every Pinned section
// and the projection's pin band depend on are pinned against production code rather than a copy of it. Pure
// in-memory — no file, no fixture.

public class SidebarPinStoreTests
{
    static SidebarPin Pin(string id, SidebarEntryKind kind = SidebarEntryKind.Playlist,
                          string uri = "spotify:playlist:x", string name = "n", long addedAtMs = 0)
        => new(id, kind, uri, name, addedAtMs);

    static SidebarPinStore StoreOf(params string[] ids)
    {
        var s = new SidebarPinStore();
        for (int i = 0; i < ids.Length; i++) Assert.True(s.Pin(Pin(ids[i])));
        return s;
    }

    static List<string> IdsOf(SidebarPinStore s)
    {
        var ids = new List<string>(s.Count);
        for (int i = 0; i < s.Count; i++) ids.Add(s[i].Id);
        return ids;
    }

    // ── identity, order, idempotent pin/unpin ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pin_appends_at_the_end()
    {
        var s = StoreOf("a", "b", "c");
        Assert.Equal(new[] { "a", "b", "c" }, IdsOf(s));
        Assert.Equal(2, s.IndexOf("c"));
    }

    [Fact]
    public void Pinning_twice_is_deduped_and_keeps_the_original_position()
    {
        var s = StoreOf("a", "b", "c");
        int version = s.Version.Peek();

        // A second Pin of the same id is a silent no-op (the menu shows Unpin in that state) — it is a MOVE candidate
        // that never happens, so a double invoke can never reorder the list.
        Assert.False(s.Pin(Pin("a", name: "renamed")));
        Assert.Equal(new[] { "a", "b", "c" }, IdsOf(s));
        Assert.Equal(0, s.IndexOf("a"));
        Assert.Equal("n", s[0].Name);                 // the rejected pin did not overwrite the cached display name
        Assert.Equal(version, s.Version.Peek());      // a rejected mutation does not bump the version (no commit, no re-render)
    }

    [Fact]
    public void Unpin_returns_the_former_index_for_the_undo_restore()
    {
        var s = StoreOf("a", "b", "c");
        Assert.Equal(1, s.Unpin("b"));
        Assert.Equal(new[] { "a", "c" }, IdsOf(s));
        Assert.Equal(1, s.IndexOf("c"));              // the tail was reindexed
    }

    [Fact]
    public void Unpinning_a_missing_id_is_a_no_op()
    {
        var s = StoreOf("a");
        int version = s.Version.Peek();
        Assert.Equal(-1, s.Unpin("nope"));
        Assert.Equal(-1, s.Unpin(null));
        Assert.Single(s);
        Assert.Equal(version, s.Version.Peek());
    }

    [Fact]
    public void Insert_restores_at_the_former_index_and_clamps_out_of_range()
    {
        var s = StoreOf("a", "b", "c");
        int at = s.Unpin("b");
        Assert.True(s.Insert(Pin("b"), at));
        Assert.Equal(new[] { "a", "b", "c" }, IdsOf(s));

        // An undo that arrives after other pins were removed must still land somewhere sane, never throw.
        Assert.True(s.Insert(Pin("z"), 999));
        Assert.Equal("z", s[^1].Id);
        Assert.True(s.Insert(Pin("y"), -5));
        Assert.Equal("y", s[0].Id);
        Assert.False(s.Insert(Pin("a"), 0));          // already pinned → rejected, not duplicated
        Assert.Equal(5, s.Count);
    }

    [Fact]
    public void Move_reorders_within_the_list()
    {
        var s = StoreOf("a", "b", "c", "d");

        s.Move(0, 2);                                  // forward
        Assert.Equal(new[] { "b", "c", "a", "d" }, IdsOf(s));

        s.Move(3, 1);                                  // backward (from > to)
        Assert.Equal(new[] { "b", "d", "c", "a" }, IdsOf(s));

        s.Move(0, s.Count);                            // to == count -> "move to the end", clamped, never out of range
        Assert.Equal(new[] { "d", "c", "a", "b" }, IdsOf(s));

        for (int i = 0; i < s.Count; i++) Assert.Equal(i, s.IndexOf(s[i].Id));   // the index map tracked every move
    }

    [Fact]
    public void A_no_op_or_out_of_range_move_does_not_bump_the_version()
    {
        var s = StoreOf("a", "b");
        int version = s.Version.Peek();
        s.Move(1, 1);
        s.Move(7, 0);
        s.Move(-1, 0);
        Assert.Equal(new[] { "a", "b" }, IdsOf(s));
        Assert.Equal(version, s.Version.Peek());
    }

    [Fact]
    public void Pins_are_unlimited()
    {
        // Locked decision 4: unlimited, no cap, no eviction.
        var s = new SidebarPinStore();
        for (int i = 0; i < 1000; i++) Assert.True(s.Pin(Pin("p" + i)));
        Assert.Equal(1000, s.Count);
        Assert.Equal("p0", s[0].Id);
        Assert.Equal("p999", s[999].Id);
        Assert.Equal(500, s.IndexOf("p500"));
    }

    [Fact]
    public void Pin_order_is_stable_across_unrelated_mutations()
    {
        // The precondition the projection's "pins first, in pin-store order" band relies on: removing or adding an
        // unrelated pin never permutes the surviving pins' relative order.
        var s = StoreOf("a", "b", "c", "d");
        s.Unpin("c");
        Assert.True(s.Pin(Pin("e")));
        Assert.Equal(new[] { "a", "b", "d", "e" }, IdsOf(s));
        Assert.True(s.IndexOf("a") < s.IndexOf("b"));
        Assert.True(s.IndexOf("b") < s.IndexOf("d"));
    }

    [Fact]
    public void Route_and_entity_pins_coexist_in_insertion_order()
    {
        var s = new SidebarPinStore();
        Assert.True(s.Pin(Pin("liked", SidebarEntryKind.AppRoute, "spotify:collection:tracks", "Liked Songs")));
        Assert.True(s.Pin(Pin("pl:spotify:playlist:1", SidebarEntryKind.Playlist)));
        Assert.True(s.Pin(Pin("folder:6a1f2c", SidebarEntryKind.Folder, "", "Cafe & chill")));
        Assert.True(s.Pin(Pin("artist:spotify:artist:1", SidebarEntryKind.Artist, "spotify:artist:1", "Daft Punk")));

        Assert.Equal(new[] { "liked", "pl:spotify:playlist:1", "folder:6a1f2c", "artist:spotify:artist:1" }, IdsOf(s));
        Assert.Equal(SidebarEntryKind.AppRoute, s[0].Kind);
        Assert.Equal(SidebarEntryKind.Folder, s[2].Kind);
    }

    [Fact]
    public void Touch_refreshes_the_cached_name_without_bumping_the_version()
    {
        // A display-cache refresh must never commit on its own and must never invalidate a render mid-projection —
        // it reports "changed" to the caller but never bumps the version.
        var s = StoreOf("a");
        int version = s.Version.Peek();

        Assert.True(s.Touch("a", "New Name"));
        Assert.Equal("New Name", s[0].Name);
        Assert.Equal(version, s.Version.Peek());

        Assert.False(s.Touch("a", "New Name"));       // unchanged -> no-op
        Assert.False(s.Touch("a", ""));               // an empty name is never a refresh
        Assert.False(s.Touch("missing", "x"));
    }

    [Fact]
    public void LoadFrom_drops_idless_and_duplicate_rows()
    {
        // A hand-edited document must never produce two rows with one identity.
        var s = new SidebarPinStore();
        s.LoadFrom([Pin("a"), Pin(""), Pin("b"), Pin("a", name: "dupe")]);
        Assert.Equal(new[] { "a", "b" }, IdsOf(s));
        Assert.Equal("n", s[0].Name);
    }

    [Fact]
    public void OnChanged_fires_for_accepted_mutations_only()
    {
        var s = new SidebarPinStore();
        int commits = 0;
        s.OnChanged = () => commits++;

        s.Pin(Pin("a"));            // 1
        s.Pin(Pin("a"));            // rejected
        s.Insert(Pin("b"), 0);      // 2
        s.Move(0, 1);               // 3
        s.Move(1, 1);               // rejected (no-op)
        s.Unpin("zz");              // rejected
        s.Unpin("a");               // 4
        s.Touch("b", "x");          // never commits alone

        Assert.Equal(4, commits);
    }

    // ── OnLocalPinChanged + ApplyRemote (the server-membership convergence path) ────────────────────────────────────

    [Fact]
    public void OnLocalPinChanged_fires_for_pin_insert_and_unpin_with_the_right_flag_but_not_for_move_touch_or_load_from()
    {
        var s = new SidebarPinStore();
        var events = new List<(string Id, bool Pinned)>();
        s.OnLocalPinChanged = (p, pinned) => events.Add((p.Id, pinned));

        Assert.True(s.Pin(Pin("a")));
        Assert.False(s.Pin(Pin("a")));                  // rejected — no event
        Assert.True(s.Insert(Pin("b"), 0));
        int at = s.Unpin("a");
        Assert.True(s.Insert(Pin("a"), at));             // the undo re-insert also raises the event (true)

        s.Move(0, 1);                                    // no event — order is local by design
        s.Touch("a", "renamed");                         // no event — a display-cache refresh is not user intent
        s.LoadFrom([Pin("c")]);                           // no event — startup load is not user intent

        Assert.Equal(
            new[] { ("a", true), ("b", true), ("a", false), ("a", true) },
            events.Select(e => (e.Id, e.Pinned)).ToArray());
    }

    [Fact]
    public void ApplyRemote_never_raises_OnLocalPinChanged()
    {
        var s = new SidebarPinStore();
        bool fired = false;
        s.OnLocalPinChanged = (_, _) => fired = true;

        s.ApplyRemote([Pin("pl:spotify:playlist:1")], _ => true, removeMissing: false);
        Assert.False(fired);
    }

    [Fact]
    public void ApplyRemote_appends_missing_pins_in_the_given_order_and_keeps_existing_order()
    {
        var s = StoreOf("a");
        bool changed = s.ApplyRemote(
            [Pin("pl:spotify:playlist:1"), Pin("pl:spotify:playlist:2")],
            _ => true, removeMissing: false);
        Assert.True(changed);
        Assert.Equal(new[] { "a", "pl:spotify:playlist:1", "pl:spotify:playlist:2" }, IdsOf(s));
    }

    [Fact]
    public void ApplyRemote_with_remove_missing_false_removes_nothing()
    {
        var s = StoreOf("pl:spotify:playlist:1", "pl:spotify:playlist:2");
        bool changed = s.ApplyRemote([Pin("pl:spotify:playlist:1")], _ => true, removeMissing: false);
        Assert.False(changed);
        Assert.Equal(new[] { "pl:spotify:playlist:1", "pl:spotify:playlist:2" }, IdsOf(s));
    }

    [Fact]
    public void ApplyRemote_with_remove_missing_true_removes_only_syncable_ids_missing_from_the_server()
    {
        var s = new SidebarPinStore();
        Assert.True(s.Pin(Pin("pl:spotify:playlist:1")));
        Assert.True(s.Pin(Pin("pl:spotify:playlist:2")));      // missing from server, syncable → removed
        Assert.True(s.Pin(Pin("folder:x", SidebarEntryKind.Folder, "", "F")));   // missing, NOT syncable → survives
        Assert.True(s.Pin(Pin("home", SidebarEntryKind.AppRoute, "", "Home")));  // missing, NOT syncable → survives

        bool changed = s.ApplyRemote(
            [Pin("pl:spotify:playlist:1")],
            isSyncable: id => id.StartsWith("pl:", StringComparison.Ordinal),
            removeMissing: true);

        Assert.True(changed);
        Assert.Equal(new[] { "pl:spotify:playlist:1", "folder:x", "home" }, IdsOf(s));
    }

    [Fact]
    public void ApplyRemote_bumps_the_version_once_and_commits_once_per_call_and_returns_false_when_nothing_changed()
    {
        var s = StoreOf("pl:spotify:playlist:1");
        int commits = 0;
        s.OnChanged = () => commits++;
        int versionBefore = s.Version.Peek();

        Assert.True(s.ApplyRemote([Pin("pl:spotify:playlist:1"), Pin("pl:spotify:playlist:2")], _ => true, false));
        Assert.Equal(1, commits);
        Assert.Equal(versionBefore + 1, s.Version.Peek());

        // A second, no-op call (nothing new, nothing removed) neither commits nor bumps.
        Assert.False(s.ApplyRemote([Pin("pl:spotify:playlist:1"), Pin("pl:spotify:playlist:2")], _ => true, false));
        Assert.Equal(1, commits);
        Assert.Equal(versionBefore + 1, s.Version.Peek());
    }

    // ── SidebarPinId: what is pinnable at all, and the canonical identity scheme ────────────────────────────────────

    [Theory]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("spotify:episode:512ojhOuo1ktJprKbVcKyQ")]
    [InlineData("spotify:local:artist:album:track:180")]
    [InlineData("")]
    [InlineData(null)]
    public void Tracks_and_episodes_are_never_pinnable(string? uri)
        => Assert.Null(SidebarPinId.FromUri(uri));

    [Theory]
    [InlineData("spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "pl:spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", SidebarEntryKind.Playlist)]
    [InlineData("spotify:album:4aawyAB79vO75wG7WLfDzB", "album:spotify:album:4aawyAB79vO75wG7WLfDzB", SidebarEntryKind.Album)]
    [InlineData("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "artist:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", SidebarEntryKind.Artist)]
    [InlineData("spotify:show:4rOoJ6Egrf8K2IrywzwOMk", "show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk", SidebarEntryKind.Show)]
    public void Entity_uris_map_to_a_prefixed_id_and_back_to_their_kind(string uri, string expectedId, SidebarEntryKind expectedKind)
    {
        string? id = SidebarPinId.FromUri(uri);
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedKind, SidebarPinId.KindOf(id!));
    }

    [Fact]
    public void Liked_songs_is_a_route_pin_not_a_playlist_pin()
    {
        // The one special case: the Liked Songs collection uri is the "liked" ROUTE, because the pin id IS the nav
        // route key.
        string? id = SidebarPinId.FromUri("spotify:collection:tracks");
        Assert.Equal("liked", id);
        Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf(id!));
    }

    [Fact]
    public void Pinnable_routes_seed_the_picker_while_dynamic_destinations_remain_pinnable()
    {
        for (int i = 0; i < SidebarPinId.PinnableRoutes.Length; i++)
        {
            string route = SidebarPinId.PinnableRoutes[i];
            Assert.Equal(route, SidebarPinId.FromRoute(route));
            Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf(route));
        }
        // The picker is intentionally curated, but the pin model accepts any durable app destination.
        Assert.Equal("browse:spotify:page:music", SidebarPinId.FromRoute("browse:spotify:page:music"));
        Assert.True(SidebarPinId.IsPinnableRoute("browse:spotify:page:music"));
        Assert.Equal("concerts", SidebarPinId.FromRoute("concerts"));
        Assert.Equal("artist-concerts:spotify:artist:x", SidebarPinId.FromRoute("artist-concerts:spotify:artist:x"));

        // Shell-internal surfaces are never sidebar destinations.
        Assert.Null(SidebarPinId.FromRoute("settings"));
        Assert.False(SidebarPinId.IsPinnableRoute("settings"));
        Assert.Null(SidebarPinId.FromRoute("api-console"));
        Assert.Null(SidebarPinId.FromRoute("sidebar-customize"));
        Assert.Null(SidebarPinId.FromRoute("home-customize"));
        Assert.Null(SidebarPinId.FromRoute("playback-diagnostics"));
        Assert.Null(SidebarPinId.FromRoute(""));
        Assert.Null(SidebarPinId.FromRoute(null));

        // …and so is anything the route vocabulary does not RECOGNISE — FromRoute is the app's route recogniser, not
        // just a policy filter. A version that returned every non-empty string made a third-party entity uri resolve
        // to a route pin with an EMPTY entity uri, and turned any typo into a pin that painted as a fallback.
        Assert.Null(SidebarPinId.FromRoute("acme:widget:7"));
        Assert.Null(SidebarPinId.FromRoute("not-a-route"));
        Assert.Null(SidebarPinId.FromRoute("spotify:album:5"));   // an entity URI is FromUri's job, never a route
        Assert.Null(SidebarPinId.FromRoute("concert:spotify:concert:9"));   // one dated event is not a durable destination
    }

    [Fact]
    public void Recents_is_pinnable_but_is_not_in_the_built_in_top_bar()
    {
        Assert.Contains("recents", SidebarPinId.PinnableRoutes);
        Assert.Equal("recents", SidebarPinId.FromRoute("recents"));
        Assert.True(SidebarPinId.IsPinnableRoute("recents"));
        Assert.Equal(SidebarEntryKind.AppRoute, SidebarPinId.KindOf("recents"));
        Assert.Equal("recents", SidebarPinId.RouteOf("recents"));
        Assert.Equal("", SidebarPinId.UriOf("recents"));            // a route pin carries no entity uri

        foreach (var item in SidebarCustomLayout.DefaultTopBar)
            Assert.NotEqual("recents", item.Key);
    }

    [Fact]
    public void Folder_pins_by_the_rootlist_group_id_and_never_navigates()
    {
        string id = SidebarPinId.ForFolder("6a1f2c");
        Assert.Equal("folder:6a1f2c", id);
        Assert.Equal(SidebarEntryKind.Folder, SidebarPinId.KindOf(id));
        Assert.Equal("6a1f2c", SidebarPinId.FolderIdOf(id));
        Assert.Null(SidebarPinId.RouteOf(id));          // a folder expands in place — it has no route
        Assert.Equal("", SidebarPinId.UriOf(id));

        // …while every other kind's id IS its route key (which is what makes the recency join an identity lookup).
        string pl = SidebarPinId.FromUri("spotify:playlist:1")!;
        Assert.Equal(pl, SidebarPinId.RouteOf(pl));
    }

    /// <summary>Folder CRUD is live, so a pinned folder can genuinely VANISH under its pin. The store keeps it: the
    /// sidebar's standing rule is that a missing entity renders visible-but-disabled with a reason, and only an
    /// explicit unpin removes a user's row.</summary>
    [Fact]
    public void A_pin_to_a_vanished_folder_is_kept()
    {
        var s = new SidebarPinStore();
        string id = SidebarPinId.ForFolder("6a1f2c");
        Assert.True(s.Pin(Pin(id, SidebarEntryKind.Folder, "", "Late night")));

        // The folder is deleted on another device: nothing in the store is told, and nothing in the store reacts.
        Assert.True(s.IsPinned(id));
        Assert.Equal(0, s.IndexOf(id));
        Assert.Equal("Late night", s[0].Name);                       // the offline display cache still names the row

        // …and a RENAME does not disturb it either: the pin is keyed by the client-minted groupId.
        s.Touch(id, "Very late night");
        Assert.True(s.IsPinned(id));
        Assert.Equal(id, SidebarPinId.ForFolder("6a1f2c"));
    }

    [Fact]
    public void IsPinned_uses_the_stable_id_so_a_rename_cannot_unpin()
    {
        var s = new SidebarPinStore();
        string id = SidebarPinId.FromUri("spotify:playlist:37i9dQZF1DX4sWSpwq3LiO")!;
        Assert.True(s.Pin(Pin(id, SidebarEntryKind.Playlist, "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "Peaceful Piano")));

        s.Touch(id, "Peaceful Piano (2026)");
        Assert.True(s.IsPinned(id));
        Assert.Equal(0, s.IndexOf(id));
    }

    [Fact]
    public void Canonical_maps_a_bare_entity_uri_onto_the_prefixed_pin_id()
    {
        Assert.Equal("pl:spotify:playlist:x", SidebarPinId.Canonical("spotify:playlist:x"));
        Assert.Equal("pl:spotify:playlist:x", SidebarPinId.Canonical("pl:spotify:playlist:x"));
        Assert.Equal("album:spotify:album:x", SidebarPinId.Canonical("spotify:album:x"));
        Assert.Equal("artist:spotify:artist:x", SidebarPinId.Canonical("spotify:artist:x"));
        Assert.Equal("liked", SidebarPinId.Canonical("spotify:collection:tracks"));
        Assert.Null(SidebarPinId.Canonical("spotify:track:x"));
    }

    [Fact]
    public void A_raw_uri_pin_is_found_and_removed_through_its_canonical_id()
    {
        // Card/hero drops used to persist the bare uri as SidebarPin.Id. The menu looks up pl:… / album:… / artist:…
        // — without the alias those pins were immortal (Pin was a silent no-op, Unpin never appeared).
        var s = new SidebarPinStore();
        Assert.True(s.Pin(Pin("spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6")));
        Assert.Equal("pl:spotify:playlist:stuck", s[0].Id);          // Pin canonicalizes on the way in
        Assert.True(s.IsPinned("spotify:playlist:stuck"));
        Assert.True(s.IsPinned("pl:spotify:playlist:stuck"));
        Assert.Equal(0, s.Unpin("pl:spotify:playlist:stuck"));
        Assert.Empty(s);
    }

    [Fact]
    public void LoadFrom_migrates_a_legacy_raw_uri_pin_and_dedupes_the_prefixed_twin()
    {
        var s = new SidebarPinStore();
        s.LoadFrom(
        [
            Pin("spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6"),
            Pin("pl:spotify:playlist:stuck", SidebarEntryKind.Playlist, "spotify:playlist:stuck", "My Playlist #6"),
        ]);
        Assert.Equal(new[] { "pl:spotify:playlist:stuck" }, IdsOf(s));
        Assert.Equal(0, s.Unpin("spotify:playlist:stuck"));
        Assert.Empty(s);
    }
}

// ── SidebarFirstSeen: the bounded first-observation stamp map ───────────────────────────────────────────────────────
//
// The honest playlist "date added" proxy: playlists have no add timestamp anywhere, so the projection records the
// first time it ever observes a playlist id and sorts by that. Pure in-memory, with an injected clock so the cap
// test does not depend on wall-clock timing.

public class SidebarFirstSeenTests
{
    /// <summary>A fresh store over a monotonic, test-owned clock — the cap fact stamps 2001 ids and must not depend
    /// on wall-clock resolution.</summary>
    static SidebarFirstSeen Clocked()
    {
        long now = 0;
        return new SidebarFirstSeen(() => ++now);
    }

    [Fact]
    public void Stamp_records_the_first_observation_only()
    {
        var seen = Clocked();
        long first = seen.Stamp("pl:a");
        long again = seen.Stamp("pl:a");
        Assert.Equal(first, again);           // the SECOND observation never overwrites the first
        Assert.Equal(1, seen.Count);
    }

    [Fact]
    public void Peek_never_records_a_stamp()
    {
        var seen = Clocked();
        Assert.Equal(0L, seen.Peek("pl:a"));   // never observed
        Assert.Equal(0, seen.Count);
        seen.Stamp("pl:a");
        Assert.Equal(seen.Stamp("pl:a"), seen.Peek("pl:a"));
    }

    [Fact]
    public void NewStamps_counts_only_fresh_records_since_the_last_reset()
    {
        var seen = Clocked();
        seen.Stamp("pl:a");
        seen.Stamp("pl:b");
        seen.Stamp("pl:a");                    // already seen — not fresh
        Assert.Equal(2, seen.NewStamps);

        seen.ResetNewCount();
        Assert.Equal(0, seen.NewStamps);
        seen.Stamp("pl:c");
        Assert.Equal(1, seen.NewStamps);
    }

    [Fact]
    public void The_cap_is_2000_and_the_oldest_stamp_is_evicted_to_admit_a_new_one()
    {
        var seen = Clocked();
        Assert.Equal(2000, SidebarFirstSeen.Cap);

        for (int i = 0; i < SidebarFirstSeen.Cap; i++) seen.Stamp("pl:" + i);
        Assert.Equal(SidebarFirstSeen.Cap, seen.Count);
        Assert.True(seen.Peek("pl:0") > 0);     // the very first stamp is still present, right at the cap

        seen.Stamp("pl:new");                   // one MORE than the cap
        Assert.Equal(SidebarFirstSeen.Cap, seen.Count);          // never grows past the cap
        Assert.Equal(0L, seen.Peek("pl:0"));                     // the OLDEST stamp was evicted
        Assert.True(seen.Peek("pl:new") > 0);
    }

    [Fact]
    public void Load_rehydrates_from_the_persisted_document_and_the_oldest_stamp_wins_on_a_duplicate()
    {
        var seen = Clocked();
        seen.Load(
        [
            new KeyValuePair<string, long>("pl:a", 500),
            new KeyValuePair<string, long>("pl:a", 100),   // a duplicate row, older — must win
            new KeyValuePair<string, long>("pl:b", 300),
            new KeyValuePair<string, long>("pl:bad", 0),    // invalid — skipped
            new KeyValuePair<string, long>("", 200),        // invalid — skipped
        ]);

        Assert.Equal(100, seen.Peek("pl:a"));
        Assert.Equal(300, seen.Peek("pl:b"));
        Assert.Equal(2, seen.Count);
        Assert.Equal(0, seen.NewStamps);        // a rehydrate is not a fresh observation
    }

    [Fact]
    public void PruneTo_drops_stamps_for_ids_no_longer_in_the_library()
    {
        var seen = Clocked();
        seen.Stamp("pl:a");
        seen.Stamp("pl:b");
        seen.Stamp("pl:c");

        int removed = seen.PruneTo(["pl:a", "pl:c"]);   // pl:b no longer in the live set

        Assert.Equal(1, removed);
        Assert.Equal(2, seen.Count);
        Assert.Equal(0L, seen.Peek("pl:b"));
        Assert.True(seen.Peek("pl:a") > 0);
        Assert.True(seen.Peek("pl:c") > 0);
    }

    [Fact]
    public void CopyTo_appends_every_stamp_for_persistence()
    {
        var seen = Clocked();
        seen.Stamp("pl:a");
        seen.Stamp("pl:b");

        var into = new List<KeyValuePair<string, long>>();
        seen.CopyTo(into);

        Assert.Equal(2, into.Count);
        Assert.Contains(into, kv => kv.Key == "pl:a");
        Assert.Contains(into, kv => kv.Key == "pl:b");
    }

    [Fact]
    public void The_frozen_instance_behaves_as_just_seen_but_never_mutates()
    {
        long first = SidebarFirstSeen.Frozen.Stamp("pl:a");
        long second = SidebarFirstSeen.Frozen.Stamp("pl:a");
        Assert.True(first > 0);
        Assert.True(second > 0);
        Assert.Equal(0, SidebarFirstSeen.Frozen.Count);           // never records anything
        Assert.Equal(0, SidebarFirstSeen.Frozen.PruneTo(Array.Empty<string>()));
    }
}

// ── SidebarRecency: navigation recency for the "recently opened" FEED ONLY ──────────────────────────────────────────
//
// Explicitly NOT what the sidebar's Recents SORT reads (that reads the play log's recency) — a surface that reads
// this for anything but the "recently opened" shelf, or that calls it "recently played", is a defect these tests
// exist to catch by naming.

public class SidebarRecencyTests
{
    [Fact]
    public void Empty_has_no_visits_and_is_the_shared_seed_instance()
    {
        Assert.Equal(0, SidebarRecency.Empty.Count);
        Assert.Equal(0L, SidebarRecency.Empty.LastVisitedTicks("anything"));
        Assert.Same(SidebarRecency.Empty, SidebarRecency.Build(Array.Empty<SidebarVisit>()));
    }

    [Fact]
    public void Build_keeps_the_newest_visit_per_route_key()
    {
        // Oldest-first input; the LAST occurrence of a key is the newest visit and must win.
        var recency = SidebarRecency.Build(
        [
            new SidebarVisit("pl:a", 100),
            new SidebarVisit("pl:b", 150),
            new SidebarVisit("pl:a", 200),   // a re-visit — must overwrite the earlier stamp
        ]);

        Assert.Equal(200, recency.LastVisitedTicks("pl:a"));
        Assert.Equal(150, recency.LastVisitedTicks("pl:b"));
        Assert.Equal(2, recency.Count);
    }

    [Fact]
    public void An_id_never_visited_reads_as_zero_not_missing()
    {
        var recency = SidebarRecency.Build([new SidebarVisit("pl:a", 100)]);
        Assert.Equal(0L, recency.LastVisitedTicks("pl:never"));
        Assert.Equal(0L, recency.LastVisitedTicks(null));
    }

    [Fact]
    public void A_visit_with_an_empty_route_key_is_ignored()
    {
        var recency = SidebarRecency.Build(
        [
            new SidebarVisit("", 100),
            new SidebarVisit("pl:a", 200),
        ]);
        Assert.Equal(1, recency.Count);
        Assert.Equal(200, recency.LastVisitedTicks("pl:a"));
    }

    [Fact]
    public void The_generic_builder_reads_any_row_type_through_accessor_lambdas()
    {
        var rows = new[] { (Key: "pl:a", Ticks: 10L), (Key: "pl:b", Ticks: 20L), (Key: "pl:a", Ticks: 30L) };
        var recency = SidebarRecency.Build(rows, r => r.Key, r => r.Ticks);

        Assert.Equal(30, recency.LastVisitedTicks("pl:a"));
        Assert.Equal(20, recency.LastVisitedTicks("pl:b"));
    }
}
