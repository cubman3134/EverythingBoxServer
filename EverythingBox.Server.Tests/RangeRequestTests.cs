using EverythingBox.Server.LocalLibrary;

namespace EverythingBox.Server.Tests;

public class RangeRequestTests
{
    [Fact] public void No_header_is_Full()            => AssertFull(RangeRequest.Parse(null, 5000));
    [Fact] public void Empty_header_is_Full()         => AssertFull(RangeRequest.Parse("", 5000));
    [Fact] public void Wrong_unit_is_Full()           => AssertFull(RangeRequest.Parse("items=0-1", 5000));
    [Fact] public void Malformed_is_Full()            => AssertFull(RangeRequest.Parse("bytes=abc", 5000));
    [Fact] public void Multi_range_is_Full()          => AssertFull(RangeRequest.Parse("bytes=0-1,2-3", 5000));
    [Fact] public void Start_after_end_is_Full()      => AssertFull(RangeRequest.Parse("bytes=100-50", 5000));
    [Fact] public void Zero_total_is_Full()           => AssertFull(RangeRequest.Parse("bytes=0-10", 0));

    [Fact]
    public void Closed_range_is_Partial()
    {
        var r = RangeRequest.Parse("bytes=0-1023", 5000);
        Assert.Equal(RangeKind.Partial, r.Kind); Assert.Equal(0, r.Start); Assert.Equal(1024, r.Length);
    }

    [Fact]
    public void Open_ended_range_runs_to_EOF()
    {
        var r = RangeRequest.Parse("bytes=1024-", 5000);
        Assert.Equal(RangeKind.Partial, r.Kind); Assert.Equal(1024, r.Start); Assert.Equal(3976, r.Length);
    }

    [Fact]
    public void Suffix_range_is_the_last_N_bytes()
    {
        var r = RangeRequest.Parse("bytes=-500", 5000);
        Assert.Equal(RangeKind.Partial, r.Kind); Assert.Equal(4500, r.Start); Assert.Equal(500, r.Length);
    }

    [Fact]
    public void Closed_end_past_EOF_is_clamped()
    {
        var r = RangeRequest.Parse("bytes=4999-999999", 5000);
        Assert.Equal(RangeKind.Partial, r.Kind); Assert.Equal(4999, r.Start); Assert.Equal(1, r.Length);
    }

    [Fact] public void Start_at_total_is_Unsatisfiable()  => Assert.Equal(RangeKind.Unsatisfiable, RangeRequest.Parse("bytes=5000-", 5000).Kind);
    [Fact] public void Start_past_total_is_Unsatisfiable() => Assert.Equal(RangeKind.Unsatisfiable, RangeRequest.Parse("bytes=6000-7000", 5000).Kind);

    private static void AssertFull(RangeResult r) => Assert.Equal(RangeKind.Full, r.Kind);
}
