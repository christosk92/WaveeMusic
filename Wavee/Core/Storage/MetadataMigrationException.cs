using System;

namespace Wavee.Core.Storage;

/// <summary>
/// Why a metadata-DB open failed. Surfaced through
/// <see cref="MetadataMigrationException"/> so the startup gate can decide
/// between "offer to rebuild" and "block — this client is too old".
/// </summary>
public enum MetadataMigrationFailureReason
{
    /// <summary>A schema migration step threw. The DB is in a partially-migrated
    /// or pre-migration state. Recoverable by deleting the DB file and
    /// refetching on next open.</summary>
    MigrationFailed,

    /// <summary>The DB's recorded <c>user_version</c> is higher than the schema
    /// this build of Wavee supports — a newer Wavee version wrote it. Not
    /// safely recoverable here; user must update Wavee or rebuild the cache.</summary>
    Downgrade,

    /// <summary>The DB file is present but unreadable (corruption, wrong magic
    /// bytes, truncated). Recoverable by rebuilding.</summary>
    Corrupted
}

/// <summary>
/// Thrown by <see cref="MetadataDatabase"/> when schema initialization cannot
/// complete. The startup UI gate catches this, surfaces the error to the user,
/// and offers <see cref="MetadataDatabase.DeleteDatabaseFile(string)"/> as the
/// recovery path for the recoverable reasons.
/// </summary>
public sealed class MetadataMigrationException : Exception
{
    public int FromVersion { get; }
    public int ToVersion { get; }
    public MetadataMigrationFailureReason Reason { get; init; }
        = MetadataMigrationFailureReason.MigrationFailed;

    public MetadataMigrationException(
        string message,
        int fromVersion,
        int toVersion,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;
    }
}
