using System;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// <c>CrashReportFrames.ParseRvas</c> over the exact text a shipped NativeAOT build prints (<c>StackTraceSupport=false</c>:
/// every frame is <c>at Wavee!&lt;BaseAddress&gt;+0x…</c>). The numbers it returns are what the crash report's
/// "Frames (RVA)" section lists, and what gets pasted into a symbol lookup — so order, duplicates and the exact hex
/// value all matter.
/// </summary>
public sealed class CrashReportFramesTests
{
    // The 0.2.0.1 x64 crash, verbatim shape: an async continuation boundary in the middle, the outer wrapper around it.
    const string Shipped =
        "System.NullReferenceException: Object reference not set to an instance of an object.\n" +
        "   at Wavee!<BaseAddress>+0x7b1fc6\n" +
        "   at Wavee!<BaseAddress>+0x1b616c\n" +
        "   at Wavee!<BaseAddress>+0x13d2ed\n" +
        "--- End of stack trace from previous location ---\n" +
        "   at Wavee!<BaseAddress>+0x1cb894\n" +
        "   at Wavee!<BaseAddress>+0x986f22\n";

    [Fact]
    public void ParsesEveryFrameInStackOrder()
    {
        var rvas = CrashReportFrames.ParseRvas(Shipped);

        Assert.Equal(new long[] { 0x7b1fc6, 0x1b616c, 0x13d2ed, 0x1cb894, 0x986f22 }, rvas);
    }

    [Fact]
    public void KeepsARepeatedFrame()
    {
        // A recursive crash repeats the same frame; collapsing it would hide the recursion.
        var rvas = CrashReportFrames.ParseRvas(
            "   at Wavee!<BaseAddress>+0x10\n   at Wavee!<BaseAddress>+0x10\n   at Wavee!<BaseAddress>+0x20\n");

        Assert.Equal(new long[] { 0x10, 0x10, 0x20 }, rvas);
    }

    [Fact]
    public void ReadsInnerExceptionFramesToo()
    {
        // ex.ToString() nests the inner exception's frames before the outer ones; both sets are wanted.
        string text =
            "System.AggregateException: One or more errors occurred.\n" +
            " ---> System.NullReferenceException: Object reference not set to an instance of an object.\n" +
            "   at Wavee!<BaseAddress>+0xabc\n" +
            "   --- End of inner exception stack trace ---\n" +
            "   at Wavee!<BaseAddress>+0xdef\n";

        Assert.Equal(new long[] { 0xabc, 0xdef }, CrashReportFrames.ParseRvas(text));
    }

    [Fact]
    public void AcceptsUpperCaseHex()
    {
        Assert.Equal(new long[] { 0x7B1FC6 }, CrashReportFrames.ParseRvas("   at Wavee!<BaseAddress>+0x7B1FC6"));
    }

    [Fact]
    public void StopsAtTheFirstNonHexCharacter()
    {
        // A frame followed by trailing text on the same line (a future runtime printing more) must not swallow it.
        Assert.Equal(new long[] { 0x1234 }, CrashReportFrames.ParseRvas("   at Wavee!<BaseAddress>+0x1234 (offset)"));
    }

    [Fact]
    public void IgnoresSymbolicatedJitFrames()
    {
        // A dev (JIT) build prints real names; there is no RVA to extract and the section must stay empty.
        string text =
            "System.NullReferenceException: Object reference not set to an instance of an object.\n" +
            "   at Wavee.Features.Detail.DetailPage.MapPlaylist(Playlist p) in C:\\wavee\\DetailPage.cs:line 508\n" +
            "   at Wavee.Program.Main(String[] args)\n";

        Assert.Empty(CrashReportFrames.ParseRvas(text));
    }

    [Fact]
    public void IgnoresAMarkerWithNoOffset()
    {
        Assert.Empty(CrashReportFrames.ParseRvas("   at Wavee!<BaseAddress>+0x\n   at Wavee!<BaseAddress>+0xzz"));
    }

    [Fact]
    public void IgnoresAnOffsetTooLongToBeAnRva()
    {
        // 16+ hex digits cannot be an image offset; refusing it beats throwing inside the crash writer.
        Assert.Empty(CrashReportFrames.ParseRvas("   at Wavee!<BaseAddress>+0x10000000000000000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no frames here")]
    public void EmptyOrFramelessInputIsAnEmptyList(string? text)
    {
        Assert.Empty(CrashReportFrames.ParseRvas(text));
    }
}
