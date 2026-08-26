using System.Net;
using EverythingBox.Server.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EverythingBox.Server.Tests;

/// <summary>
/// What the fetch route does around a source, rather than what the source does: it sweeps, it hands
/// out a staging directory, and only then asks. Asserted over the real host, because both halves are
/// properties of the ROUTE — a source cannot be made to prove them and a unit test of the endpoint
/// delegate would not have the DI that supplies the staging root.
///
/// <para>The sweep is the reason this file exists. Retention was implemented one task before anything
/// wrote a staged file, so nothing called it and nothing noticed: a dead sweep passes every test that
/// only asks whether <see cref="RomhackStaging.Sweep"/> works. It is harmless right up until fetches
/// start leaving gigabyte ROMs behind, which is exactly what the change under it does. The aged
/// directory below is the pin — remove the call and it comes back.</para>
///
/// <para>Sweeping HERE, where a fetch directory is handed out, rather than on a timer: the call site
/// is self-limiting (it runs only when the feature is used), needs no background service, and cleans
/// exactly when new files are about to be created. The consequence, stated rather than papered over:
/// if nobody fetches, nothing is swept — but nothing is being created either, so the only cost is
/// that a previous session's files linger until the next fetch.</para>
///
/// Joins <see cref="AddonServerCollection"/> for the reason every class touching the EBS_*
/// environment variables does — see that collection's doc comment. Like <see cref="RomhackFileRouteTests"/>
/// it builds its own <see cref="StockServerFactory"/>: a no-plugin config is a different config and
/// must not share a host instance with one.
/// </summary>
[Collection(AddonServerCollection.Name)]
public class RomhackFetchStagingTests : IDisposable
{
    private readonly StockServerFactory _stock = new();   // writes a config with Indexers: [], no plugin

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rfs-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly RomhackStaging _staging;

    public RomhackFetchStagingTests() => _staging = new RomhackStaging(_root, TimeSpan.FromHours(6));

    public void Dispose()
    {
        _stock.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_source_is_handed_a_directory_that_already_exists_inside_the_staging_root()
    {
        // The source writes files into what it is handed and mints urls from their paths, so a
        // directory that is outside the root — or that it has to create itself — produces urls the
        // file route will refuse. Handing it in, rather than letting the source pick, is what keeps
        // "where the file is" and "where files are served from" the same answer.
        var source = new RecordingSource();

        var response = await Client(source).GetAsync("/romhack/aaa:1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(source.StagingDirectory);
        Assert.True(source.DirectoryExistedWhenAsked,
                    "the source must be handed a directory it can write into immediately");
        Assert.True(_staging.IsInsideRoot(source.StagingDirectory!));
    }

    [Fact]
    public async Task Two_fetches_are_handed_two_different_directories()
    {
        // One directory per fetch, so two releases that ship a patch under the same name cannot
        // overwrite each other's file — and so a url minted by the first fetch keeps pointing at the
        // bytes that fetch produced.
        var source = new RecordingSource();
        var client = Client(source);

        await client.GetAsync("/romhack/aaa:1");
        var first = source.StagingDirectory;
        await client.GetAsync("/romhack/aaa:2");

        Assert.NotEqual(first, source.StagingDirectory);
    }

    [Fact]
    public async Task An_aged_staging_directory_is_gone_after_a_fetch()
    {
        // The pin on the sweep actually running. Nothing else in the system deletes a staged file:
        // the response that names it is long finished by the time the retention expires, and there
        // is no install confirmation to hang a delete on.
        var aged = _staging.NewFetchDirectory();
        await File.WriteAllBytesAsync(Path.Combine(aged, "old.rom"), [1, 2, 3, 4]);
        Directory.SetLastWriteTimeUtc(aged, DateTime.UtcNow.AddDays(-2));

        await Client(new RecordingSource()).GetAsync("/romhack/aaa:1");

        Assert.False(Directory.Exists(aged));
    }

    [Fact]
    public async Task A_fetch_does_not_sweep_away_a_directory_still_inside_its_retention()
    {
        // The other half, and the one a sweep that ignored the retention would break: a client that
        // is still downloading a file staged a minute ago must not have it deleted out from under it
        // because somebody else started a fetch.
        var recent = _staging.NewFetchDirectory();
        await File.WriteAllBytesAsync(Path.Combine(recent, "busy.rom"), [1, 2, 3, 4]);

        await Client(new RecordingSource()).GetAsync("/romhack/aaa:1");

        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public async Task A_sweep_that_cannot_run_does_not_cost_the_caller_its_fetch()
    {
        // Housekeeping that can fail a request is worse than housekeeping that skips a directory.
        // A staging root that has never been created is the ordinary shape of a server whose first
        // ever romhack fetch this is, so it must not be an incident.
        var missing = new RomhackStaging(Path.Combine(_root, "not-created-yet"), TimeSpan.FromHours(6));
        var source = new RecordingSource();

        var client = _stock.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                        {
                            services.AddSingleton(missing);
                            services.AddSingleton<IReadOnlyList<IRomhackSource>>([source]);
                        }))
                           .CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/romhack/aaa:1")).StatusCode);
        Assert.True(Directory.Exists(source.StagingDirectory!));
    }

    /// <summary>A client onto the stock host with the staging root and the sources both replaced, so
    /// the test owns the directory the route hands out and can supply a source with no plugin on disk
    /// to load one from. The last registration wins, which is what makes these overrides of
    /// Program.cs's own rather than additions beside them.</summary>
    private HttpClient Client(params IRomhackSource[] sources) =>
        _stock.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                   {
                       services.AddSingleton(_staging);
                       services.AddSingleton<IReadOnlyList<IRomhackSource>>(sources);
                   }))
              .CreateClient();

    /// <summary>Records the directory it was handed and claims the id, as the real source does for an
    /// id whose tag it owns.</summary>
    private sealed class RecordingSource : IRomhackSource
    {
        public string? StagingDirectory { get; private set; }
        public bool DirectoryExistedWhenAsked { get; private set; }

        public Task<IReadOnlyList<RomhackInfo>> ListAsync(string systemId, string title, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RomhackInfo>>([]);

        public Task<RomhackPatchSet?> FetchAsync(string id, string stagingDirectory, CancellationToken ct)
        {
            StagingDirectory = stagingDirectory;
            DirectoryExistedWhenAsked = Directory.Exists(stagingDirectory);
            return Task.FromResult<RomhackPatchSet?>(
                new RomhackPatchSet(id, "1.0", null, [new RomhackPatch("hack.ips", "ips", "romhack-file/abc")]));
        }
    }
}
