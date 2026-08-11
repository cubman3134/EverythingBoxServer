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
