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
/// </summary>
public sealed class PluginServerFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-host-" + Guid.NewGuid().ToString("N"));

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");

    public PluginServerFactory()
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "testplugins", "good");
        var dest = Path.Combine(PluginsDirectory, "good");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(staged))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));

        Directory.CreateDirectory(FilesDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath, "{}");

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);
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
