using System.Diagnostics;
using System.Text.RegularExpressions;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Parsing;
using EverythingBox.Server.Core.Ranking;

namespace EverythingBox.Server.Core;

/// <summary>
/// The top-level entry point of the library. Given a <see cref="MediaRequest"/>
/// it routes the request to every capable provider, merges their results,
/// optionally parses + dedupes them, then asks the ranker for the single best
/// release (with alternatives).
/// <para>
/// Construct via the constructor for DI scenarios, or use <see cref="GrabberBuilder"/>
/// for a fluent setup.
/// </para>
/// </summary>
public sealed class TorrentGrabber : ITorrentGrabber
{
    private readonly IReadOnlyList<ITorrentProvider> _providers;
    private readonly ITorrentRanker _ranker;
    private readonly IReleaseParser _parser;
    private readonly GrabberOptions _options;
    private readonly IDownloadClient? _downloadClient;
    private readonly IDebridService? _debridService;
    private readonly IProviderPerformanceTracker? _providerTracker;

    public TorrentGrabber(
        IEnumerable<ITorrentProvider> providers,
        ITorrentRanker? ranker = null,
        IReleaseParser? parser = null,
        GrabberOptions? options = null,
        IDownloadClient? downloadClient = null,
        IDebridService? debridService = null,
        IProviderPerformanceTracker? providerTracker = null)
    {
        _providers = providers.ToList();
        _ranker = ranker ?? new DefaultTorrentRanker();
        _parser = parser ?? new DefaultReleaseParser();
        _options = options ?? new GrabberOptions();
        _downloadClient = downloadClient;
        _debridService = debridService;
        _providerTracker = providerTracker;
    }

    /// <summary>Search, rank, and return the single best match plus alternatives.</summary>
    public async Task<GrabResult> GrabAsync(
        MediaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_options.QuickGrabScore is { } threshold)
            return await QuickGrabAsync(request, threshold, cancellationToken).ConfigureAwait(false);

        var queryResults = await SearchInternalAsync(request, cancellationToken).ConfigureAwait(false);
        var results = queryResults.SelectMany(q => q.Results).ToList();

        var prepared = Prepare(request, results);
        var ranked = await MarkAndOrderByCacheAsync(_ranker.Rank(request, prepared, _options.Ranking), cancellationToken)
            .ConfigureAwait(false);

        RecordOutcomes(queryResults, ranked.Count > 0 ? ranked[0].Result.ProviderName : null);

        return new GrabResult
        {
            Best = ranked.Count > 0 ? ranked[0].Result : null,
            Ranked = ranked,
            Errors = ErrorsOf(queryResults),
        };
    }

    /// <summary>
    /// Query providers as they finish and return as soon as a candidate reaches
    /// <paramref name="threshold"/>, cancelling the remaining providers. Falls back
    /// to the full ranked set if nothing clears the bar.
    /// </summary>
    private async Task<GrabResult> QuickGrabAsync(MediaRequest request, double threshold, CancellationToken cancellationToken)
    {
        // Best-performing providers first, so the early exit can fire sooner.
        var capable = Prioritize(_providers.Where(p => p.Capabilities.Supports(request.MediaType)).ToList());

        var results = new List<TorrentResult>();
        var completed = new List<QueryResult>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ProviderTimeout > TimeSpan.Zero)
            cts.CancelAfter(_options.ProviderTimeout);

        // When we prefer cached releases and can actually check, a quick-grab stop
        // must land on a cached result — instant availability is the whole point.
        var requireCached = _options.PreferCachedReleases && _debridService is ICachedAvailabilityChecker;

        var tasks = capable.Select(p => QueryProviderAsync(p, request, cts.Token, cancellationToken)).ToList();

        GrabResult? early = null;
        try
        {
            await foreach (var finished in Task.WhenEach(tasks).ConfigureAwait(false))
            {
                var outcome = await finished.ConfigureAwait(false);
                completed.Add(outcome);
                results.AddRange(outcome.Results);

                // Only consult the cache (a network call) once something clears the bar.
                var scoreRanked = _ranker.Rank(request, Prepare(request, results), _options.Ranking);
                if (scoreRanked.Count == 0 || scoreRanked[0].Score < threshold)
                    continue;

                var ranked = await MarkAndOrderByCacheAsync(scoreRanked, cancellationToken).ConfigureAwait(false);
                var pick = ranked.FirstOrDefault(s => s.Score >= threshold && (!requireCached || s.Cached));
                if (pick is not null)
                {
                    cts.Cancel(); // good enough — stop the rest
                    RecordOutcomes(completed, pick.Result.ProviderName);
                    early = new GrabResult { Best = pick.Result, Ranked = ranked, Errors = ErrorsOf(completed) };
                    break;
                }
            }
        }
        finally
        {
            // Let the cancelled in-flight provider requests finish unwinding before
            // the linked CTS is disposed — otherwise aborting them mid-request leaks
            // a spurious TaskCanceledException/IOException.
            await WhenAllSafe(tasks).ConfigureAwait(false);
        }

        if (early is not null)
            return early;

        cancellationToken.ThrowIfCancellationRequested();

        var finalRanked = await MarkAndOrderByCacheAsync(
            _ranker.Rank(request, Prepare(request, results), _options.Ranking), cancellationToken).ConfigureAwait(false);
        RecordOutcomes(completed, finalRanked.Count > 0 ? finalRanked[0].Result.ProviderName : null);
        return new GrabResult
        {
            Best = finalRanked.Count > 0 ? finalRanked[0].Result : null,
            Ranked = finalRanked,
            Errors = ErrorsOf(completed),
        };
    }

    /// <summary>
    /// Mark which ranked results the debrid service already has cached and float
    /// them to the top (cached, then score, then seeders). No-op unless
    /// <see cref="GrabberOptions.PreferCachedReleases"/> is set and the debrid
    /// service implements <see cref="ICachedAvailabilityChecker"/>.
    /// </summary>
    private async Task<IReadOnlyList<ScoredTorrent>> MarkAndOrderByCacheAsync(
        IReadOnlyList<ScoredTorrent> ranked, CancellationToken cancellationToken)
    {
        if (!_options.PreferCachedReleases || _debridService is not ICachedAvailabilityChecker checker || ranked.Count == 0)
            return ranked;

        IReadOnlySet<string> cachedHashes;
        try
        {
            var hashes = ranked.Select(s => InfoHashOf(s.Result)).Where(h => h is not null).Select(h => h!);
            cachedHashes = await checker.GetCachedHashesAsync(hashes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ranked; // availability check failed — keep the score order
        }

        if (cachedHashes.Count == 0)
            return ranked;

        var marked = ranked
            .Select(s => s with { Cached = InfoHashOf(s.Result) is { } h && cachedHashes.Contains(h) })
            .ToList();

        marked.Sort((a, b) =>
        {
            var byCached = b.Cached.CompareTo(a.Cached);
            if (byCached != 0) return byCached;
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : (b.Result.Seeders ?? 0).CompareTo(a.Result.Seeders ?? 0);
        });

        return marked;
    }

    private static string? InfoHashOf(TorrentResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.InfoHash))
            return result.InfoHash.Trim().ToLowerInvariant();
        return result.MagnetUri is not null ? InfoHashFromMagnet(result.MagnetUri)?.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Grab the best release and hand it to the configured download client. The
    /// returned <see cref="DownloadResult"/> reports both what was chosen and the
    /// client's response; <see cref="DownloadResult.Download"/> is null when
    /// nothing matched the request. Requires a download client to have been
    /// configured (constructor or <see cref="GrabberBuilder.UseDownloadClient"/>).
    /// </summary>
    public async Task<DownloadResult> GrabAndDownloadAsync(
        MediaRequest request,
        DownloadOptions? downloadOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_downloadClient is null)
            throw new InvalidOperationException(
                "No download client configured. Provide one via the constructor or GrabberBuilder.UseDownloadClient.");

        var grab = await GrabAsync(request, cancellationToken).ConfigureAwait(false);
        if (!grab.Found)
            return new DownloadResult(grab, null);

        var added = await _downloadClient.AddAsync(grab.Best!, downloadOptions, cancellationToken).ConfigureAwait(false);
        return new DownloadResult(grab, added);
    }

    /// <summary>
    /// Grab the best release and resolve it through the configured debrid service
    /// into direct download links. The returned <see cref="DebridGrabResult"/>
    /// reports both the choice and the debrid outcome; <see cref="DebridGrabResult.Debrid"/>
    /// is null when nothing matched. Requires a debrid service to have been
    /// configured (constructor or <see cref="GrabberBuilder.UseDebridService"/>).
    /// </summary>
    public async Task<DebridGrabResult> GrabAndResolveAsync(
        MediaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_debridService is null)
            throw new InvalidOperationException(
                "No debrid service configured. Provide one via the constructor or GrabberBuilder.UseDebridService.");

        var grab = await GrabAsync(request, cancellationToken).ConfigureAwait(false);
        if (!grab.Found)
            return new DebridGrabResult(grab, null);

        var resolved = await _debridService.ResolveAsync(grab.Best!, request, cancellationToken).ConfigureAwait(false);
        return new DebridGrabResult(grab, resolved);
    }

    /// <summary>Search every capable provider and return the merged raw results.</summary>
    public async Task<IReadOnlyList<TorrentResult>> SearchAsync(
        MediaRequest request,
        CancellationToken cancellationToken = default)
    {
        var queryResults = await SearchInternalAsync(request, cancellationToken).ConfigureAwait(false);
        return Prepare(request, queryResults.SelectMany(q => q.Results).ToList());
    }

    private async Task<List<QueryResult>> SearchInternalAsync(
        MediaRequest request,
        CancellationToken cancellationToken)
    {
        // Best-performing providers first (matters for sequential querying).
        var capable = Prioritize(_providers.Where(p => p.Capabilities.Supports(request.MediaType)).ToList());

        // A linked token lets us impose a per-call timeout on top of whatever the
        // caller passed in. Cancellation from the caller propagates (we rethrow
        // below); cancellation from the timeout is reported as a provider error.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ProviderTimeout > TimeSpan.Zero)
            cts.CancelAfter(_options.ProviderTimeout);
        var token = cts.Token;

        List<QueryResult> queryResults;
        if (_options.QueryProvidersInParallel)
        {
            queryResults = [.. await Task.WhenAll(capable.Select(p => QueryProviderAsync(p, request, token, cancellationToken))).ConfigureAwait(false)];
        }
        else
        {
            queryResults = [];
            foreach (var provider in capable)
                queryResults.Add(await QueryProviderAsync(provider, request, token, cancellationToken).ConfigureAwait(false));
        }

        // If the caller cancelled (vs. a provider timing out), surface it.
        cancellationToken.ThrowIfCancellationRequested();
        return queryResults;
    }

    private async Task<QueryResult> QueryProviderAsync(
        ITorrentProvider provider, MediaRequest request, CancellationToken token, CancellationToken callerToken)
    {
        // provider.Name is plugin-authored code and can throw. Read it once, defensively,
        // before anything else — including before the try below — so every exit out of this
        // method (success, timeout, or a genuine provider exception) uses the same captured,
        // safe value. Reading provider.Name again from inside the catch blocks would let a
        // throwing Name escape unguarded and take down every other provider's results in the
        // same batch, not just this one's.
        var name = SafeProviderName(provider);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var found = await provider.SearchAsync(request, token).ConfigureAwait(false);
            return new QueryResult(name, found, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // Timed out, or stopped early by a quick grab — reported, not fatal.
            return new QueryResult(name, [], "stopped before completing", stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw; // genuine caller cancellation
        }
        catch (Exception ex)
        {
            return new QueryResult(name, [], ex.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Core's own equivalent of the host's PluginDiagnostics.SafeLabel — Core cannot
    /// reference the host, so this is a local, narrower copy of the same idea: a provider's
    /// Name is plugin-authored and can throw, and a diagnostic read of it must never itself
    /// throw. Falls back to the runtime type when Name is unavailable.
    /// </summary>
    private static string SafeProviderName(ITorrentProvider? provider)
    {
        if (provider is null) return "<null provider>";

        try { return provider.Name; }
        catch { return provider.GetType().FullName ?? "<unknown provider>"; }
    }

    private IReadOnlyList<ITorrentProvider> Prioritize(IReadOnlyList<ITorrentProvider> capable)
        => _providerTracker?.Prioritize(capable) ?? capable;

    private void RecordOutcomes(IReadOnlyList<QueryResult> queryResults, string? bestProviderName)
    {
        if (_providerTracker is null)
            return;

        var outcomes = queryResults
            .Select(q => new ProviderOutcome(q.Name, q.Results.Count, q.Error is not null, q.Elapsed, q.Name == bestProviderName))
            .ToList();
        _providerTracker.Record(outcomes);
    }

    private static List<ProviderError> ErrorsOf(IEnumerable<QueryResult> queryResults)
        => queryResults.Where(q => q.Error is not null).Select(q => new ProviderError(q.Name, q.Error!)).ToList();

    /// <summary>Await all tasks, swallowing the cancellations from a quick-grab stop.</summary>
    private static async Task WhenAllSafe(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Provider queries we deliberately cancelled — already accounted for.
        }
    }

    private sealed record QueryResult(string Name, IReadOnlyList<TorrentResult> Results, string? Error, TimeSpan Elapsed);

    private IReadOnlyList<TorrentResult> Prepare(MediaRequest request, List<TorrentResult> results)
    {
        IEnumerable<TorrentResult> prepared = _options.Deduplicate ? Deduplicate(results) : results;

        if (_options.ParseReleases)
        {
            prepared = prepared.Select(r => r.ParsedInfo is not null
                ? r
                : r with { ParsedInfo = _parser.Parse(r.Title, request.MediaType) });
        }

        return prepared.ToList();
    }

    /// <summary>
    /// Collapse the same release reported by multiple providers into one entry,
    /// keeping the most-seeded copy. Identity is by info hash (whether stated
    /// directly or embedded in a magnet), then by download URL, then by a
    /// title+size fallback.
    /// </summary>
    private static IReadOnlyList<TorrentResult> Deduplicate(IEnumerable<TorrentResult> results)
    {
        var best = new Dictionary<string, TorrentResult>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            var key = DeduplicationKey(result);
            if (!best.TryGetValue(key, out var existing) || (result.Seeders ?? 0) > (existing.Seeders ?? 0))
                best[key] = result;
        }

        return best.Values.ToList();
    }

    private static string DeduplicationKey(TorrentResult r)
    {
        var hash = !string.IsNullOrWhiteSpace(r.InfoHash)
            ? r.InfoHash
            : r.MagnetUri is not null ? InfoHashFromMagnet(r.MagnetUri) : null;

        if (!string.IsNullOrWhiteSpace(hash))
            return $"hash:{hash.Trim().ToLowerInvariant()}";
        if (r.MagnetUri is not null)
            return $"magnet:{r.MagnetUri.AbsoluteUri.ToLowerInvariant()}";
        if (r.DownloadUrl is not null)
            return $"dl:{r.DownloadUrl.AbsoluteUri.ToLowerInvariant()}";

        return $"title:{r.Title.Trim().ToLowerInvariant()}|{r.SizeBytes?.ToString() ?? "?"}";
    }

    private static string? InfoHashFromMagnet(Uri magnet)
        => MagnetHash.Match(magnet.OriginalString) is { Success: true } m ? m.Groups[1].Value : null;

    private static readonly Regex MagnetHash =
        new(@"urn:btih:([A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
