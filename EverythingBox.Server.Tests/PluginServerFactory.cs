using Microsoft.AspNetCore.Mvc.Testing;

namespace EverythingBox.Server.Tests;

/// <summary>
/// Boots the real host in-memory via <see cref="WebApplicationFactory{TEntryPoint}"/>, with
/// the "good" fixture plugin installed.
///
/// Env vars are set in the CONSTRUCTOR, not in an override of <c>ConfigureWebHost</c>:
/// <c>Program.cs</c> calls <see cref="ServerConfig.Load"/> and reads
/// <see cref="ServerConfig.ResolvedPluginsDirectory"/> / <see cref="ServerConfig.ResolvedFilesCacheDir"/>
/// in its top-level statements, which run the moment the host is first built — before
/// <c>ConfigureWebHost</c> would get a chance to run.
///
/// This class is shared by every test in <see cref="AddonServerCollection"/> because it sets
/// these as process-wide environment variables and <see cref="ServerConfig"/> re-reads them
/// live rather than snapshotting; see that collection's doc comment for why.
///
/// <see cref="AddonServerCollection"/> now also provides <see cref="SearchServerFactory"/> as a
/// second <c>ICollectionFixture</c>. xUnit constructs every fixture registered on a collection
/// before any test in that collection runs, so without the eager <see cref="CreateClient"/> call
/// at the end of this constructor, whichever of the two factories is constructed SECOND would
/// silently overwrite these env vars before THIS host's lazily-built <c>Program.cs</c> ever reads
/// them — this factory's own host would then boot against the other one's config. Forcing the
/// host to build here, immediately after these env vars are set and before the other fixture's
/// constructor can run, makes each factory's config read atomic with its own env-var write.
/// </summary>
public sealed class PluginServerFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-host-" + Guid.NewGuid().ToString("N"));

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");

    public PluginServerFactory()
    {
        StagePlugin("good");
        // A source that throws on every request-time member — Catalogs, SearchAsync,
        // DetailAsync, ResolveAsync, OpenAsync. Installed alongside "good" (never alone)
        // so HTTP-level tests can prove containment (F1): the "good" source's routes must
        // keep working, and /manifest.json must still list "good"'s catalog, even though
        // this source blows up on everything it touches.
        StagePlugin("throwing");

        Directory.CreateDirectory(FilesDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath, "{}");

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);

        // See the class doc comment: forces Program.cs to read these env vars NOW, before a
        // sibling collection fixture's constructor can overwrite them.
        CreateClient().Dispose();
    }

    private void StagePlugin(string fixtureName)
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "testplugins", fixtureName);
        var dest = Path.Combine(PluginsDirectory, fixtureName);
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(staged))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
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
