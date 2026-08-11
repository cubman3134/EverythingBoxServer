# Self-Download Idle Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Abandon a self-download that receives no new bytes for `IdleTimeoutSeconds`, degrading to the caching notice earlier than the total timeout — host-only, no contract change.

**Architecture:** A pure `IdleWatchdog.ShouldGiveUp(bytes, nowTick)` makes the stall decision. `ReleaseStreamResolver.RunDownloadAsync` drives it from the downloader's existing progress reports via a synchronous `InlineProgress`, cancelling an idle token linked alongside the total-timeout and caller tokens. `MonoTorrentDownloader` and the `ITorrentDownloader` contract are untouched.

**Tech Stack:** .NET 9 / C#, xUnit. `EverythingBox.Server` (host; has `InternalsVisibleTo("EverythingBox.Server.Tests")`). No Abstractions/contract change.

## Global Constraints

- **NO contract change, NO API bump.** `IdleTimeoutSeconds` is host config on `ServerConfig.Download`; the logic is host-only. Do not touch `ServerApi.VersionString`, `ITorrentDownloader`, or `MonoTorrentDownloader`.
- **Additive:** `IdleTimeoutSeconds` defaults to 120 but self-download is off by default; `0` disables idle detection → `progress: null` → behavior byte-identical to today.
- **PUBLIC repo — no content-source name anywhere** (code, comments, paths, tests, commit message). `RepositoryCleanlinessTests` must stay green.
- The total `TimeoutSeconds` remains the hard ceiling; idle detection only ends a download *earlier*.
- Stage files by explicit path (never `git add -A`). No AI attribution in any commit.
- No test spawns a process, touches the network, or reads a real browser profile.
- Run tests per-project — this CLI rejects two projects in one `dotnet test` call (MSB1008).

---

### Task 1: `IdleWatchdog` — the pure stall decision

**Files:**
- Create: `EverythingBox.Server/Download/IdleWatchdog.cs`
- Test: `EverythingBox.Server.Tests/IdleWatchdogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal sealed class IdleWatchdog(long idleMilliseconds)` with `bool ShouldGiveUp(long bytesDownloaded, long nowTick)`.

- [ ] **Step 1: Write the failing tests**

Create `EverythingBox.Server.Tests/IdleWatchdogTests.cs`:

```csharp
using EverythingBox.Server.Download;

namespace EverythingBox.Server.Tests;

public class IdleWatchdogTests
{
    [Fact]
    public void The_first_call_primes_the_clock_and_never_trips()
    {
        var w = new IdleWatchdog(1000);
        Assert.False(w.ShouldGiveUp(0, 5000)); // first call primes even far past any window
    }

    [Fact]
    public void No_advance_for_the_window_trips()
    {
        var w = new IdleWatchdog(1000);
        Assert.False(w.ShouldGiveUp(100, 0));    // prime at bytes=100, tick=0
        Assert.False(w.ShouldGiveUp(100, 999));  // no advance, under the window
        Assert.True(w.ShouldGiveUp(100, 1000));  // no advance, window elapsed → trip
    }

    [Fact]
    public void An_advance_resets_the_clock()
    {
        var w = new IdleWatchdog(1000);
        Assert.False(w.ShouldGiveUp(100, 0));     // prime
        Assert.False(w.ShouldGiveUp(100, 900));   // no advance, under window
        Assert.False(w.ShouldGiveUp(200, 1500));  // advanced → resets, even though 1500-0 > window
        Assert.False(w.ShouldGiveUp(200, 2499));  // no advance, under window measured from 1500
        Assert.True(w.ShouldGiveUp(200, 2500));   // no advance since 1500, window elapsed → trip
    }

    [Fact]
    public void A_fresh_advance_after_it_would_trip_resets_again()
    {
        var w = new IdleWatchdog(1000);
        Assert.False(w.ShouldGiveUp(0, 0));     // prime
        Assert.True(w.ShouldGiveUp(0, 1000));   // idle → trip
        Assert.False(w.ShouldGiveUp(50, 1200)); // advanced → clock resets, no longer tripping
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~IdleWatchdog" -v minimal`
Expected: FAIL — build error, `IdleWatchdog` does not exist.

- [ ] **Step 3: Implement `IdleWatchdog`**

Create `EverythingBox.Server/Download/IdleWatchdog.cs`:

```csharp
namespace EverythingBox.Server.Download;

/// <summary>
/// Decides when a download has stalled. Fed the running byte count on each progress report, it
/// reports "give up" once no advance has happened for the idle window. Single-threaded: call it
/// only from the (serial) progress-report callback.
/// </summary>
internal sealed class IdleWatchdog(long idleMilliseconds)
{
    private long _lastBytes = -1;
    private long _lastAdvanceTick;
    private bool _started;

    /// <summary>True once no byte-advance has occurred for the idle window. The first call primes
    /// the clock and never trips; each advance resets it.</summary>
    /// <param name="bytesDownloaded">The running total of bytes received so far.</param>
    /// <param name="nowTick">A monotonic millisecond tick (e.g. <c>Environment.TickCount64</c>).</param>
    public bool ShouldGiveUp(long bytesDownloaded, long nowTick)
    {
        if (!_started)
        {
            _started = true;
            _lastBytes = bytesDownloaded;
            _lastAdvanceTick = nowTick;
            return false;
        }

        if (bytesDownloaded > _lastBytes)
        {
            _lastBytes = bytesDownloaded;
            _lastAdvanceTick = nowTick;
            return false;
        }

        return nowTick - _lastAdvanceTick >= idleMilliseconds;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~IdleWatchdog" -v minimal`
Expected: PASS — all four green.

- [ ] **Step 5: Commit**

```bash
git add EverythingBox.Server/Download/IdleWatchdog.cs EverythingBox.Server.Tests/IdleWatchdogTests.cs
git commit -m "feat: add an idle-watchdog for stalled self-downloads"
```

---

### Task 2: `IdleTimeoutSeconds` config + wire idle detection into `RunDownloadAsync`

**Files:**
- Modify: `EverythingBox.Server/ServerConfig.cs` (add `DownloadConfig.IdleTimeoutSeconds`)
- Modify: `EverythingBox.Server/Sources/ReleaseStreamResolver.cs` (`RunDownloadAsync` idle wiring + `InlineProgress`)
- Test: `EverythingBox.Server.Tests/ServerConfigTests.cs` (default + bind)

**Interfaces:**
- Consumes: `IdleWatchdog` (Task 1).
- Produces: `DownloadConfig.IdleTimeoutSeconds` (int, default 120).

- [ ] **Step 1: Write the failing config test**

Append to the `ServerConfigTests` class in `EverythingBox.Server.Tests/ServerConfigTests.cs`:

```csharp
    [Fact]
    public void Download_has_an_idle_timeout_by_default_and_it_binds()
    {
        Assert.True(new ServerConfig().Download.IdleTimeoutSeconds > 0);

        var config = ServerConfig.FromJson("""{ "Download": { "IdleTimeoutSeconds": 45 } }""");
        Assert.Equal(45, config.Download.IdleTimeoutSeconds);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~idle_timeout_by_default" -v minimal`
Expected: FAIL — build error, `DownloadConfig` has no `IdleTimeoutSeconds`.

- [ ] **Step 3: Add the config field**

In `EverythingBox.Server/ServerConfig.cs`, add to `DownloadConfig` (after `TimeoutSeconds`):

```csharp
    /// <summary>Give up on a self-download that receives no new bytes for this many seconds — a
    /// faster early-out than <see cref="TimeoutSeconds"/> for a dead/seedless swarm. The total
    /// <see cref="TimeoutSeconds"/> still applies as the hard ceiling. 0 disables idle detection
    /// (only the total timeout applies).</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;
```

- [ ] **Step 4: Run the config test to verify it passes**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~idle_timeout_by_default" -v minimal`
Expected: PASS.

- [ ] **Step 5: Wire idle detection into `RunDownloadAsync`**

In `EverythingBox.Server/Sources/ReleaseStreamResolver.cs`, replace the body of `RunDownloadAsync` (it currently creates one `timeoutCts` + `linked`, calls `DownloadAsync`, then does the EBS#20 `KeepVerifiedAsync` + all-reject cleanup, and catches `OperationCanceledException` for the timeout). Keep the verification logic exactly; add the idle CTS + watchdog + distinguished log:

```csharp
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_download.TimeoutSeconds));
        using var idleCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token, idleCts.Token);

        var directory = DownloadDirectory(files, release, request);

        IProgress<TorrentDownloadProgress>? watchdog = null;
        if (_download.IdleTimeoutSeconds > 0)
        {
            var idle = new IdleWatchdog(_download.IdleTimeoutSeconds * 1000L);
            watchdog = new InlineProgress(p =>
            {
                if (idle.ShouldGiveUp(p.BytesDownloaded, Environment.TickCount64))
                    idleCts.Cancel();
            });
        }

        try
        {
            var paths = await downloader.DownloadAsync(
                release, request, directory, progress: watchdog,
                maxTotalBytes: _download.MaxSizeMB * 1024L * 1024L,
                cancellationToken: linked.Token).ConfigureAwait(false);

            var verified = await KeepVerifiedAsync(release, paths, linked.Token).ConfigureAwait(false);
            if (paths.Count > 0 && verified.Count == 0)
                RemoveDownloadDirectory(directory);
            return verified;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (idleCts.IsCancellationRequested)
                _logger.LogInformation(
                    "Self-download of '{Title}' received no new data for {Seconds}s; falling back to the caching notice.",
                    release.Title, _download.IdleTimeoutSeconds);
            else
                _logger.LogInformation(
                    "Self-download of '{Title}' timed out after {Seconds}s; falling back to the caching notice.",
                    release.Title, _download.TimeoutSeconds);
            return [];
        }
```

NOTE: this preserves the EBS#20 `KeepVerifiedAsync`/`RemoveDownloadDirectory(directory)` lines already present in the method — only the token setup, the `progress:` argument, and the catch's log are changed. If the current method body differs from the pre-idle version shown above (aside from those additions), reconcile by ADDING the idle pieces to the existing body rather than deleting verification logic. Add `using EverythingBox.Server.Download;` at the top of the file if `IdleWatchdog` isn't already in scope (it likely is — `MonoTorrentDownloader` types are used here — only add if the build complains).

Add the synchronous progress adapter as a private nested type on `ReleaseStreamResolver` (place it near the other private helpers):

```csharp
    /// <summary>Runs the callback synchronously on the reporting thread, unlike
    /// <see cref="System.Progress{T}"/> which posts to a captured synchronization context — we need
    /// the cancel to take effect before the downloader's next cancellation check.</summary>
    private sealed class InlineProgress(Action<TorrentDownloadProgress> onReport)
        : IProgress<TorrentDownloadProgress>
    {
        public void Report(TorrentDownloadProgress value) => onReport(value);
    }
```

- [ ] **Step 6: Run the full engine suites**

Run: `dotnet test EverythingBox.Server.Tests -v minimal`
Then: `dotnet test EverythingBox.Server.Core.Tests -v minimal`
Expected: both PASS — including `RepositoryCleanlinessTests`, `UncachedFallbackTests` (which set no idle-relevant state and must still serve), and the new tests. No version-pin test changes (no API bump).

- [ ] **Step 7: Commit**

```bash
git add EverythingBox.Server/ServerConfig.cs EverythingBox.Server/Sources/ReleaseStreamResolver.cs EverythingBox.Server.Tests/ServerConfigTests.cs
git commit -m "feat: abandon a self-download that stalls, via a configurable idle timeout"
```

---

## Self-review

**Spec coverage:**
- `DownloadConfig.IdleTimeoutSeconds` default 120, 0 disables (spec §1 + Semantics) → Task 2 Step 3. ✅
- `IdleWatchdog.ShouldGiveUp` pure decision (spec §2) → Task 1. ✅
- `RunDownloadAsync` idle CTS + `InlineProgress` watchdog + distinguished log, preserving verification (spec §3) → Task 2 Step 5. ✅
- Total timeout stays the hard ceiling; idle only ends earlier; both degrade to notice (spec Semantics) → the linked token carries both; catch unchanged except log. ✅
- `IdleTimeoutSeconds = 0` → `progress: null` → today's behavior (spec Semantics) → the `if (_download.IdleTimeoutSeconds > 0)` guard leaves `watchdog` null. ✅
- No API/contract change; `MonoTorrentDownloader`/`ITorrentDownloader` untouched (spec What binds) → no task touches them. ✅
- Testing: `IdleWatchdog` unit tests + config default/bind (spec Testing) → Task 1 Step 1, Task 2 Step 1. ✅

**Placeholder scan:** none — every code step shows complete code; tick sequences are concrete.

**Type consistency:** `IdleWatchdog(long idleMilliseconds)` + `ShouldGiveUp(long, long)` defined in Task 1, constructed as `new IdleWatchdog(_download.IdleTimeoutSeconds * 1000L)` and called `ShouldGiveUp(p.BytesDownloaded, Environment.TickCount64)` in Task 2 Step 5 — matches. `InlineProgress(Action<TorrentDownloadProgress>)` implements `IProgress<TorrentDownloadProgress>`, passed as `progress: watchdog` (typed `IProgress<TorrentDownloadProgress>?`) — matches the `DownloadAsync` signature. `IdleTimeoutSeconds` is `int` in the field and used as `* 1000L` (long) and `> 0` consistently.
