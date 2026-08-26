using EverythingBox.Server;
using Xunit;

namespace EverythingBox.Server.Tests;

public sealed class RomhackStagingTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "stg-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Each_fetch_gets_its_own_directory_inside_the_root()
    {
        var s = new RomhackStaging(_root, TimeSpan.FromHours(6));

        var a = s.NewFetchDirectory();
        var b = s.NewFetchDirectory();

        Assert.NotEqual(a, b);
        Assert.True(Directory.Exists(a));
        Assert.True(s.IsInsideRoot(a));
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(a));
    }

    [Fact]
    public void A_path_outside_the_root_is_not_inside_it()
    {
        var s = new RomhackStaging(_root, TimeSpan.FromHours(6));
        Assert.False(s.IsInsideRoot(Path.Combine(Path.GetTempPath(), "somewhere-else")));
        Assert.False(s.IsInsideRoot(Path.Combine(_root, "..", "escaped")));
    }

    // A prefix comparison that forgets the separator answers "inside" for a SIBLING whose name merely
    // starts with the root's — "<root>-evil" is not under "<root>". Neither case above catches that:
    // both are paths that do not share the root's prefix at all. This one exists so the separator in
    // the containment check cannot be dropped without a test going red.
    [Fact]
    public void A_sibling_whose_name_merely_starts_with_the_roots_is_not_inside_it()
    {
        var sibling = Directory.CreateDirectory(_root + "-evil").FullName;
        try
        {
            var s = new RomhackStaging(_root, TimeSpan.FromHours(6));

            Assert.False(s.IsInsideRoot(sibling));
            Assert.False(s.IsInsideRoot(Path.Combine(sibling, "rom.bin")));
        }
        finally { try { Directory.Delete(sibling, true); } catch { } }
    }

    [Fact]
    public void The_sweep_removes_only_what_is_older_than_the_retention()
    {
        var s = new RomhackStaging(_root, TimeSpan.FromHours(6));
        var old = s.NewFetchDirectory();
        var fresh = s.NewFetchDirectory();
        File.WriteAllText(Path.Combine(old, "rom.bin"), "x");
        File.WriteAllText(Path.Combine(fresh, "rom.bin"), "x");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-9));

        var removed = s.Sweep(DateTimeOffset.UtcNow);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void The_sweep_never_touches_the_root_itself()
    {
        var s = new RomhackStaging(_root, TimeSpan.FromHours(6));
        Directory.SetLastWriteTimeUtc(_root, DateTime.UtcNow.AddDays(-30));

        s.Sweep(DateTimeOffset.UtcNow);

        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void A_sweep_over_a_root_that_does_not_exist_is_not_an_error() =>
        Assert.Equal(0, new RomhackStaging(Path.Combine(_root, "gone"), TimeSpan.FromHours(6))
            .Sweep(DateTimeOffset.UtcNow));
}
