using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Wavee;

static class StartupPreflight
{
    const string MissingNativeTitle = "Wavee cannot start";

    public static bool TryGetBlockingIssue(out string title, out string body)
    {
        if (OperatingSystem.IsWindows() && !TryCheckSqliteStorage(out body))
        {
            title = MissingNativeTitle;
            return true;
        }

        title = "";
        body = "";
        return false;
    }

    /// <summary>Prove the SQLite stack works. JIT builds ship <c>e_sqlite3.dll</c> under
    /// <c>runtimes/win-*/native/</c>; publish colocates it next to the exe — both are valid.</summary>
    static bool TryCheckSqliteStorage(out string message)
    {
        string baseDir = AppContext.BaseDirectory;
        string? nativePath = ResolveNativeLibraryPath(baseDir, "e_sqlite3.dll");
        if (nativePath is null)
        {
            message =
                "Wavee is missing the SQLite native library (e_sqlite3.dll)." + Environment.NewLine + Environment.NewLine +
                $"Started from:{Environment.NewLine}{baseDir}{Environment.NewLine}{Environment.NewLine}" +
                "Rebuild the app (dotnet build app/Wavee/Wavee.csproj) so NuGet copies the native runtime, " +
                "or run from a complete publish folder.";
            return false;
        }

        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            message = "";
            return true;
        }
        catch (Exception ex)
        {
            message =
                "Wavee found e_sqlite3.dll, but SQLite could not initialize." + Environment.NewLine + Environment.NewLine +
                $"Native path:{Environment.NewLine}{nativePath}{Environment.NewLine}{Environment.NewLine}" +
                $"Process architecture: {RuntimeInformation.ProcessArchitecture}{Environment.NewLine}" +
                $"Error: {ex.Message}";
            return false;
        }
    }

    internal static string? ResolveNativeLibraryPath(string baseDir, string fileName)
    {
        string direct = Path.Combine(baseDir, fileName);
        if (File.Exists(direct)) return direct;

        if (!OperatingSystem.IsWindows()) return null;

        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            Architecture.Arm => "win-arm",
            _ => "win-x64",
        };
        string ridPath = Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        return File.Exists(ridPath) ? ridPath : null;
    }
}
