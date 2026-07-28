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
}
