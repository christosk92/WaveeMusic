using System;
using System.IO;
using Xunit;

namespace Wavee.Tests;

/// <summary>Factory-reset wipe against TEMP trees only — never the real <c>%LOCALAPPDATA%\Wavee</c>.</summary>
public sealed class FactoryResetTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "wavee-factory-reset-tests", Guid.NewGuid().ToString("n"));

    public FactoryResetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void WipeDirectories_RemovesNestedFilesAndTheRoot()
    {
        string data = Path.Combine(_root, "Wavee");
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        File.WriteAllText(Path.Combine(data, "store.json"), "{}");
        File.WriteAllText(Path.Combine(data, "library.db"), "sqlite");
        File.WriteAllText(Path.Combine(data, "logs", "wavee.log"), "log");

        FactoryReset.WipeDirectories([data]);

        Assert.False(Directory.Exists(data));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void WipeDirectories_IsANoOpOnMissingRoots()
    {
        FactoryReset.WipeDirectories([Path.Combine(_root, "does-not-exist")]);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void ApplyPending_WipesDefaultRootsAndMarkerExtras_ThenDeletesTheMarker()
    {
        string local = Path.Combine(_root, "Local", "Wavee");
        string temp = Path.Combine(_root, "Temp", "Wavee");
        string extra = Path.Combine(_root, "D", "WaveeAudioCache");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(extra);
        File.WriteAllText(Path.Combine(local, "library.db"), "db");
        File.WriteAllText(Path.Combine(temp, "scratch"), "x");
        File.WriteAllText(Path.Combine(extra, "chunk"), "y");

        string marker = Path.Combine(_root, "Wavee.factory-reset.pending");
        FactoryReset.WriteMarker(marker, [extra], [local, temp]);
        Assert.True(File.Exists(marker));
        Assert.Contains(Path.GetFullPath(extra), File.ReadAllText(marker), StringComparison.OrdinalIgnoreCase);

        FactoryReset.ApplyPending(marker, [local, temp], wipeRegistry: false);

        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(local));
        Assert.False(Directory.Exists(temp));
        Assert.False(Directory.Exists(extra));
    }

    [Fact]
    public void WriteMarker_OmitsExtrasAlreadyUnderADefaultRoot()
    {
        string local = Path.Combine(_root, "Local", "Wavee");
        string nested = Path.Combine(local, "Cache", "audio");
        Directory.CreateDirectory(nested);

        string marker = Path.Combine(_root, "Wavee.factory-reset.pending");
        FactoryReset.WriteMarker(marker, [nested], [local]);

        Assert.Equal("", File.ReadAllText(marker).Trim());
    }

    [Fact]
    public void ApplyPending_NoOpsWhenTheMarkerIsMissing()
    {
        string local = Path.Combine(_root, "Local", "Wavee");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "keep.json"), "{}");

        FactoryReset.ApplyPending(Path.Combine(_root, "missing.pending"), [local], wipeRegistry: false);

        Assert.True(File.Exists(Path.Combine(local, "keep.json")));
    }
}
