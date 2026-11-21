namespace Stratoscope.Core;

/// <summary>Polls one server. Never overlaps two polls and never throws.</summary>
public sealed class ServerMonitor
{
    private readonly IEngineProvider provider;
    private int busy;

    public ServerMonitor(string name, IEngineProvider provider)
    {
        Name = name;
        this.provider = provider;
    }

    public string Name { get; }
    /// <summary>The newest answer; null while the engine is not running (or before the first poll).</summary>
    public EngineSnapshot? Snapshot { get; private set; }
    /// <summary>Why the engine does not answer, in English.</summary>
    public string? Error { get; private set; }
    /// <summary>The rolling speed graph, its longer observation history and the adaptive scales.</summary>
    public SpeedHistory History { get; } = new SpeedHistory();

    /// <summary>True once the first poll has finished.</summary>
    public bool Polled { get; private set; }

    /// <summary>True when the last poll failed with an unexpected exception (a bug or an unforeseen answer), not a plain "nothing answers".</summary>
    public bool Faulted { get; private set; }

    public ServerState State => !Polled ? ServerState.Connecting : Faulted ? ServerState.Error : Display.StateOf(Snapshot);

    /// <summary>Counts polls that change the visible state or carry a new full-poll history sample.</summary>
    public int DataVersion { get; private set; }
    private int lastContent = int.MinValue;

    private int sampling;

    /// <summary>
    /// For direct connections, reads counters between full polls to feed the speed curves. Returns true when the panel has a new sample to paint.
    /// </summary>
    public async Task<bool> SampleAsync(CancellationToken cancellation)
    {
        if (!Polled || Snapshot is null || Interlocked.CompareExchange(ref sampling, 1, 0) != 0) return false;
        try
        {
            var counters = await provider.ReadCountersAsync(cancellation).ConfigureAwait(true);
            return History.AddCounters(DateTime.UtcNow, counters);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
        finally { Interlocked.Exchange(ref sampling, 0); }
    }

    /// <summary>Asks the engine once. Returns false when the previous poll is still running.</summary>
    public async Task<bool> PollAsync(CancellationToken cancellation)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        var cancelled = false;
        try
        {
            Snapshot = await provider.PollAsync(cancellation).ConfigureAwait(true);
            Error = Snapshot == null ? provider.LastError : null;
            Faulted = false;
        }
        catch (OperationCanceledException) { cancelled = true; throw; }
        catch (Exception e)
        {
            Snapshot = null;
            Faulted = true;
            Error = "Unexpected error: " + e.GetType().Name + ": " + e.Message;
        }
        finally
        {
            Polled = true;
            var previousSample = History.LastSampleTime;
            History.Add(DateTime.UtcNow, cancelled ? null : Snapshot);
            var content = HashCode.Combine(Snapshot?.ContentHash() ?? 0, Error, Faulted);
            if (content != lastContent || (!cancelled && Snapshot is { RouterLimited: false }
                && (History.LastSampleTime != previousSample || Snapshot.FullPollCounters != null)))
            {
                lastContent = content;
                DataVersion++;
            }
            Interlocked.Exchange(ref busy, 0);
        }
        return true;
    }
}

/// <summary>Choices that involve several servers.</summary>
public static class Servers
{
    /// <summary>The server whose state the icon shows: the first one that is busy, else the first one that answers, else the first.</summary>
    public static int Primary(IList<ServerMonitor> monitors)
    {
        int firstOnline = -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            var s = monitors[i].Snapshot;
            if (s != null && s.IsBusy) return i;
            if (s != null && firstOnline < 0) firstOnline = i;
        }
        return firstOnline >= 0 ? firstOnline : 0;
    }

    /// <summary>True while at least one engine answers. The Mac app hides its menu bar item otherwise.</summary>
    public static bool AnyReachable(IEnumerable<ServerMonitor> monitors) => monitors.Any(m => m.Snapshot != null);

    /// <summary>True until every server has been asked once, so the icon does not flicker at start.</summary>
    public static bool AllPolled(IEnumerable<ServerMonitor> monitors) => monitors.All(m => m.Polled);

    /// <summary>Keeps a tab selection inside the list.</summary>
    public static int Clamp(int selected, int count) => count <= 0 ? 0 : Math.Max(0, Math.Min(selected, count - 1));

    /// <summary>The tooltip: one part per server, cut to the 127 characters Windows allows.</summary>
    public static string Tooltip(string app, IEnumerable<ServerMonitor> monitors, int limit = 127)
    {
        var parts = monitors.Select(m => m.Name + ": " + Display.Brief(m.Snapshot, m.Polled, m.Faulted));
        var text = app + " | " + string.Join(" | ", parts);
        return text.Length <= limit ? text : text.Substring(0, limit - 1) + "…";
    }
}
