// ── Wavee.Tests/StoreHealthTests.cs — what a mid-session sqlite failure means for the file (wave D1) ───────────────
//
// `StoreHealth.OnFault` is the whole decision behind Store.cs's mid-session recovery, extracted so it is pinned with no
// file at all: a statement about the FILE (SQLITE_CORRUPT, SQLITE_NOTADB) rebuilds it ONCE per store session, and
// everything else — and any second corruption — is only counted. The "once" is the fact that keeps a disk that keeps
// destroying the file from turning into a rebuild loop; `RecoverySpent` is its other half (the store then goes
// memory-only for the rest of the session instead of faulting every batch).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class StoreHealthTests
{
    [Theory]
    [InlineData(11)]   // SQLITE_CORRUPT — the 2026-09-18 file
    [InlineData(26)]   // SQLITE_NOTADB — garbage where a database should be
    public void The_first_statement_that_the_file_is_damaged_recovers(int code)
    {
        Assert.Equal(StoreFaultVerdict.Recover, StoreHealth.OnFault(code, alreadyRecovered: false));
        Assert.False(StoreHealth.RecoverySpent(code, alreadyRecovered: false));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(26)]
    public void A_second_damaged_file_in_the_same_session_is_counted_and_spends_the_recovery(int code)
    {
        Assert.Equal(StoreFaultVerdict.Count, StoreHealth.OnFault(code, alreadyRecovered: true));
        Assert.True(StoreHealth.RecoverySpent(code, alreadyRecovered: true));   // ⇒ memory-only, never a loop
    }

    [Theory]
    [InlineData(1)]    // SQLITE_ERROR: the statement, not the file
    [InlineData(5)]    // SQLITE_BUSY: another writer, never a reason to rebuild
    [InlineData(6)]    // SQLITE_LOCKED
    [InlineData(10)]   // SQLITE_IOERR: the machine, not the bytes
    [InlineData(13)]   // SQLITE_FULL: a rebuild would only fill the disk again
    [InlineData(0)]    // no sqlite code at all (an I/O exception, a disposed object, a bug)
    public void Every_other_failure_is_only_counted_and_never_spends_anything(int code)
    {
        Assert.Equal(StoreFaultVerdict.Count, StoreHealth.OnFault(code, alreadyRecovered: false));
        Assert.Equal(StoreFaultVerdict.Count, StoreHealth.OnFault(code, alreadyRecovered: true));
        Assert.False(StoreHealth.RecoverySpent(code, alreadyRecovered: false));
        Assert.False(StoreHealth.RecoverySpent(code, alreadyRecovered: true));
    }

    /// <summary>The recover-at-open rule and the recover-mid-session rule are ONE rule: whatever recreates a file when
    /// it is opened is exactly what rebuilds it while it is in use.</summary>
    [Theory]
    [InlineData(11)] [InlineData(26)] [InlineData(1)] [InlineData(5)] [InlineData(10)] [InlineData(13)]
    public void Recovery_mid_session_uses_the_open_time_verdict(int code)
        => Assert.Equal(Store.IsUnreadableFile(code),
                        StoreHealth.OnFault(code, alreadyRecovered: false) == StoreFaultVerdict.Recover);
}
