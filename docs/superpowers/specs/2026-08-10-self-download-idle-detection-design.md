# Self-download idle/stall detection (EBS#21, idle-detection scope)

**Status:** approved 2026-08-10, ready for planning.

## Goal

Abandon a self-download that stops making progress, instead of holding the request until the
coarse total timeout. Host-only; no contract change, no API bump. (EBS#21's concurrency-cap and
resume sub-features are deliberately deferred — see "Out of scope".)

## Why this is needed

`MonoTorrentDownloader.WaitForCompletionAsync` only ends on completion, `TorrentState.Error`, or
the linked cancellation token — which carries the single hard wall-clock `TimeoutSeconds` (600s
default) applied by `ReleaseStreamResolver.RunDownloadAsync`. So a dead/seedless swarm that will
never deliver a byte still holds the request for the full 600s. There is no "no progress for N
seconds → give up" detector.

## Approach — host-only, via the existing progress channel

The downloader already reports progress on every poll (~500ms) whether or not bytes advanced
(`MonoTorrentDownloader.WaitForCompletionAsync` calls `progress?.Report(...)` each iteration).
So idle detection needs **no change to the downloader and no contract change**: the resolver
passes a lightweight `IProgress<TorrentDownloadProgress>` that watches `BytesDownloaded` and
cancels an idle token when it stalls.

### 1. Config (`EverythingBox.Server/ServerConfig.cs`, host — NOT the Abstractions contract)

Add to `DownloadConfig`:
```csharp
    /// <summary>Give up on a self-download that receives no new bytes for this many seconds — a
    /// faster early-out than <see cref="TimeoutSeconds"/> for a dead/seedless swarm. The total
    /// <see cref="TimeoutSeconds"/> still applies as the hard ceiling. 0 disables idle detection
    /// (only the total timeout applies).</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;
```
This is host config (`ServerConfig.Download`), so **no `ServerApi` version bump**.

### 2. The pure, testable core: `IdleWatchdog`

New file `EverythingBox.Server/Download/IdleWatchdog.cs`:
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
    /// <param name="bytesDownloaded">The running total bytes received so far.</param>
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

### 3. Wiring in `ReleaseStreamResolver.RunDownloadAsync`

Add an idle CTS linked alongside the existing timeout + caller tokens, and a synchronous
`IProgress` that drives the watchdog. Replace the body of `RunDownloadAsync` (keeping the
EBS#20 verification/`KeepVerifiedAsync`/all-reject-cleanup logic already there):

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

Add the small synchronous progress adapter as a private nested type on `ReleaseStreamResolver`
(the built-in `System.Progress<T>` marshals to a captured `SynchronizationContext` and would run
the callback off-thread/asynchronously — we need it inline in the report call so the cancel takes
effect before the downloader's next `ThrowIfCancellationRequested`):
```csharp
    /// <summary>Runs the callback synchronously on the reporting thread, unlike
    /// <see cref="System.Progress{T}"/> which posts to a captured synchronization context.</summary>
    private sealed class InlineProgress(Action<TorrentDownloadProgress> onReport)
        : IProgress<TorrentDownloadProgress>
    {
        public void Report(TorrentDownloadProgress value) => onReport(value);
    }
```

## Semantics

- **Total `TimeoutSeconds` remains the hard ceiling.** Idle detection only ends a download
  *earlier* when it stalls; it never lets a download exceed the total timeout. Both cancel the
  same `linked` token and degrade to the caching notice.
- **`IdleTimeoutSeconds = 0` disables it** → `watchdog` stays null → `progress: null` → behavior
  byte-identical to today.
- **The idle clock starts at the first progress report** (which begins after the download starts,
  post-metadata), and every byte advance resets it — so a slow-but-progressing swarm is never
  abandoned, only a genuinely stalled one after the window.
- Idle-cancel and total-timeout both cancel the linked token, but the downloader **swallows the
  cancellation internally** (its `ITorrentDownloader` contract: "a cancelled download returns
  empty") and returns `[]` rather than throwing. So the resolver distinguishes the reason *after*
  the download returns empty, by inspecting which of `idleCts`/`timeoutCts` fired (`LogIfAbandoned`)
  — it does not rely on catching an exception. The `catch (OperationCanceledException)` block is now
  an edge case, entered only if post-download verification observes the cancel; it reuses the same
  `LogIfAbandoned` helper. A genuine caller cancellation is still detected first (via
  `cancellationToken.IsCancellationRequested`) and reported as neither idle nor timeout.

## Testing

- **`IdleWatchdog` unit tests** (new `EverythingBox.Server.Tests/IdleWatchdogTests.cs`, via the
  existing `InternalsVisibleTo`), driving `ShouldGiveUp` with an explicit `(bytes, tick)` sequence:
  - the first call primes and returns false;
  - a byte advance resets the clock and returns false, even long after the previous tick;
  - no advance for exactly/over the window returns true;
  - no advance but under the window returns false;
  - after it would trip, a fresh advance returns false again (the clock resets).
- **Config:** `DownloadConfig.IdleTimeoutSeconds` defaults to a positive value; a config with an
  explicit value binds. Extend `ServerConfigTests` (the default-values test and a bind assertion).
- The live wiring (`InlineProgress` + linked CTS in `RunDownloadAsync`) is thin and follows the
  same "swarm-facing parts aren't unit-tested" precedent as the rest of that path; the decision it
  depends on is fully covered by `IdleWatchdog` tests.
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- **No contract change, no API bump** — host config + host logic only. `ITorrentDownloader`,
  `MonoTorrentDownloader`, and `ServerApi.VersionString` are all unchanged.
- Additive: `IdleTimeoutSeconds` defaults on (120), but self-download is off by default, so only
  opt-in deployments are affected; `0` restores exactly today's behavior.
- **Cleanliness:** fully generic — no content-source name in code, comments, paths, tests, or the
  commit message. `RepositoryCleanlinessTests` must stay green.
- Composes with the rest of the path: the same `linked` token still carries EBS#5's total-size
  behavior indirectly (size cap is enforced in the downloader), EBS#19 selection, and EBS#20
  verification — idle detection only adds one more reason the token may cancel.

## Out of scope (deferred EBS#21 sub-features)

- **Concurrency cap** (a max-simultaneous-downloads semaphore) — deferred; low value on a
  fallback path.
- **Download resume** (fast-resume persistence + retaining the working directory) — deferred;
  reworks the "delete working dir after publish / one-download engine" lifecycle for marginal
  value on a fallback meant to grab small releases while the user waits.
- Any change to the total-timeout ceiling semantics, or to `MonoTorrentDownloader`.

## Done when

- A self-download that receives no new bytes for `IdleTimeoutSeconds` is abandoned and degrades to
  the caching notice, logged distinctly from a total-timeout; a progressing download is never
  abandoned early; `IdleTimeoutSeconds = 0` reproduces today's behavior.
- `IdleWatchdog` is unit-tested; `IdleTimeoutSeconds` binds and defaults positive.
- No API/contract change; both engine test projects green including `RepositoryCleanlinessTests`.
  Verified in Release.
