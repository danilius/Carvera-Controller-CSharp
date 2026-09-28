namespace Carvera.Core.Connection;

/// <summary>How persistently to connect at start-up: <see cref="Retries"/> further attempts, <see cref="Interval"/> apart.</summary>
public sealed record AutoConnectPolicy(int Retries, TimeSpan Interval);

public enum AutoConnectResult
{
    Connected,
    /// <summary>Every attempt failed.</summary>
    GaveUp,
    /// <summary>Someone else connected, or started connecting, so there was nothing left to do.</summary>
    Superseded,
    /// <summary>There is nothing to connect to yet (no saved address, or a simulator).</summary>
    NothingToConnect,
    Cancelled,
}

/// <summary>
/// Connects to the last-used machine when the program starts, retrying a configurable number of times at a
/// configurable interval. The policy is read afresh before every wait, so changes in settings apply at once.
/// Gives up quietly as soon as a connection exists or is being made by someone else (for instance the user pressing Connect).
/// </summary>
public sealed class AutoConnector(
    Func<AutoConnectPolicy> policy,
    Func<bool> hasTarget,
    Func<bool> connectionBusy,
    Func<CancellationToken, Task<bool>> connect,
    Action<string> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task<AutoConnectResult> RunAsync(CancellationToken token)
    {
        try
        {
            var attempt = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (connectionBusy()) return AutoConnectResult.Superseded;
                if (!hasTarget())
                {
                    log("Auto-connect: no machine address saved yet, so nothing to connect to.");
                    return AutoConnectResult.NothingToConnect;
                }
                attempt++;
                if (await connect(token).ConfigureAwait(false)) return AutoConnectResult.Connected;
                var current = policy();
                var total = Math.Max(0, current.Retries) + 1;
                if (attempt >= total)
                {
                    log($"Auto-connect: gave up after {attempt} attempt{(attempt == 1 ? "" : "s")}. Use Connect to try again.");
                    return AutoConnectResult.GaveUp;
                }
                var wait = current.Interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : current.Interval;
                log($"Auto-connect: attempt {attempt} of {total} failed; trying again in {wait.TotalSeconds:0.#} s.");
                await (delay ?? Task.Delay)(wait, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return AutoConnectResult.Cancelled;
        }
    }
}
