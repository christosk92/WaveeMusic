using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Wavee.Tests;

// ReportRedactor.Redact (Diagnostics/ReportRedactor.cs): the whole-text scrubber every report bundle runs its
// contents through before anything leaves the machine (clipboard, saved file, or the URL). Every rule is pinned
// before->after, plus the false positives that must NOT be touched (a track URI, a build stamp, a log timestamp)
// and idempotency over a synthetic multi-line log.
public class ReportRedactorTests
{
    // ── Built-in path / identity / secret / network rules (RedactionRules.None -- no injected literals) ─────────────

    [Theory]
    [InlineData(@"C:\Users\bob\AppData\Local\Wavee\logs\wavee.log", @"C:\Users\<user>\AppData\Local\Wavee\logs\wavee.log")]
    [InlineData("c:/users/Bob/x", "c:/users/<user>/x")]
    [InlineData(@"%USERPROFILE%\x", @"<user-profile>\x")]
    [InlineData("/Users/bob/Library", "/Users/<user>/Library")]
    [InlineData(@"\\NAS01\share", @"\\<host>\share")]
    [InlineData("spotify:user:abc123", "spotify:user:<id>")]
    [InlineData("bob@example.com", "<email>")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9", "Authorization: Bearer <token>")]
    [InlineData("access_token=abc&refresh_token=def", "access_token=<redacted>&refresh_token=<redacted>")]
    [InlineData("country=NL product=premium", "country=<redacted> product=<redacted>")]
    [InlineData("ip=192.168.1.20", "ip=<ip>")]
    [InlineData("fe80::1%12", "<ip6>")]
    [InlineData("2001:db8::ff00:42:8329", "<ip6>")]
    [InlineData("AA-BB-CC-DD-EE-FF", "<mac>")]
    [InlineData("deviceId=0123456789abcdef0123", "deviceId=<device-id>")]
    public void Redact_AppliesEachRule(string before, string after)
        => Assert.Equal(after, ReportRedactor.Redact(before, RedactionRules.None));

    [Fact]
    public void Redact_EmptyOrNull_ReturnsEmptyString()
    {
        Assert.Equal("", ReportRedactor.Redact("", RedactionRules.None));
        Assert.Equal("", ReportRedactor.Redact(null!, RedactionRules.None));
    }

    // ── Injected literals ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Redact_InjectedUserName_IsReplaced_CaseInsensitive()
    {
        var rules = RedactionRules.None with { UserName = "chris" };
        Assert.Equal("Logged in as <user> today", ReportRedactor.Redact("Logged in as CHRIS today", rules));
    }

    [Fact]
    public void Redact_InjectedMachineName_IsReplaced_CaseInsensitive()
    {
        var rules = RedactionRules.None with { MachineName = "DESKTOP1" };
        Assert.Equal("host=<machine>", ReportRedactor.Redact("host=desktop1", rules));
    }

    [Fact]
    public void Redact_InjectedSpotifyUserId_IsReplaced()
    {
        var rules = RedactionRules.None with { SpotifyUserId = "abc123spotify" };
        Assert.Equal("owner: <spotify-user>", ReportRedactor.Redact("owner: abc123spotify", rules));
    }

    [Fact]
    public void Redact_InjectedDisplayName_IsReplaced()
    {
        var rules = RedactionRules.None with { DisplayName = "Bob Smith" };
        Assert.Equal("signed in as <display-name>", ReportRedactor.Redact("signed in as Bob Smith", rules));
    }

    [Fact]
    public void Redact_InjectedDeviceNames_AreEachReplaced()
    {
        var rules = RedactionRules.None with { DeviceNames = new List<string> { "Kitchen Speaker", "Office PC" } };
        Assert.Equal("active: <device>, idle: <device>",
            ReportRedactor.Redact("active: Kitchen Speaker, idle: Office PC", rules));
    }

    /// <summary>A two-letter user name would erase half the alphabet if redacted case-insensitively -- literals
    /// under 3 characters are skipped entirely.</summary>
    [Fact]
    public void Redact_InjectedLiteral_UnderThreeChars_IsSkipped()
    {
        var rules = RedactionRules.None with { UserName = "Al" };
        Assert.Equal("Alright then", ReportRedactor.Redact("Alright then", rules));
    }

    [Fact]
    public void Redact_InjectedLiteral_ExactlyThreeChars_IsReplaced()
    {
        var rules = RedactionRules.None with { UserName = "bob" };
        Assert.Equal("hi <user>", ReportRedactor.Redact("hi BOB", rules));
    }

    // ── False positives: text that must survive Redact untouched ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("version=0.2.5.6")]
    [InlineData("0.2.5 Breaker (0.2.5.6)")]
    [InlineData("t=12:34:56.789")]
    [InlineData("seq=1 tid=5")]
    [InlineData("Wavee!<BaseAddress>+0x7b1fc6")]
    [InlineData("user")]
    [InlineData("key of C")]
    public void Redact_NeverTouchesTheseFalsePositives(string text)
        => Assert.Equal(text, ReportRedactor.Redact(text, RedactionRules.None));

    // ── Idempotency ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Placeholders never re-match a rule (an inserted "<user>" contains no digits an IPv4 rule could read,
    /// no "@" an email rule could read, etc.), so running Redact twice must be a no-op the second time -- the report
    /// composer relies on this to cache a redacted body and reuse it per keystroke.</summary>
    [Fact]
    public void Redact_IsIdempotent_OverASyntheticLog()
    {
        var rules = new RedactionRules(UserName: "chris", MachineName: "DESKTOP-CHRIS", SpotifyUserId: "31abc123xyz",
            DisplayName: "Chris K", DeviceNames: new List<string> { "Living Room Speaker" });

        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            sb.AppendLine(i switch
            {
                0 => @"C:\Users\chris\AppData\Local\Wavee\logs\wavee.log opened",
                1 => "user=CHRIS machine=desktop-chris",
                2 => "spotify:user:31abc123xyz signed in",
                3 => "contact chris@example.com for support",
                4 => "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abcdef",
                5 => "access_token=zzz&refresh_token=yyy",
                6 => "country=NL product=premium tier=free",
                7 => "connected to 192.168.1.20 via fe80::1%12",
                8 => "peer 2001:db8::ff00:42:8329 mac AA-BB-CC-DD-EE-FF",
                9 => "deviceId=0123456789abcdef0123 name=Living Room Speaker",
                10 => "display name: Chris K",
                11 => @"\\NAS01\share\music",
                12 => "spotify:track:4uLU6hMCjMI75M1A2tKUQC playing",
                13 => "version=0.2.5.6 quad=0.2.5.6",
                14 => "t=12:34:56.789 seq=1 tid=5",
                _ => $"line {i}: nothing interesting here",
            });
        }
        string log = sb.ToString();

        string once = ReportRedactor.Redact(log, rules);
        string twice = ReportRedactor.Redact(once, rules);

        Assert.Equal(once, twice);
    }
}
