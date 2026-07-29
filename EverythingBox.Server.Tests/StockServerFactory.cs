using Microsoft.AspNetCore.Mvc.Testing;

namespace EverythingBox.Server.Tests;

/// <summary>
/// Boots the real host with the project's stated out-of-the-box shape: no indexer, no debrid,
/// no plugin — <c>Indexers: []</c>, same as a fresh <c>everythingbox-server.json</c>. Proves the
/// "no catalog is advertised that nothing can fill, but the manifest and unadvertised catalog
/// routes still work" property by assertion rather than by inspection (see
/// <see cref="StockServerTests"/>).
/// <para>
/// Unlike <see cref="SearchServerFactory"/>, this fixture needs no fake <see cref="HttpClient"/>
/// handler: with zero configured indexers, <c>IndexerSearchSource</c> declares no catalogs at
/// all, so a request for one never even reaches the grabber, let alone makes an HTTP call.
/// </para>
/// <para>
/// Writes the same process-wide <c>EBS_*</c> environment variables every other fixture in
/// <see cref="AddonServerCollection"/> does, so <see cref="StockServerTests"/> joins that
/// collection too — even though it constructs its own instance per test rather than taking the
/// collection's shared one, since a stock (no-indexer) config is a different config than
/// <see cref="SearchServerFactory"/>'s and the two must never share a host instance.
/// </para>
/// </summary>
public sealed class StockServerFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-stock-host-" + Guid.NewGuid().ToString("N"));

    public string FilesDirectory => Path.Combine(_root, "files");

    public StockServerFactory()
    {
        Directory.CreateDirectory(FilesDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath, """{ "Indexers": [] }""");

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", Path.Combine(_root, "plugins"));
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);

        // Forces Program.cs to read these env vars right now, before any other fixture in
        // AddonServerCollection (which this class joins — see the class doc comment) gets a
        // chance to overwrite the same process-wide names with its own paths.
        CreateClient().Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
