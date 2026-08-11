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
