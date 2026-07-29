using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Plugins;

/// <summary>
/// Defers building the real <see cref="ITorrentGrabber"/> until the first search/grab
/// call. Exists to break a construction cycle: plugins register indexers during
/// <c>Configure</c>, but they reach the grabber through <see cref="IServerServices"/>,
/// which the host must hand them AT <c>Configure</c> time — before every plugin (and
/// therefore every indexer) has loaded. A plugin may stash the <see cref="ITorrentGrabber"/>
/// reference during registration; as long as it only calls it while serving a request,
/// the underlying grabber is not built until well after plugin loading has finished, by
/// which point <paramref name="factory"/> sees every indexer.
/// </summary>
public sealed class LazyTorrentGrabber(Func<ITorrentGrabber> factory) : ITorrentGrabber
{
    private readonly Lazy<ITorrentGrabber> _inner = new(factory);

    public Task<GrabResult> GrabAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => _inner.Value.GrabAsync(request, cancellationToken);

    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => _inner.Value.SearchAsync(request, cancellationToken);
}
