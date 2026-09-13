namespace Wavee.Core;

/// <summary>
/// The Microsoft Store's version rules, as a pure mapping from Wavee's own numbers. A Store package must carry
/// <c>M.m.p.0</c>: the fourth part is reserved for the Store (and must be 0 on upload) and the first part cannot be 0.
/// Wavee's feed quad is <c>0.m.p.&lt;WaveeBuild&gt;</c>, so the Store quad folds the build counter into the third part
/// and lifts the major by one: <c>0.2.1</c> build 2 → <c>1.2.102.0</c>. Monotonic as long as the semver and the build
/// counter only ever go up (both do — the release script bumps the build on every release), which is what the
/// Store requires between submissions.
/// </summary>
public static class StoreVersion
{
    /// <summary>Per-part ceiling Windows enforces on package versions.</summary>
    public const int MaxPart = 65535;

    /// <summary>The Store quad for <paramref name="coreSemver"/> (<c>M.m.p</c>, no pre-release tag) at
    /// <paramref name="build"/> (the monotonic <c>WaveeBuild</c> counter).</summary>
    public static string Quad(string coreSemver, int build)
    {
        if (string.IsNullOrWhiteSpace(coreSemver)) throw new ArgumentException("core semver required", nameof(coreSemver));
        if (build < 0) throw new ArgumentOutOfRangeException(nameof(build));
        var parts = coreSemver.Trim().Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor) || !int.TryParse(parts[2], out int patch))
            throw new ArgumentException("core semver must be M.m.p; got '" + coreSemver + "'", nameof(coreSemver));
        int third = patch * 100 + build;
        if (major + 1 > MaxPart || minor > MaxPart || third > MaxPart)
            throw new ArgumentOutOfRangeException(nameof(build), "Store quad part exceeds " + MaxPart + " for " + coreSemver + " build " + build);
        return (major + 1) + "." + minor + "." + third + ".0";
    }

    /// <summary>True when <paramref name="quad"/> already satisfies the Store's shape (first part ≥ 1, fourth part 0).</summary>
    public static bool IsStoreShaped(string quad)
    {
        var p = quad.Split('.');
        return p.Length == 4 && int.TryParse(p[0], out int a) && a >= 1 && int.TryParse(p[3], out int d) && d == 0;
    }
}

/// <summary>Deep links into the Store app for a listed product.</summary>
public static class StoreLinks
{
    /// <summary>The product page (opens the Store app on the listing, which is where updates are applied from).</summary>
    public static string ProductPage(string storeId) => "ms-windows-store://pdp/?productid=" + storeId;

    /// <summary>The web listing, for places that cannot open the Store app.</summary>
    public static string WebPage(string storeId) => "https://apps.microsoft.com/detail/" + storeId;
}
