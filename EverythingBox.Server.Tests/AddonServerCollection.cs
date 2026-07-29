namespace EverythingBox.Server.Tests;

/// <summary>
/// Shared, non-parallel xUnit collection for every test class that touches the EBS_*
/// environment variables — not just HTTP-level tests that drive the server through
/// <see cref="PluginServerFactory"/>, but ANY test class that reads or sets EBS_PLUGINS_DIR,
/// EBS_FILES_DIR or EBS_CONFIG, however indirectly.
///
/// <see cref="PluginServerFactory"/> sets EBS_PLUGINS_DIR, EBS_FILES_DIR and EBS_CONFIG as
/// PROCESS-WIDE environment variables, and <see cref="ServerConfig"/> re-reads them live on
/// every access rather than snapshotting at startup. xUnit runs test classes in parallel by
/// default. Two independent things touching these env vars concurrently — two
/// <see cref="PluginServerFactory"/> instances, or a factory racing some other test that
/// reads/writes the same variables directly — would stomp each other, so a server (or a
/// direct <see cref="ServerConfig"/> read) could end up seeing another fixture's
/// plugin/files directory mid-test.
///
/// Any new test class that touches these env vars — HTTP-level or not — MUST join this
/// same collection — via <c>[Collection(AddonServerCollection.Name)]</c> and constructor-
/// injecting the shared <see cref="PluginServerFactory"/> — rather than defining its own
/// fixture or collection. Do not remove <c>DisableParallelization</c>. As a structural
/// backstop against this being missed, see the assembly-level
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c> in
/// <c>AssemblyInfo.cs</c>, which serialises the whole suite regardless.
/// </summary>
/// <summary>
/// Also provides <see cref="SearchServerFactory"/>, <see cref="BrowseServerFactory"/> and
/// <see cref="SearchOnlyServerFactory"/> as further independent shared fixtures — xUnit
/// constructs one instance per <see cref="ICollectionFixture{TFixture}"/> interface here,
/// so each is a separate host instance that happens to share this collection's non-parallel
/// scheduling, which is exactly what their EBS_* environment-variable writes need.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AddonServerCollection :
    ICollectionFixture<PluginServerFactory>,
    ICollectionFixture<SearchServerFactory>,
    ICollectionFixture<BrowseServerFactory>,
    ICollectionFixture<SearchOnlyServerFactory>,
    ICollectionFixture<FallbackServerFactory>
{
    public const string Name = "addon-server";
}
