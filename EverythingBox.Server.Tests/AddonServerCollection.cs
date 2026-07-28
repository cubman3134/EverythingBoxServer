namespace EverythingBox.Server.Tests;

/// <summary>
/// Shared, non-parallel xUnit collection for every HTTP-level test class that drives the
/// server through <see cref="PluginServerFactory"/>.
///
/// <see cref="PluginServerFactory"/> sets EBS_PLUGINS_DIR, EBS_FILES_DIR and EBS_CONFIG as
/// PROCESS-WIDE environment variables, and <see cref="ServerConfig"/> re-reads them live on
/// every access rather than snapshotting at startup. xUnit runs test classes in parallel by
/// default. Two independent <see cref="PluginServerFactory"/> instances running concurrently
/// would stomp each other's env vars, so a server could end up loading another fixture's
/// plugin/files directory mid-test.
///
/// Any new HTTP-level test class (e.g. for stream routes or a file cache) MUST join this
/// same collection — via <c>[Collection(AddonServerCollection.Name)]</c> and constructor-
/// injecting the shared <see cref="PluginServerFactory"/> — rather than defining its own
/// fixture or collection. Do not remove <c>DisableParallelization</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AddonServerCollection : ICollectionFixture<PluginServerFactory>
{
    public const string Name = "addon-server";
}
