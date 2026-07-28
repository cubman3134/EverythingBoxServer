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

            // Dispose defensively: if this guard ever regresses and a stream comes back, an
            // undisposed FileStream would hold `elsewhere` open, and the File.Delete below would
            // throw — masking the real assertion failure instead of reporting it.
            await using var proxy = await NewSource().OpenAsync(outside, null, CancellationToken.None);
            Assert.Null(proxy);
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

    [Fact]
    public async Task An_id_decoding_to_an_empty_path_returns_null_rather_than_throwing()
    {
        // "" is valid base64url payload (decodes cleanly) but Path.GetFullPath("") throws
        // ArgumentException. Every other malformed-id case returns null; this one must too.
        var empty = LocalFolderSource.EncodeId("");
        Assert.Null(await NewSource().OpenAsync(empty, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_id_decoding_to_a_path_with_an_embedded_NUL_returns_null_rather_than_throwing()
    {
        // Decodes fine as base64/UTF-8, but Path.GetFullPath rejects the embedded NUL with
        // ArgumentException ("Null character in path"). Same requirement: return null, don't throw.
        var withNul = LocalFolderSource.EncodeId("foo\0bar.mkv");
        Assert.Null(await NewSource().OpenAsync(withNul, null, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_does_not_list_a_file_reached_through_a_junction_that_escapes_the_configured_folder()
    {
        // Same unprivileged-on-Windows-only rationale as the OpenAsync junction test above.
        if (!OperatingSystem.IsWindows()) return;

        var outsideDir = Path.Combine(Path.GetTempPath(), "ebs-junction-target-" + Guid.NewGuid().ToString("N"));
        var linkPath = Path.Combine(_root, "link-out-search");
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

            var catalog = await NewSource().SearchAsync("files", null, new SourceContext(), CancellationToken.None);

            Assert.DoesNotContain(catalog.Items, i => i.Title == "secret");
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
    public async Task A_junction_cycle_does_not_crash_the_catalog()
    {
        // Same unprivileged-on-Windows-only rationale as the other junction tests.
        if (!OperatingSystem.IsWindows()) return;

        // A junction inside the configured folder pointing back at one of its own ancestors (the
        // folder itself) makes a naive recursive walk descend into itself forever. The old
        // hand-rolled walk followed it until it hit Windows' path-length ceiling and threw an
        // unhandled FileNotFoundException out of SearchAsync, killing the listing for every
        // configured folder, not just this one.
        var cycleLink = Path.Combine(_root, "cycle");

        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{cycleLink}\" \"{_root}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        mklink!.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);

        try
        {
            var catalog = await NewSource().SearchAsync("files", null, new SourceContext(), CancellationToken.None);

            var titles = catalog.Items.Select(i => i.Title).OrderBy(t => t).ToArray();
            Assert.Equal(["Sintel", "The Matrix (1999)"], titles);
        }
        finally
        {
            try { Directory.Delete(cycleLink); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task An_unreadable_subdirectory_does_not_empty_the_rest_of_the_listing()
    {
        // icacls and the deny ACE below are Windows-specific.
        if (!OperatingSystem.IsWindows()) return;

        var lockedDir = Path.Combine(_root, "locked");
        Directory.CreateDirectory(lockedDir);
        await File.WriteAllTextAsync(Path.Combine(lockedDir, "Hidden.mp4"), "x");

        var siblingDir = Path.Combine(_root, "sibling");
        Directory.CreateDirectory(siblingDir);
        await File.WriteAllTextAsync(Path.Combine(siblingDir, "Sibling.mp4"), "x");

        var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";

        var deny = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe",
            $"\"{lockedDir}\" /deny \"{currentUser}:(OI)(CI)(RX)\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        deny!.WaitForExit();
        Assert.Equal(0, deny.ExitCode);

        try
        {
            // The locked subdirectory must be skipped, not treated as a reason to abandon the
            // whole configured folder: its readable sibling directory and the folder's own
            // top-level files must still show up.
            var catalog = await NewSource().SearchAsync("files", null, new SourceContext(), CancellationToken.None);
            var titles = catalog.Items.Select(i => i.Title).OrderBy(t => t).ToArray();

            Assert.Equal(["Sibling", "Sintel", "The Matrix (1999)"], titles);
        }
        finally
        {
            // Restore access before Dispose() tries to recursively delete _root — otherwise the
            // deny ACE survives on disk as an undeletable leftover directory.
            var restore = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe",
                $"\"{lockedDir}\" /remove:d \"{currentUser}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            restore!.WaitForExit();
        }
    }

    [Fact]
    public async Task A_configured_root_that_is_itself_a_junction_still_lists_and_opens_its_files()
    {
        // Same unprivileged-on-Windows-only rationale as the other junction tests.
        if (!OperatingSystem.IsWindows()) return;

        // AttributesToSkip on EnumerationOptions applies to entries found DURING enumeration, not
        // to the root passed to EnumerateFiles — a configured folder that is itself a reparse
        // point (a legitimate setup: e.g. a drive-letter junction to another volume) must still
        // work, not be silently skipped.
        var realDir = Path.Combine(Path.GetTempPath(), "ebs-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realDir);
        await File.WriteAllTextAsync(Path.Combine(realDir, "Real.mp4"), "x");

        var junctionRoot = Path.Combine(Path.GetTempPath(), "ebs-root-junction-" + Guid.NewGuid().ToString("N"));

        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{junctionRoot}\" \"{realDir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        mklink!.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);

        try
        {
            var source = new LocalFolderSource(new LocalFolderConfig { Folders = [junctionRoot] });
            var catalog = await source.SearchAsync("files", null, new SourceContext(), CancellationToken.None);
            var item = Assert.Single(catalog.Items);
            Assert.Equal("Real", item.Title);

            await using var proxy = await source.OpenAsync(item.Id, null, CancellationToken.None);
            Assert.NotNull(proxy);
            Assert.Equal(1, proxy!.ContentLength);
        }
        finally
        {
            try { Directory.Delete(junctionRoot); } catch { /* best effort */ }
            try { Directory.Delete(realDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_media_file_marked_Hidden_is_still_listed()
    {
        // Files can pick up the Hidden attribute from a download, NAS, or sync tool without the
        // owner's intent. A media server hiding such a file with no indication is worse than
        // listing one they didn't deliberately mark hidden — so SearchAsync lists it anyway,
        // and AttributesToSkip is set to only ReparsePoint, not Hidden | System.
        var hiddenFile = Path.Combine(_root, "HiddenMovie.mp4");
        await File.WriteAllTextAsync(hiddenFile, "x");
        var info = new FileInfo(hiddenFile);
        info.Attributes |= FileAttributes.Hidden;

        var catalog = await NewSource().SearchAsync("files", null, new SourceContext(), CancellationToken.None);

        var hidden = Assert.Single(catalog.Items, i => i.Title == "HiddenMovie");
        Assert.NotNull(hidden);
    }
}
