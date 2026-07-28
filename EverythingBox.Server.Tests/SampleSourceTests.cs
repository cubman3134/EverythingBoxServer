using EverythingBox.Server.Abstractions;
using EverythingBox.Server.SampleSource;

namespace EverythingBox.Server.Tests;

public class SampleSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-sample-" + Guid.NewGuid().ToString("N"));

    public SampleSourceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "The Matrix (1999).mkv"), "x");
        File.WriteAllText(Path.Combine(_root, "Sintel.mp4"), "x");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not media");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private LocalFolderSource NewSource() => new(new LocalFolderConfig { Folders = [_root] });

    [Fact]
    public async Task Lists_media_files_and_ignores_the_rest()
    {
        var catalog = await NewSource().SearchAsync("files", null, new SourceContext(), CancellationToken.None);

        var titles = catalog.Items.Select(i => i.Title).OrderBy(t => t).ToArray();
        Assert.Equal(["Sintel", "The Matrix (1999)"], titles);
    }

    [Fact]
    public async Task Filters_by_query()
    {
        var catalog = await NewSource().SearchAsync("files", "matrix", new SourceContext(), CancellationToken.None);
        Assert.Equal("The Matrix (1999)", Assert.Single(catalog.Items).Title);
    }

    [Fact]
    public async Task Resolves_to_a_proxy_path_the_host_serves()
    {
        var source = NewSource();
        var item = (await source.SearchAsync("files", "sintel", new SourceContext(), CancellationToken.None)).Items.Single();

        var stream = await source.ResolveAsync(item.Id, 0, new SourceContext(), CancellationToken.None);

        Assert.NotNull(stream);
        Assert.StartsWith("proxy/local/", stream!.Url);
        Assert.Equal("video/mp4", stream.Mime);
    }

    [Fact]
    public async Task Opens_the_file_for_the_host_to_relay()
    {
        var source = NewSource();
        var item = (await source.SearchAsync("files", "sintel", new SourceContext(), CancellationToken.None)).Items.Single();

        await using var proxy = await source.OpenAsync(item.Id, null, CancellationToken.None);

        Assert.NotNull(proxy);
        Assert.Equal(1, proxy!.ContentLength);
    }

    [Fact]
    public async Task Refuses_to_open_a_file_outside_a_configured_folder()
    {
        // A real, readable file — so this fails for the right reason (outside the
        // configured roots) rather than because the path does not exist.
        var elsewhere = Path.Combine(Path.GetTempPath(), "ebs-outside-" + Guid.NewGuid().ToString("N") + ".mkv");
        await File.WriteAllTextAsync(elsewhere, "x");
        try
        {
            var outside = LocalFolderSource.EncodeId(elsewhere);
            Assert.Null(await NewSource().OpenAsync(outside, null, CancellationToken.None));
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }

    [Fact]
    public async Task An_unconfigured_source_is_empty_rather_than_broken()
    {
        var source = new LocalFolderSource(new LocalFolderConfig());
        var catalog = await source.SearchAsync("files", null, new SourceContext(), CancellationToken.None);
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public async Task Refuses_to_open_a_file_reached_through_a_junction_that_escapes_the_configured_folder()
    {
        // Directory junctions on Windows do not require elevation (unlike file symlinks), so this
        // test can run in ordinary CI. There is no equivalent unprivileged repro on non-Windows
        // filesystems here, so skip rather than fail elsewhere.
        if (!OperatingSystem.IsWindows()) return;

        var outsideDir = Path.Combine(Path.GetTempPath(), "ebs-junction-target-" + Guid.NewGuid().ToString("N"));
        var linkPath = Path.Combine(_root, "link-out");
        Directory.CreateDirectory(outsideDir);
        var secretFile = Path.Combine(outsideDir, "secret.mkv");
        await File.WriteAllTextAsync(secretFile, "SECRET-DATA");

        try
        {
            var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{linkPath}\" \"{outsideDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            mklink!.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);

            var throughJunction = LocalFolderSource.EncodeId(Path.Combine(linkPath, "secret.mkv"));

            // Dispose defensively: if this regresses and a stream comes back, an undisposed
            // FileStream would hold secret.mkv open and make the outsideDir cleanup below fail
            // silently, leaving a scratch directory behind outside the system temp cleanup.
            await using var proxy = await NewSource().OpenAsync(throughJunction, null, CancellationToken.None);
            Assert.Null(proxy);
        }
        finally
        {
            // Remove the junction itself (not its target) so the outside directory's contents
            // survive for cleanup, then delete both.
            try { Directory.Delete(linkPath); } catch { /* best effort */ }
            try { Directory.Delete(outsideDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_configured_folder_with_a_trailing_separator_still_serves_its_files()
    {
        var withTrailingSeparator = _root + Path.DirectorySeparatorChar;
        var source = new LocalFolderSource(new LocalFolderConfig { Folders = [withTrailingSeparator] });

        var catalog = await source.SearchAsync("files", "sintel", new SourceContext(), CancellationToken.None);
        var item = Assert.Single(catalog.Items);

        await using var proxy = await source.OpenAsync(item.Id, null, CancellationToken.None);
        Assert.NotNull(proxy);
        Assert.Equal(1, proxy!.ContentLength);
    }
}
