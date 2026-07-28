using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server;

/// <summary>
/// Runs one registered source's <see cref="IMediaSource.WarmUpAsync"/> at startup under a
/// bound timeout, so a plugin whose warm-up hangs (not just throws) can never delay the
/// server from listening or hold up every other source's own warm-up. See
/// <see cref="IMediaSource.WarmUpAsync"/>'s doc comment for the guarantee this exists to
/// keep true. <paramref name="timeout"/> is a parameter (rather than always reading
/// <see cref="DefaultTimeout"/> internally) purely so tests can bound it tightly — the
/// running server always calls this with <see cref="DefaultTimeout"/>; there is no
/// per-source configuration surface, and building one is out of scope here.
/// </summary>
internal static class SourceWarmUp
{
    /// <summary>Generous enough to cover a real network probe; short enough that one
    /// hung plugin cannot meaningfully delay startup for everyone else.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static async Task RunAsync(IMediaSource source, string label, ILogger log, TimeSpan timeout)
    {
        try
        {
            var result = await source.WarmUpAsync(CancellationToken.None).WaitAsync(timeout);
            switch (result.Status)
            {
                case WarmUpStatus.Ready:
                    log.LogInformation("Source '{Source}' warmed up.", label);
                    break;
                case WarmUpStatus.Failed:
                    log.LogWarning("Source '{Source}' failed to warm up: {Detail}", label, result.Detail);
                    break;
                case WarmUpStatus.NotApplicable:
                default:
                    break;
            }
        }
        catch (TimeoutException)
        {
            log.LogWarning(
                "Source '{Source}' did not finish warming up within {Seconds}s — continuing without it warmed. " +
                "The attempt keeps running in the background; it is abandoned, not cancelled, because " +
                "WarmUpAsync takes no meaningful cancellation signal of its own here.",
                label, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Source '{Source}' threw during WarmUpAsync — continuing without it warmed.", label);
        }
    }
}
