namespace EverythingBox.Server.Sync;

/// <summary>One object's metadata as listed. Version is the server-assigned opaque per-write stamp
/// (the ETag). Meta is the opaque client string (X-Sync-Meta), stored and echoed, never interpreted.</summary>
public sealed record SyncObjectInfo(string Key, string Version, string? Meta, long Size, bool Deleted, DateTime ModifiedUtc);

/// <summary>A live object's bytes (as a file on disk) plus its stamp/meta.</summary>
public sealed record SyncObjectContent(string BlobPath, string Version, string? Meta, long Size);

public enum SyncConditionKind { Unconditional, IfMatch, IfNoneMatchStar }

/// <summary>The conditional-write precondition parsed from If-Match / If-None-Match headers.</summary>
public sealed record SyncCondition(SyncConditionKind Kind, string? Version = null)
{
    public static readonly SyncCondition None = new(SyncConditionKind.Unconditional);
}

public enum SyncWriteStatus { Ok, PreconditionFailed, QuotaExceeded, TooLarge }

/// <summary>Outcome of a PUT/DELETE. Version is the NEW version on Ok.</summary>
public sealed record SyncWriteOutcome(SyncWriteStatus Status, string? Version = null);
