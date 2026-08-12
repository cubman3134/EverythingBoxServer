# Sync endpoints — Increment A (server object store) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A token-authenticated, per-namespace, versioned object store on the host: `GET` list, `GET`/`PUT`/`DELETE` objects, with `If-Match`/`If-None-Match` compare-and-swap (412), per-namespace quota (507), tombstone-on-delete, and atomic durable writes. The server never interprets payloads.

**Architecture:** A `SyncStore` (durable per-namespace files + `index.json`, hashed keys, per-namespace async lock for atomic CAS) behind a `MapSync` route group under the existing token prefix, enabled by an opt-in `SyncConfig`. Mirrors `FileResolverCache` (atomic temp-then-move), `AddonEndpoints` (route idiom), `DownloadConfig` (config section), `TokenPluginServerFactory` (auth test harness).

**Tech Stack:** .NET 9 / C#, ASP.NET minimal APIs, xUnit + `WebApplicationFactory`. All in `EverythingBox.Server` + `EverythingBox.Server.Tests`.

## Global Constraints

- **Server never interprets payloads.** Blobs are opaque; `meta` (`X-Sync-Meta`) is stored and echoed verbatim, never parsed.
- **Security:** namespace validated (`^[A-Za-z0-9._-]{1,64}$`, not `.`/`..`) AND its resolved dir confirmed contained under the sync root; keys are **hashed** (SHA-256) to blob filenames so a client key is never a filesystem path segment (no traversal). The token path-prefix is the only auth (no per-route check).
- **Atomic CAS:** a per-namespace `SemaphoreSlim` serializes check-version→write→bump→rewrite-index; blob and index are written temp-then-`File.Move(overwrite:true)`.
- **Opt-in / degrade-not-refuse:** `SyncConfig.Enabled=false` by default; when disabled the routes are not mapped; an absent `Sync` config section deserializes to disabled.
- **No `ServerApi` version bump** (host routes, not the plugin contract). No new NuGet package.
- PUBLIC repo cleanliness — no external content-source name (code/paths/commit messages); `RepositoryCleanlinessTests` green. Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: `SyncConfig` + `SyncStore` (the durable store) + unit tests

**Files:**
- Modify: `EverythingBox.Server/ServerConfig.cs` (add `Sync` + `SyncConfig`)
- Create: `EverythingBox.Server/Sync/SyncStore.cs`
- Create: `EverythingBox.Server/Sync/SyncTypes.cs` (the small records/enums)
- Create: `EverythingBox.Server.Tests/SyncStoreTests.cs`

**Interfaces:**
- Produces: `SyncStore(string rootDir, long perNamespaceQuotaBytes, long maxObjectBytes)` with `static bool IsValidNamespace(string)`, `Task<IReadOnlyList<SyncObjectInfo>> ListAsync(string ns, CancellationToken)`, `Task<SyncObjectContent?> GetAsync(string ns, string key, CancellationToken)`, `Task<SyncWriteOutcome> PutAsync(string ns, string key, Stream body, SyncCondition condition, string? meta, CancellationToken)`, `Task<SyncWriteOutcome> DeleteAsync(string ns, string key, SyncCondition condition, CancellationToken)`.

- [ ] **Step 1: `SyncConfig` on `ServerConfig`** — add the property + section (mirror `DownloadConfig`):
```csharp
    /// <summary>Self-hosted save/state sync object store. OFF by default; when disabled the sync
    /// routes are not mapped at all.</summary>
    public SyncConfig Sync { get; set; } = new();
```
and, next to `DownloadConfig`:
```csharp
public sealed class SyncConfig
{
    /// <summary>Off by default. When true, the /{token}/sync routes are mapped.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where per-namespace sync data is stored. Defaults to a "sync" folder next to the exe.</summary>
    public string? Directory { get; set; }

    /// <summary>Hard byte cap per namespace; a PUT that would exceed it is refused with 507.</summary>
    public long PerNamespaceQuotaBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Largest single object accepted; a larger PUT body is refused with 400.</summary>
    public long MaxObjectBytes { get; set; } = 64L * 1024 * 1024;

    public string ResolvedSyncDirectory =>
        Environment.GetEnvironmentVariable("EBS_SYNC_DIR") is { Length: > 0 } fromEnv ? fromEnv
        : !string.IsNullOrWhiteSpace(Directory) ? Directory!
        : Path.Combine(AppContext.BaseDirectory, "sync");
}
```

- [ ] **Step 2: `SyncTypes.cs`** — the small value types:
```csharp
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
```

- [ ] **Step 3: `SyncStore.cs`** — the store. Implement exactly this shape:
```csharp
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EverythingBox.Server.Sync;

/// <summary>
/// A dumb, versioned, per-namespace object store: plain blob files + one index.json per namespace.
/// Keys are hashed to blob filenames (a client key is never a path segment → no traversal). A
/// per-namespace lock serialises mutations so check-version→write→bump is atomic. The store never
/// reads a payload; it stores bytes + an opaque per-object version and an opaque client meta string.
/// </summary>
public sealed partial class SyncStore
{
    private readonly string _root;
    private readonly long _perNamespaceQuotaBytes;
    private readonly long _maxObjectBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public SyncStore(string rootDir, long perNamespaceQuotaBytes, long maxObjectBytes)
    {
        _root = rootDir;
        _perNamespaceQuotaBytes = perNamespaceQuotaBytes;
        _maxObjectBytes = maxObjectBytes;
        try { Directory.CreateDirectory(_root); } catch { /* created lazily on first write too */ }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NamespacePattern();

    public static bool IsValidNamespace(string ns)
        => !string.IsNullOrEmpty(ns) && ns is not ("." or "..") && NamespacePattern().IsMatch(ns);

    // ---- internal index model (persisted as index.json) ----
    private sealed class IndexEntry
    {
        public string Version { get; set; } = "";
        public string? Meta { get; set; }
        public long Size { get; set; }
        public bool Deleted { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }
    private sealed class Index { public Dictionary<string, IndexEntry> Objects { get; set; } = new(StringComparer.Ordinal); }

    private SemaphoreSlim LockFor(string ns) => _locks.GetOrAdd(ns, _ => new SemaphoreSlim(1, 1));

    // The namespace directory, containment-checked under _root. Null if the namespace is invalid or
    // (defensively) resolves outside the root.
    private string? NamespaceDir(string ns)
    {
        if (!IsValidNamespace(ns)) return null;
        string full, rootFull;
        try { full = Path.GetFullPath(Path.Combine(_root, ns)); rootFull = Path.GetFullPath(_root); }
        catch { return null; }
        var boundary = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(boundary, cmp) ? full : null;
    }

    private static string BlobName(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string NewVersion() => Guid.NewGuid().ToString("N");

    private static Index LoadIndex(string nsDir)
    {
        var path = Path.Combine(nsDir, "index.json");
        try
        {
            if (!File.Exists(path)) return new Index();
            return JsonSerializer.Deserialize<Index>(File.ReadAllText(path), Json) ?? new Index();
        }
        catch { return new Index(); } // a corrupt index degrades to empty rather than throwing out of a request
    }

    private static void SaveIndex(string nsDir, Index index)
    {
        Directory.CreateDirectory(nsDir);
        var path = Path.Combine(nsDir, "index.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index, Json));
        File.Move(temp, path, overwrite: true);
    }

    public async Task<IReadOnlyList<SyncObjectInfo>> ListAsync(string ns, CancellationToken ct)
    {
        var nsDir = NamespaceDir(ns);
        if (nsDir is null) return [];
        var gate = LockFor(ns);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = LoadIndex(nsDir);
            return index.Objects
                .Select(kv => new SyncObjectInfo(kv.Key, kv.Value.Version, kv.Value.Meta, kv.Value.Size, kv.Value.Deleted, kv.Value.ModifiedUtc))
                .ToList();
        }
        finally { gate.Release(); }
    }

    public async Task<SyncObjectContent?> GetAsync(string ns, string key, CancellationToken ct)
    {
        var nsDir = NamespaceDir(ns);
        if (nsDir is null) return null;
        var gate = LockFor(ns);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = LoadIndex(nsDir);
            if (!index.Objects.TryGetValue(key, out var e) || e.Deleted) return null;
            var blob = Path.Combine(nsDir, BlobName(key));
            if (!File.Exists(blob)) return null;
            return new SyncObjectContent(blob, e.Version, e.Meta, e.Size);
        }
        finally { gate.Release(); }
    }

    public async Task<SyncWriteOutcome> PutAsync(string ns, string key, Stream body, SyncCondition condition, string? meta, CancellationToken ct)
    {
        var nsDir = NamespaceDir(ns);
        if (nsDir is null) return new SyncWriteOutcome(SyncWriteStatus.PreconditionFailed); // invalid ns → caller 400s first; defensive
        var gate = LockFor(ns);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = LoadIndex(nsDir);
            index.Objects.TryGetValue(key, out var existing);

            if (!ConditionSatisfied(condition, existing))
                return new SyncWriteOutcome(SyncWriteStatus.PreconditionFailed);

            // Stream to a temp file, counting bytes and enforcing the per-object cap without buffering.
            Directory.CreateDirectory(nsDir);
            var temp = Path.Combine(nsDir, BlobName(key) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            long size;
            try { size = await CopyCappedAsync(body, temp, _maxObjectBytes, ct).ConfigureAwait(false); }
            catch (TooLargeException) { TryDelete(temp); return new SyncWriteOutcome(SyncWriteStatus.TooLarge); }
            catch { TryDelete(temp); throw; }

            // Quota: sum of LIVE object sizes, replacing this key's old contribution.
            var oldContribution = existing is { Deleted: false } ? existing.Size : 0;
            var liveTotal = index.Objects.Values.Where(v => !v.Deleted).Sum(v => v.Size);
            if (liveTotal - oldContribution + size > _perNamespaceQuotaBytes)
            {
                TryDelete(temp);
                return new SyncWriteOutcome(SyncWriteStatus.QuotaExceeded);
            }

            var blob = Path.Combine(nsDir, BlobName(key));
            File.Move(temp, blob, overwrite: true);
            var version = NewVersion();
            index.Objects[key] = new IndexEntry { Version = version, Meta = meta, Size = size, Deleted = false, ModifiedUtc = DateTime.UtcNow };
            SaveIndex(nsDir, index);
            return new SyncWriteOutcome(SyncWriteStatus.Ok, version);
        }
        finally { gate.Release(); }
    }

    public async Task<SyncWriteOutcome> DeleteAsync(string ns, string key, SyncCondition condition, CancellationToken ct)
    {
        var nsDir = NamespaceDir(ns);
        if (nsDir is null) return new SyncWriteOutcome(SyncWriteStatus.PreconditionFailed);
        var gate = LockFor(ns);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = LoadIndex(nsDir);
            index.Objects.TryGetValue(key, out var existing);
            if (!ConditionSatisfied(condition, existing))
                return new SyncWriteOutcome(SyncWriteStatus.PreconditionFailed);

            TryDelete(Path.Combine(nsDir, BlobName(key))); // free the bytes; keep a tombstone in the index
            var version = NewVersion();
            index.Objects[key] = new IndexEntry { Version = version, Meta = existing?.Meta, Size = 0, Deleted = true, ModifiedUtc = DateTime.UtcNow };
            SaveIndex(nsDir, index);
            return new SyncWriteOutcome(SyncWriteStatus.Ok, version);
        }
        finally { gate.Release(); }
    }

    // A live object counts as "present" for If-None-Match:*; a tombstone counts as absent (re-creatable).
    private static bool ConditionSatisfied(SyncCondition c, IndexEntry? existing) => c.Kind switch
    {
        SyncConditionKind.Unconditional => true,
        SyncConditionKind.IfNoneMatchStar => existing is null || existing.Deleted,
        SyncConditionKind.IfMatch => existing is not null && existing.Version == c.Version,
        _ => false,
    };

    private sealed class TooLargeException : Exception { }

    private static async Task<long> CopyCappedAsync(Stream src, string destPath, long cap, CancellationToken ct)
    {
        await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > cap) throw new TooLargeException();
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        await dest.FlushAsync(ct).ConfigureAwait(false);
        return total;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }
}
```

- [ ] **Step 4: `SyncStoreTests.cs`** — over a temp root, driving the store directly (bodies = `MemoryStream`):
  - `IsValidNamespace`: accepts `p1`, `Prof-2.a_b`; rejects ``, `.`, `..`, `a/b`, `a\b`, `../x`, a 65-char string.
  - put→get round-trips the exact bytes; `SyncObjectContent.Version`/`Meta`/`Size` match; the version changes on a second put.
  - `ListAsync` returns the entry with size/version/meta; a second key appears; a tombstone appears with `Deleted:true`.
  - `If-Match` with the current version succeeds and returns a new version; with a stale version → `PreconditionFailed`; unconditional always writes.
  - `If-None-Match:*` succeeds when absent, `PreconditionFailed` when a live object exists, succeeds again after a delete (tombstone = absent).
  - delete → `GetAsync` null, `ListAsync` shows deleted; a `PutAsync` after delete re-creates (new version, live).
  - quota: a put whose size would exceed `PerNamespaceQuotaBytes` (set small in the test) → `QuotaExceeded`, and the prior object is intact (not corrupted).
  - object cap: a body over `MaxObjectBytes` → `TooLarge`, no partial blob left.
  - **containment:** a namespace `..` / `a/b` is rejected by `IsValidNamespace` (so `GetAsync`/`PutAsync` on it return empty/precondition without touching disk outside root).
  - **concurrency:** N parallel `PutAsync` to the SAME key complete without a corrupt index (final `ListAsync` has exactly one entry; the file parses); N parallel puts to N distinct keys all land.

- [ ] **Step 5: Build + test + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` (green incl. `SyncStoreTests`, `RepositoryCleanlinessTests`, and existing `ServerConfigTests`).
```bash
git add EverythingBox.Server/ServerConfig.cs EverythingBox.Server/Sync/SyncStore.cs EverythingBox.Server/Sync/SyncTypes.cs EverythingBox.Server.Tests/SyncStoreTests.cs
git commit -m "feat: SyncStore — a per-namespace versioned object store with atomic CAS and quota"
```

---

### Task 2: `MapSync` routes + wiring + integration tests

**Files:**
- Create: `EverythingBox.Server/Sync/SyncEndpoints.cs`
- Modify: `EverythingBox.Server/Program.cs` (register `SyncStore` + map the routes when enabled)
- Create: `EverythingBox.Server.Tests/SyncServerFactory.cs`
- Create: `EverythingBox.Server.Tests/SyncEndpointsTests.cs`

**Interfaces:**
- Consumes: `SyncStore` (Task 1), `SyncCondition`, the outcome types.
- Produces: `SyncEndpoints.MapSync(this WebApplication app, string prefix)`.

- [ ] **Step 1: `SyncEndpoints.cs`** — the route group (mirror `AddonEndpoints`):
```csharp
using EverythingBox.Server.Sync;
using Microsoft.AspNetCore.Http;

namespace EverythingBox.Server;

public static class SyncEndpoints
{
    public static void MapSync(this WebApplication app, string prefix)
    {
        // GET list
        app.MapGet($"{prefix}/sync/{{ns}}", async (string ns, SyncStore store, CancellationToken ct) =>
        {
            if (!SyncStore.IsValidNamespace(ns)) return Results.BadRequest();
            var objects = await store.ListAsync(ns, ct);
            return Results.Json(new { objects });
        });

        // GET one object's bytes
        app.MapGet($"{prefix}/sync/{{ns}}/{{**key}}", async (string ns, string key, SyncStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!SyncStore.IsValidNamespace(ns)) return Results.BadRequest();
            var obj = await store.GetAsync(ns, key, ct);
            if (obj is null) return Results.NotFound();
            http.Response.Headers.ETag = Quote(obj.Version);
            http.Response.Headers["X-Sync-Meta"] = obj.Meta ?? "";
            return Results.File(obj.BlobPath, "application/octet-stream");
        });

        // PUT (conditional) — the first write route on this host
        app.MapPut($"{prefix}/sync/{{ns}}/{{**key}}", async (string ns, string key, SyncStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!SyncStore.IsValidNamespace(ns)) return Results.BadRequest();
            var condition = ParseCondition(http.Request);
            var meta = http.Request.Headers.TryGetValue("X-Sync-Meta", out var m) ? m.ToString() : null;
            var outcome = await store.PutAsync(ns, key, http.Request.Body, condition, meta, ct);
            return ToResult(outcome, http);
        });

        // DELETE (tombstone, conditional)
        app.MapDelete($"{prefix}/sync/{{ns}}/{{**key}}", async (string ns, string key, SyncStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!SyncStore.IsValidNamespace(ns)) return Results.BadRequest();
            var outcome = await store.DeleteAsync(ns, key, ParseCondition(http.Request), ct);
            return ToResult(outcome, http);
        });
    }

    private static string Quote(string v) => "\"" + v + "\"";
    private static string Unquote(string v) => v.Trim().Trim('"');

    private static SyncCondition ParseCondition(HttpRequest req)
    {
        var ifNoneMatch = req.Headers.IfNoneMatch.ToString();
        if (ifNoneMatch.Trim() == "*") return new SyncCondition(SyncConditionKind.IfNoneMatchStar);
        var ifMatch = req.Headers.IfMatch.ToString();
        if (!string.IsNullOrWhiteSpace(ifMatch)) return new SyncCondition(SyncConditionKind.IfMatch, Unquote(ifMatch));
        return SyncCondition.None;
    }

    private static IResult ToResult(SyncWriteOutcome outcome, HttpContext http) => outcome.Status switch
    {
        SyncWriteStatus.Ok => SetETagNoContent(http, outcome.Version!),
        SyncWriteStatus.PreconditionFailed => Results.StatusCode(StatusCodes.Status412PreconditionFailed),
        SyncWriteStatus.QuotaExceeded => Results.StatusCode(StatusCodes.Status507InsufficientStorage),
        SyncWriteStatus.TooLarge => Results.BadRequest(),
        _ => Results.StatusCode(500),
    };

    private static IResult SetETagNoContent(HttpContext http, string version)
    {
        http.Response.Headers.ETag = Quote(version);
        return Results.NoContent();
    }
}
```

- [ ] **Step 2: Wire in `Program.cs`** — register the store + map the routes only when enabled. After the `new FileCache(...)` registration (~line 40):
```csharp
if (config.Sync.Enabled)
    builder.Services.AddSingleton(_ => new SyncStore(
        config.Sync.ResolvedSyncDirectory, config.Sync.PerNamespaceQuotaBytes, config.Sync.MaxObjectBytes));
```
and after `app.MapFiles(prefix);` (~line 188):
```csharp
if (config.Sync.Enabled)
    app.MapSync(prefix);
```

- [ ] **Step 3: `SyncServerFactory.cs`** — mirror `TokenPluginServerFactory`, but write a `Sync`-enabled config (with a small quota so the quota test is cheap) + `EBS_SYNC_DIR`:
```csharp
// (mirror TokenPluginServerFactory exactly; the only differences below)
public const string Token = "sync-tok";
public string SyncDirectory => Path.Combine(_root, "sync");
// in the ctor, after creating dirs:
Directory.CreateDirectory(SyncDirectory);
var configPath = Path.Combine(_root, "everythingbox-server.json");
File.WriteAllText(configPath,
    "{ \"AccessToken\": \"" + Token + "\", " +
    "\"Sync\": { \"Enabled\": true, \"Directory\": " + System.Text.Json.JsonSerializer.Serialize(SyncDirectory) + ", " +
    "\"PerNamespaceQuotaBytes\": 65536, \"MaxObjectBytes\": 32768 } }");
Environment.SetEnvironmentVariable("EBS_SYNC_DIR", SyncDirectory);
// ... plus the same EBS_PLUGINS_DIR/EBS_FILES_DIR/EBS_CONFIG writes and the CapturingLoggerProvider hook.
```
Give it its own `[CollectionDefinition]` (`sync-server`, `DisableParallelization = true`), like `TokenServerCollection`.

- [ ] **Step 4: `SyncEndpointsTests.cs`** — `[Collection("sync-server")]`, driving `_factory.CreateClient()` against `/{SyncServerFactory.Token}/sync/...`:
  - **auth:** a request WITHOUT the token prefix (`/sync/p1`) → 404; with the prefix → 200.
  - **put→get round-trip:** `PutAsync(".../sync/p1/resume%2Fabc", new ByteArrayContent(bytes))` → 204 + an `ETag`; `GetAsync` → 200, body == bytes, `ETag` matches, `X-Sync-Meta` echoes what was sent.
  - **list:** after two puts, `GET /sync/p1` → JSON with both keys, sizes, versions; after a delete, the deleted key shows `deleted:true` and GET of it → 404.
  - **If-Match CAS:** put with `If-Match: "<stale>"` → 412; with the current ETag → 204 + new ETag. `If-None-Match: *` on an existing live key → 412; on a fresh key → 204.
  - **quota:** a body larger than the namespace quota (>65536 across objects) → 507; a single body over `MaxObjectBytes` (>32768) → 400.
  - **bad namespace:** `PUT /sync/..%2f/..` style or an invalid ns segment → 400 (and nothing is written outside the sync dir).
  - Use `HttpRequestMessage` to set `If-Match`/`If-None-Match`/`X-Sync-Meta` headers. Assert `HttpStatusCode` and bodies. No process/network beyond the in-memory factory.

- [ ] **Step 5: Full suites + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal` — both green (incl. `RepositoryCleanlinessTests`; existing suites unaffected — the sync routes only exist when enabled, and no existing factory enables them).
```bash
git add EverythingBox.Server/Sync/SyncEndpoints.cs EverythingBox.Server/Program.cs EverythingBox.Server.Tests/SyncServerFactory.cs EverythingBox.Server.Tests/SyncEndpointsTests.cs
git commit -m "feat: /{token}/sync object-store routes (list/get/put-if-match/delete-tombstone), enabled by config"
```

---

## Self-review

**Spec coverage:** the four routes + conditional CAS + 507 quota + tombstone (spec Increment A) → Task 2. `SyncStore` durable per-namespace files + `index.json`, hashed keys, per-namespace lock, atomic temp-then-move, quota, version stamps (spec) → Task 1. Namespace validation + containment (spec Security) → Task 1 `IsValidNamespace`/`NamespaceDir` + tests. Opt-in `SyncConfig`, routes only when enabled (spec) → Task 1 Step 1 + Task 2 Step 2. Token-prefix auth reused, no per-route check (spec) → routes mapped under `prefix`; auth test in Task 2 Step 4. No API bump (spec) → nothing touches `ServerApi`. ✅

**Placeholder scan:** none — `SyncStore` is complete code incl. the CAS/quota/containment/concurrency logic; `SyncEndpoints` is complete; the factory diffs from `TokenPluginServerFactory` are enumerated; every test lists concrete assertions.

**Type consistency:** `SyncStore(string,long,long)` + `IsValidNamespace`/`ListAsync`/`GetAsync`/`PutAsync`/`DeleteAsync` (Task 1) consumed by `SyncEndpoints` (Task 2). `SyncCondition`/`SyncConditionKind`/`SyncWriteOutcome`/`SyncWriteStatus`/`SyncObjectInfo`/`SyncObjectContent` defined in `SyncTypes.cs` (Task 1), used in both. `SyncConfig.Enabled/Directory/PerNamespaceQuotaBytes/MaxObjectBytes/ResolvedSyncDirectory` (Task 1 Step 1) read in `Program.cs` (Task 2 Step 2). Routes emit `ETag`/`X-Sync-Meta`, 204/404/412/507/400 as the spec's contract. No `ServerApi` change. ✅
