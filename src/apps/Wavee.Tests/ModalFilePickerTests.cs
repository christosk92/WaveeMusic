// ── Wavee.Tests/ModalFilePickerTests.cs — the system file dialogs' threading contract (#155) ────────────────────────
//
// Every open-file, pick-folder and save-file dialog Wavee shows runs on its own STA thread, modal to the Wavee window,
// one at a time, with its answer posted back to the UI thread and a watchdog behind it. What is pinned here: the
// request vocabulary (the three variants), the STA runner, the one-at-a-time gate, the watchdog's verdicts, and — through
// `ModalFilePicker.Dialog`, the seam the shell dialog sits behind — that ShowAsync hands each variant to a dialog thread
// unchanged, never blocks its caller, refuses a second dialog and always lets go of the gate. No window, no shell.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModalFilePickerCollection
{
    public const string Name = "ModalFilePicker";
}

[Collection(ModalFilePickerCollection.Name)]
public class ModalFilePickerTests
{
    // ── 1. the request vocabulary ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenRequest_CarriesTitleAndFilters()
    {
        var r = PickerRequest.Open("Pick", ("Audio", "*.mp3;*.flac"), ("All", "*.*"));
        Assert.Equal(PickerKind.OpenFile, r.Kind);
        Assert.Equal("Pick", r.Title);
        Assert.Equal(new (string, string)[] { ("Audio", "*.mp3;*.flac"), ("All", "*.*") }, r.Filters);
        Assert.Equal("", r.DefaultFileName);
    }

    [Fact]
    public void FolderRequest_HasNoFiltersAndNoFileName()
    {
        var r = PickerRequest.Folder("Choose a folder");
        Assert.Equal(PickerKind.Folder, r.Kind);
        Assert.Equal("Choose a folder", r.Title);
        Assert.Empty(r.Filters);
        Assert.Equal("", r.DefaultFileName);
    }

    [Fact]
    public void SaveRequest_CarriesTheDefaultFileName()
    {
        var r = PickerRequest.Save("Export", "wavee-session-live.txt", ("Log text", "*.txt"), ("All files", "*.*"));
        Assert.Equal(PickerKind.SaveFile, r.Kind);
        Assert.Equal("wavee-session-live.txt", r.DefaultFileName);
        Assert.Equal(2, r.Filters.Length);
    }

    [Fact]
    public void Requests_ToleratePassedNulls()
    {
        Assert.Empty(PickerRequest.Open("t", null!).Filters);
        var save = PickerRequest.Save("t", null!, null!);
        Assert.Empty(save.Filters);
        Assert.Equal("", save.DefaultFileName);
    }

    // ── 2. ShowAsync over the dialog seam ────────────────────────────────────────────────────────────────────────────

    /// <summary>Run <paramref name="body"/> with <see cref="ModalFilePicker.Dialog"/> replaced, restoring it after.</summary>
    static async Task WithDialog(Func<nint, PickerRequest, string?> dialog, Func<Task> body)
    {
        var real = ModalFilePicker.Dialog;
        ModalFilePicker.Dialog = dialog;
        try { await body(); }
        finally { ModalFilePicker.Dialog = real; }
    }

    [Theory]
    [InlineData(PickerKind.OpenFile)]
    [InlineData(PickerKind.Folder)]
    [InlineData(PickerKind.SaveFile)]
    public async Task EveryVariant_ReachesADialogThread_Unchanged_WithTheOwner(PickerKind kind)
    {
        PickerRequest request = kind switch
        {
            PickerKind.Folder => PickerRequest.Folder("folder"),
            PickerKind.SaveFile => PickerRequest.Save("save", "a.txt", ("Text", "*.txt")),
            _ => PickerRequest.Open("open", ("Any", "*.*")),
        };
        PickerRequest? seen = null;
        nint seenOwner = 0;
        ApartmentState apartment = ApartmentState.Unknown;
        int caller = Environment.CurrentManagedThreadId, dialogThread = 0;
        await WithDialog((owner, r) =>
        {
            seen = r; seenOwner = owner;
            apartment = Thread.CurrentThread.GetApartmentState();
            dialogThread = Environment.CurrentManagedThreadId;
            return "C:\\picked";
        }, async () =>
        {
            int generation = ModalFilePicker.Generation;
            var task = kind switch
            {
                PickerKind.Folder => ModalFilePicker.PickFolderAsync(0x1234, request.Title),
                PickerKind.SaveFile => ModalFilePicker.SaveFileAsync(0x1234, request.Title, request.DefaultFileName, request.Filters),
                _ => ModalFilePicker.OpenFileAsync(0x1234, request.Title, request.Filters),
            };
            Assert.NotNull(task);
            Assert.Equal("C:\\picked", await task!.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(generation + 1, ModalFilePicker.Generation);
        });
        Assert.Equal(request.Kind, seen!.Kind);
        Assert.Equal(request.Title, seen.Title);
        Assert.Equal(request.DefaultFileName, seen.DefaultFileName);
        Assert.Equal(request.Filters, seen.Filters);
        Assert.Equal((nint)0x1234, seenOwner);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(caller, dialogThread);
        Assert.False(ModalFilePicker.IsOpen);
    }

    [Fact]
    public async Task ASecondPicker_IsRefusedWhileOneIsOpen_AndAdmittedOnceItCloses()
    {
        using var release = new ManualResetEventSlim(false);
        await WithDialog((_, _) => { Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return null; }, async () =>
        {
            var first = ModalFilePicker.PickFolderAsync(0, "first");
            Assert.NotNull(first);
            Assert.False(first!.IsCompleted);                    // the caller came back while the "dialog" is still up
            Assert.True(ModalFilePicker.IsOpen);
            Assert.Null(ModalFilePicker.SaveFileAsync(0, "second", "x.txt"));
            release.Set();
            Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(10)));   // null = cancelled
            Assert.False(ModalFilePicker.IsOpen);
            var third = ModalFilePicker.OpenFileAsync(0, "third");
            Assert.NotNull(third);
            await third!.WaitAsync(TimeSpan.FromSeconds(10));
        });
    }

    [Fact]
    public async Task AFailingDialog_FaultsTheTask_AndReleasesTheGate()
    {
        await WithDialog((_, _) => throw new InvalidOperationException("IModalWindow.Show failed"), async () =>
        {
            var task = ModalFilePicker.SaveFileAsync(0, "save", "a.txt");
            await Assert.ThrowsAsync<InvalidOperationException>(() => task!.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(ModalFilePicker.IsOpen);
        });
    }

    // ── 3. the STA runner, the gate, the watchdog ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StaThread_RunsTheWorkOnAnotherStaThread_WithoutBlockingTheCaller()
    {
        using var release = new ManualResetEventSlim(false);
        int caller = Environment.CurrentManagedThreadId;
        var task = StaThread.Run(() =>
        {
            // The work cannot finish until the caller has come back from Run: Run must not wait on it.
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return (Thread.CurrentThread.GetApartmentState(), Environment.CurrentManagedThreadId, Thread.CurrentThread.IsBackground);
        }, "test-sta");
        Assert.False(task.IsCompleted);
        release.Set();
        var (apartment, thread, background) = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(caller, thread);
        Assert.True(background);
    }

    [Fact]
    public async Task StaThread_FaultsTheTask_WhenTheWorkThrows()
    {
        var task = StaThread.Run<string?>(() => throw new InvalidOperationException("dialog failed"), "test-sta");
        await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void PickerGate_AdmitsOnePickerAtATime()
    {
        var gate = new PickerGate();
        Assert.True(gate.TryEnter());
        Assert.True(gate.IsHeld);
        Assert.False(gate.TryEnter());
        gate.Exit();
        Assert.False(gate.IsHeld);
        Assert.True(gate.TryEnter());
    }

    [Theory]
    [InlineData(false, false, false, 99_999, PickerRules.Verdict.Done)]          // the picker returned
    [InlineData(true, true, false, 99_999, PickerRules.Verdict.Done)]            // the dialog is up and modal
    [InlineData(true, false, false, 1_000, PickerRules.Verdict.Wait)]            // still coming up
    [InlineData(true, false, false, PickerRules.WatchdogMs, PickerRules.Verdict.ReleaseOwner)]
    [InlineData(true, false, true, PickerRules.WatchdogMs, PickerRules.Verdict.Done)]   // nothing to hand back
    public void Watchdog_HandsTheWindowBack_OnlyWhenTheDialogNeverShowed(bool open, bool visible, bool enabled, long elapsed,
                                                                         PickerRules.Verdict expected)
        => Assert.Equal(expected, PickerRules.Watch(open, visible, enabled, elapsed));

    [Theory]
    [InlineData(true, 5, 5, true)]
    [InlineData(true, 6, 5, false)]    // that picker closed and another opened: not this watch's business
    [InlineData(false, 5, 5, false)]
    public void Watchdog_JudgesOnlyThePickerItWasArmedFor(bool open, int now, int armed, bool expected)
        => Assert.Equal(expected, PickerRules.SamePicker(open, now, armed));
}
