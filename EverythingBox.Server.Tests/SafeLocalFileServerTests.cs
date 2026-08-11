using System.Diagnostics;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// Exercises the containment + serving surface of <see cref="SafeLocalFileServer"/> directly — the
/// security-critical code extracted from the local-library plugin. Every id that arrives is treated
/// as hostile: it is decoded, real-resolved through any reparse point, and confirmed to live inside a
/// configured root before a single byte is served.
/// </summary>
public class SafeLocalFileServerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ebs-safe-" + Guid.NewGuid().ToString("N"));

    public SafeLocalFileServerTests() => Directory.CreateDirectory(_base);

    public void Dispose() { try { Directory.Delete(_base, true); } catch { } GC.SuppressFinalize(this); }

    // A distinctive MIME map so a served response proves the CTOR function was used, not a hardcoded one.
    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    private SafeLocalFileServer Server(params string[] roots) => new(roots, Mime);

    private string NewDir(string name)
    {
        var d = Path.Combine(_base, name);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void EncodeId_and_TryDecodeId_round_trip()
    {
        var path = Path.Combine(_base, "Some Movie (2019).mkv");
        var id = SafeLocalFileServer.EncodeId(path);
        Assert.Equal(path, SafeLocalFileServer.TryDecodeId(id));
        // Base64url: no '+', '/', or '=' padding survives.
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('=', id);
    }

    [Fact]
    public void TryDecodeId_returns_null_for_garbage()
        => Assert.Null(SafeLocalFileServer.TryDecodeId("!!!not base64!!!"));

    [Fact]
    public async Task An_id_outside_the_roots_resolves_to_nothing()
    {
        var root = NewDir("root");
        var outside = Path.Combine(_base, "outside.mkv");
        File.WriteAllBytes(outside, [9]);

        var server = Server(root);
        var id = SafeLocalFileServer.EncodeId(outside);

        Assert.Null(server.ResolveSafeFile(id));
        Assert.Null(await server.OpenAsync(id, null, default));
    }

    [Fact]
    public async Task A_file_directly_in_a_root_serves()
    {
        var root = NewDir("root");
        var file = Path.Combine(root, "movie.mkv");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var server = Server(root);
        var id = SafeLocalFileServer.EncodeId(file);

        Assert.Equal(Path.GetFullPath(file), server.ResolveSafeFile(id));
        await using var r = await server.OpenAsync(id, null, default);
        Assert.NotNull(r);
        Assert.Equal(200, r!.StatusCode);
    }

    [Fact]
    public void A_root_prefixed_sibling_directory_is_not_contained()
    {
        // "root" is configured; "root-Secret" merely shares a lexical prefix. The trailing-separator
        // boundary means a file under the sibling must NOT be treated as inside the configured root.
        var root = NewDir("root");
        var sibling = NewDir("root-Secret");
        var file = Path.Combine(sibling, "leak.mkv");
        File.WriteAllBytes(file, [9]);

        var server = Server(root);
        var id = SafeLocalFileServer.EncodeId(file);

        Assert.False(server.IsContained(Path.GetFullPath(file)));
        Assert.Null(server.ResolveSafeFile(id));
    }

    [Fact]
    public async Task A_junction_inside_a_root_pointing_outside_does_not_leak()
    {
        var root = NewDir("root");
        var outside = NewDir("outside");
        var secret = Path.Combine(outside, "secret.mkv");
        File.WriteAllBytes(secret, [1, 2, 3, 4]);

        var link = Path.Combine(root, "link");
        MakeDirectoryLink(link, outside);

        // The file is reachable THROUGH the junction (lexically under root), but really lives outside.
        var throughLink = Path.Combine(link, "secret.mkv");
        Assert.True(File.Exists(throughLink)); // sanity: the link resolves to the real file

        var server = Server(root);
        var id = SafeLocalFileServer.EncodeId(throughLink);

        Assert.Null(server.ResolveSafeFile(id));
        Assert.Null(await server.OpenAsync(id, null, default));
    }

    [Fact]
    public void ResolveSafeDir_accepts_a_contained_subfolder_and_rejects_a_root_a_foreign_dir_and_a_file()
    {
        var root = NewDir("root");
        var show = Path.Combine(root, "Show");
        Directory.CreateDirectory(show);
        var foreign = NewDir("foreign");
        var file = Path.Combine(root, "movie.mkv");
        File.WriteAllBytes(file, [1]);

        var server = Server(root);

        Assert.Equal(Path.GetFullPath(show), server.ResolveSafeDir(SafeLocalFileServer.EncodeId(show)));
        // The root itself is never a strict subfolder — rejected.
        Assert.Null(server.ResolveSafeDir(SafeLocalFileServer.EncodeId(root)));
        // A directory outside every root — rejected.
        Assert.Null(server.ResolveSafeDir(SafeLocalFileServer.EncodeId(foreign)));
        // A file id is not a directory — rejected.
        Assert.Null(server.ResolveSafeDir(SafeLocalFileServer.EncodeId(file)));
    }

    [Fact]
    public async Task OpenAsync_serves_206_with_the_correct_slice_and_content_range()
    {
        var root = NewDir("root");
        var file = Path.Combine(root, "movie.mkv");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var server = Server(root);
        await using var r = await server.OpenAsync(SafeLocalFileServer.EncodeId(file), "bytes=1-2", default);
        Assert.NotNull(r);
        Assert.Equal(206, r!.StatusCode);
        Assert.Equal(2, r.ContentLength);
        Assert.Equal("bytes 1-2/4", r.ContentRange);
        Assert.Equal("bytes", r.AcceptRanges);
        using var sink = new MemoryStream();
        await r.Body.CopyToAsync(sink);
        Assert.Equal(new byte[] { 2, 3 }, sink.ToArray());
    }

    [Fact]
    public async Task OpenAsync_serves_200_full_with_the_ctor_mime()
    {
        var root = NewDir("root");
        var file = Path.Combine(root, "poster.png");
        File.WriteAllBytes(file, [7, 7, 7]);

        var server = Server(root);
        await using var r = await server.OpenAsync(SafeLocalFileServer.EncodeId(file), null, default);
        Assert.NotNull(r);
        Assert.Equal(200, r!.StatusCode);
        Assert.Equal(3, r.ContentLength);
        Assert.Equal("image/png", r.ContentType); // came from the ctor's MIME function
        using var sink = new MemoryStream();
        await r.Body.CopyToAsync(sink);
        Assert.Equal(new byte[] { 7, 7, 7 }, sink.ToArray());
    }

    [Fact]
    public async Task OpenAsync_returns_416_for_an_unsatisfiable_range()
    {
        var root = NewDir("root");
        var file = Path.Combine(root, "movie.mkv");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var server = Server(root);
        await using var r = await server.OpenAsync(SafeLocalFileServer.EncodeId(file), "bytes=100-200", default);
        Assert.NotNull(r);
        Assert.Equal(416, r!.StatusCode);
        Assert.Equal("bytes */4", r.ContentRange);
        Assert.Equal(0, r.ContentLength);
    }

    /// <summary>Creates a directory reparse point at <paramref name="link"/> pointing at
    /// <paramref name="target"/>. On Windows a junction (mklink /J) needs no elevation; elsewhere a
    /// directory symlink is used.</summary>
    private static void MakeDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"mklink /J failed: {p.StandardError.ReadToEnd()}");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}
