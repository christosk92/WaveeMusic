using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

// SessionSnapshotStore: versioned session.json beside history.json. Covers DTO round-trip (incl. the playback schema),
// the 50-entry stack cap, corrupt-file fail-soft (no overwrite until the first successful save), and TryApplyNav order.
//
// Every test points the store at its own temp file; the real %LOCALAPPDATA% is never touched.
public class SessionSnapshotTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-session-tests", Guid.NewGuid().ToString("n"));
    readonly string _path;

    public SessionSnapshotTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "session.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    SessionSnapshotStore Store()
    {
        var s = new SessionSnapshotStore();
        s.Init(_path);
        return s;
    }

    static Route R(string name, string? arg = null) => new(name, arg);

    static List<Route> Stack(int count, string prefix)
    {
        var list = new List<Route>(count);
        for (int i = 0; i < count; i++) list.Add(R(prefix + i));
        return list;
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(Store().Load());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void RoundTrip_NavAndPlayback_SurviveSerializeDeserialize()
    {
        var store = Store();
        var back = new List<Route> { R("home"), R("search", "daft"), R("album:spotify:album:a") };
        var fwd = new List<Route> { R("liked") };
        store.UpdateNav(R("pl:spotify:playlist:p", "Focus"), back, fwd, tabId: 7);
        store.UpdatePlayback(new SessionPlaybackDto
        {
            ContextUri = "spotify:album:a",
            ContextKind = "album",
            TrackUri = "spotify:track:t",
            TrackUid = "uid-1",
            TrackIndex = 3,
            PositionMs = 123456,
            Paused = true,
            Shuffle = true,
            RepeatMode = "context",
            UserQueueUris = ["spotify:track:q1", "spotify:track:q2"],
            AutoplayActive = true,
            CapturedAtUnixMs = 1_700_000_000_000,
        });
        store.UpdateShell(railOpen: true, railMode: 4);
        store.SaveAndWait();

        var loaded = Store().Load();
        Assert.NotNull(loaded);
        Assert.Equal(SessionSnapshotStore.CurrentVersion, loaded!.Version);
        Assert.Equal(1, loaded.Version);

        var nav = loaded.Nav;
        Assert.NotNull(nav);
        Assert.Equal("pl:spotify:playlist:p", nav!.Active?.Name);
        Assert.Equal("Focus", nav.Active?.Arg);
        Assert.Equal(7, nav.ActiveTabId);
        Assert.Equal(3, nav.Back!.Length);
        Assert.Equal("home", nav.Back[0].Name);
        Assert.Equal("search", nav.Back[1].Name);
        Assert.Equal("daft", nav.Back[1].Arg);
        Assert.Equal("album:spotify:album:a", nav.Back[2].Name);
        Assert.Single(nav.Forward!);
        Assert.Equal("liked", nav.Forward![0].Name);

        var pb = loaded.Playback;
        Assert.NotNull(pb);
        Assert.Equal("spotify:album:a", pb!.ContextUri);
        Assert.Equal("album", pb.ContextKind);
        Assert.Equal("spotify:track:t", pb.TrackUri);
        Assert.Equal("uid-1", pb.TrackUid);
        Assert.Equal(3, pb.TrackIndex);
        Assert.Equal(123456, pb.PositionMs);
        Assert.True(pb.Paused);
        Assert.True(pb.Shuffle);
        Assert.Equal("context", pb.RepeatMode);
        Assert.NotNull(pb.UserQueueUris);
        Assert.Equal(["spotify:track:q1", "spotify:track:q2"], pb.UserQueueUris);
        Assert.True(pb.AutoplayActive);
        Assert.Equal(1_700_000_000_000, pb.CapturedAtUnixMs);

        var shell = loaded.Shell;
        Assert.NotNull(shell);
        Assert.True(shell!.RailOpen);
        Assert.Equal(4, shell.RailMode);
    }

    [Fact]
    public void UpdateNav_CapsBackStackAt50_DroppingTheOldest()
    {
        var store = Store();
        var back = Stack(51, "b");
        store.UpdateNav(R("home"), back, Array.Empty<Route>(), tabId: -1);
        store.SaveAndWait();

        var nav = Store().Load()!.Nav!;
        Assert.Equal(SessionSnapshotStore.MaxStack, nav.Back!.Length);
        Assert.Equal(50, SessionSnapshotStore.MaxStack);
        Assert.Equal("b1", nav.Back[0].Name);     // 51 entries, oldest b0 dropped
        Assert.Equal("b50", nav.Back[^1].Name);
    }

    [Fact]
    public void UpdateNav_CapsForwardStackAt50_DroppingTheOldest()
    {
        var store = Store();
        store.UpdateNav(R("home"), Array.Empty<Route>(), Stack(51, "f"), tabId: -1);
        store.SaveAndWait();

        var nav = Store().Load()!.Nav!;
        Assert.Equal(50, nav.Forward!.Length);
        Assert.Equal("f1", nav.Forward[0].Name);
        Assert.Equal("f50", nav.Forward[^1].Name);
    }

    [Fact]
    public void CorruptFile_LoadReturnsNull_AndDoesNotOverwriteUntilSave()
    {
        File.WriteAllText(_path, "{ this is not a session document");
        byte[] original = File.ReadAllBytes(_path);

        var store = Store();
        Assert.Null(store.Load());
        Assert.True(File.Exists(_path));
        Assert.Equal(original, File.ReadAllBytes(_path));   // Load must not move, delete, or rewrite

        store.UpdateNav(R("home"), Array.Empty<Route>(), Array.Empty<Route>(), tabId: -1);
        store.SaveAndWait();

        Assert.True(File.Exists(_path));
        Assert.NotEqual(original, File.ReadAllBytes(_path));
        var loaded = Store().Load();
        Assert.NotNull(loaded);
        Assert.Equal("home", loaded!.Nav!.Active?.Name);
    }

    [Fact]
    public void BinaryGarbageFile_LoadReturnsNull()
    {
        File.WriteAllBytes(_path, [0x00, 0xFF, 0x13, 0x37]);
        Assert.Null(Store().Load());
        Assert.True(File.Exists(_path));   // preserved until a successful save
    }

    [Fact]
    public void TooNewVersion_ReturnsNull_AndBlocksWrites()
    {
        File.WriteAllText(_path, """{"version":99,"nav":{"active":{"name":"search"}}}""");
        byte[] original = File.ReadAllBytes(_path);

        var store = Store();
        Assert.Null(store.Load());
        Assert.True(store.WritesBlocked);
        Assert.Equal(original, File.ReadAllBytes(_path));

        store.UpdateNav(R("home"), Array.Empty<Route>(), Array.Empty<Route>(), tabId: -1);
        store.SaveAndWait();
        Assert.Equal(original, File.ReadAllBytes(_path));   // a newer build owns this file
    }

    [Fact]
    public void Flush_WithoutAnUpdate_DoesNotCreateAFile()
    {
        var store = Store();
        store.Flush();
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void TryApplyNav_RestoresOldestFirstAndActiveRoute()
    {
        var nav = new SessionNavDto
        {
            Active = new SessionRouteDto("album:spotify:album:z", "Random Access Memories"),
            Back = [new("home", null), new("search", "daft"), new("artists", null)],
            Forward = [new("liked", null)],
            ActiveTabId = 4,
        };
        var back = new List<SessionRouteDto>();
        var fwd = new List<SessionRouteDto>();

        Assert.True(SessionSnapshotStore.TryApplyNav(nav, back, fwd, out var active, out int tabId));
        Assert.Equal("album:spotify:album:z", active.Name);
        Assert.Equal("Random Access Memories", active.Arg);
        Assert.Equal(4, tabId);
        Assert.Equal(3, back.Count);
        Assert.Equal("home", back[0].Name);
        Assert.Equal("search", back[1].Name);
        Assert.Equal("artists", back[^1].Name);   // Back() pops this last — newest is at the tail
        Assert.Equal("liked", Assert.Single(fwd).Name);
    }

    [Fact]
    public void TryApplyNav_CapsAnOverlongPersistedStack()
    {
        var tooMany = new SessionRouteDto[51];
        for (int i = 0; i < 51; i++) tooMany[i] = new SessionRouteDto("r" + i, null);
        var nav = new SessionNavDto
        {
            Active = new SessionRouteDto("home", null),
            Back = tooMany,
        };
        var back = new List<SessionRouteDto>();
        var fwd = new List<SessionRouteDto>();

        Assert.True(SessionSnapshotStore.TryApplyNav(nav, back, fwd, out _, out _));
        Assert.Equal(50, back.Count);
        Assert.Equal("r1", back[0].Name);
        Assert.Equal("r50", back[^1].Name);
    }

    [Fact]
    public void TryApplyNav_EmptyActive_ReturnsFalseAndLeavesStacks()
    {
        var back = new List<SessionRouteDto> { new("keep", null) };
        var fwd = new List<SessionRouteDto> { new("also", null) };
        Assert.False(SessionSnapshotStore.TryApplyNav(null, back, fwd, out var active, out int tabId));
        Assert.Equal("home", active.Name);
        Assert.Equal(-1, tabId);
        Assert.Equal("keep", Assert.Single(back).Name);   // caller keeps the pinned-workspace default
        Assert.Equal("also", Assert.Single(fwd).Name);
    }

    [Fact]
    public void DefaultPath_SitsBesideHistoryJson()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wavee", "WaveeMusic", "session.json");
        Assert.Equal(expected, SessionSnapshotStore.DefaultPath());
    }

    [Fact]
    public void UpdateNav_DoesNotDropAPreviouslyLoadedPlaybackSection()
    {
        var store = Store();
        store.UpdateNav(R("home"), Array.Empty<Route>(), Array.Empty<Route>(), -1);
        store.UpdatePlayback(new SessionPlaybackDto { TrackUri = "spotify:track:keep", PositionMs = 9 });
        store.UpdateShell(railOpen: true, railMode: 4);
        store.SaveAndWait();

        var reopened = Store();
        Assert.NotNull(reopened.Load());
        reopened.UpdateNav(R("search"), Array.Empty<Route>(), Array.Empty<Route>(), -1);
        reopened.SaveAndWait();

        var loaded = Store().Load();
        Assert.Equal("search", loaded!.Nav!.Active?.Name);
        Assert.Equal("spotify:track:keep", loaded.Playback!.TrackUri);
        Assert.Equal(9, loaded.Playback.PositionMs);
        Assert.True(loaded.Shell!.RailOpen);
        Assert.Equal(4, loaded.Shell.RailMode);
    }

    [Fact]
    public void OldV1Snapshot_WithoutShellSection_LoadsWithDefaultChrome()
    {
        File.WriteAllText(_path, """{"version":1,"nav":{"active":{"name":"home"}}}""");

        var store = Store();
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Null(loaded!.Shell);
        Assert.Null(store.ShellSection);
    }

    [Fact]
    public void RoundTrip_OriginFields_Survive()
    {
        var origins = new NavOriginStore();
        origins.Write("browse:spotify:page:x", "Pop", new NavOrigin("pop", "search", "pop"));
        var store = Store();
        store.UpdateNav(R("browse:spotify:page:x", "Pop"), Array.Empty<Route>(), Array.Empty<Route>(), 1, origins);
        store.SaveAndWait();

        var loaded = Store().Load();
        var active = loaded!.Nav!.Active;
        Assert.Equal("pop", active?.OriginLabel);
        Assert.Equal("search", active?.OriginName);
        Assert.Equal("pop", active?.OriginArg);
    }

    [Fact]
    public void OldSnapshot_WithoutOriginFields_FailsSoft()
    {
        var nav = new SessionNavDto
        {
            Active = new SessionRouteDto("browse:spotify:page:x", "Pop"),
            Back = [new("home", null)],
            Forward = [],
            ActiveTabId = 0,
        };
        var back = new List<SessionRouteDto>();
        var fwd = new List<SessionRouteDto>();
        Assert.True(SessionSnapshotStore.TryApplyNav(nav, back, fwd, out var active, out _));
        Assert.Null(active.OriginLabel);
        Assert.Null(active.OriginName);
        Assert.Null(back[0].OriginLabel);
    }
}
