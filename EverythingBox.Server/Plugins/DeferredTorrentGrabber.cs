using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Plugins;

/// <summary>
/// Late-bound <see cref="ITorrentGrabber"/> that breaks a real construction cycle: plugins
/// register indexers during <c>Configure</c>, but they reach the grabber through
/// <see cref="IServerServices"/>, which the host must hand them AT <c>Configure</c> time —
/// before every plugin (and therefore every indexer) has loaded.
/// <para>
/// The host constructs exactly one of these, hands it to every plugin's
/// <see cref="PluginContext"/> during loading, then calls <see cref="SetGrabber"/> exactly
/// once — after <see cref="PluginHost.Load"/> returns, once every indexer is known. A plugin
/// may stash the <see cref="ITorrentGrabber"/> reference during registration and call it
/// later, while serving a request; see <see cref="IServerServices.Grabber"/> for the rule
/// stated where a plugin author will actually see it.
/// </para>
/// <para>
/// This deliberately does NOT build-and-cache a grabber the first time something calls it
/// (the way a naive <c>Lazy&lt;T&gt;</c>-over-a-not-yet-populated-indexer-list would). That
/// approach silently poisons the grabber forever the moment any plugin calls
/// <c>Server.Grabber</c> from its own <c>Configure</c> — it builds and permanently caches a
/// grabber with zero indexers, and every later request from every plugin silently gets
/// nothing back, with no exception and no log. Calling this too early is a plugin bug, so it
/// fails loudly instead: an <see cref="InvalidOperationException"/> right at the call that
/// made the mistake. Calling <see cref="SetGrabber"/> twice is a host bug, and also throws.
/// </para>
/// </summary>
public sealed class DeferredTorrentGrabber : ITorrentGrabber
{
    private ITorrentGrabber? _grabber;

    /// <summary>
    /// Bind the real grabber. Must be called exactly once, after every plugin has finished
    /// registering its indexers (i.e. after <see cref="PluginHost.Load"/> returns). A second
    /// call is a host bug, not a plugin one, and throws.
    /// </summary>
    public void SetGrabber(ITorrentGrabber grabber)
    {
        ArgumentNullException.ThrowIfNull(grabber);

        if (Interlocked.CompareExchange(ref _grabber, grabber, null) is not null)
            throw new InvalidOperationException(
                "DeferredTorrentGrabber.SetGrabber was already called. The host must set the real " +
                "grabber exactly once, after PluginHost.Load returns — calling it a second time is a " +
                "host bug, not a plugin one.");
    }

    public Task<GrabResult> GrabAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => Grabber.GrabAsync(request, cancellationToken);

    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => Grabber.SearchAsync(request, cancellationToken);

    // A plain Volatile.Read, not a lock: once SetGrabber has run, every later call on any
    // thread is ordinary delegation on the hot path, with no per-call synchronization.
    private ITorrentGrabber Grabber => Volatile.Read(ref _grabber) ?? throw new InvalidOperationException(
        "The grabber is not available during plugin registration: indexers are still being collected " +
        "while every plugin's Configure runs, so no real grabber exists yet. Hold onto this " +
        "ITorrentGrabber reference on your IPluginContext.Server and call it later, while serving a " +
        "request — never from inside Configure.");
}
